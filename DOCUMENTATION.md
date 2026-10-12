# Semantic Agent Broker - Documentation

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [ReAct Execution Engine](#react-execution-engine)
4. [Authentication and RBAC](#authentication-and-rbac)
5. [Getting Started](#getting-started)
6. [Using the UI](#using-the-ui)
7. [API Reference](#api-reference)
8. [SignalR Real-Time API](#signalr-real-time-api)
9. [Plugin Development Guide](#plugin-development-guide)
10. [Creating Custom Agents](#creating-custom-agents)
11. [Configuration Reference](#configuration-reference)
12. [Client Hosting and Multi-Server Deployment](#client-hosting-and-multi-server-deployment)
13. [Advanced Features](#advanced-features)
14. [Built-in Plugins Reference](#built-in-plugins-reference)
15. [Mock Data Plugins Reference](#mock-data-plugins-reference)
16. [Example Scenarios](#example-scenarios)
17. [Testing and Quality Assurance](#testing-and-quality-assurance)
18. [License & Attribution](#license--attribution)

---

## Overview

Semantic Agent Broker is a dynamic, plugin-based AI agent framework built on [Semantic Kernel](https://github.com/microsoft/semantic-kernel). It enables you to:

- **Define custom agents** via the REST API - configure custom system instructions and curated tool bindings programmatically
- **Load plugins** from DLLs at runtime - drop a `.dll` in the `Plugins` folder (auto-detected on startup, watched via file system watcher, or reloaded on demand via API)
- **Execute tasks** using two execution modes: autonomous (`Auto`) or user-reviewed (`Planned`)
- **Monitor execution** in real-time via SignalR-powered live logs and chat view
- **Manage workspace files** - upload, download, view (with Markdown preview), and edit files in the browser
- **Define plugin tasks** - constrained, reusable task definitions with curated tool subsets and custom system instructions

### Key Concepts

| Concept | Description |
|---------|-------------|
| **Plugin** | A DLL containing one or more tools (methods) that agents can use |
| **Method** | A single callable tool within a plugin (e.g., `ExecutePythonScript`) |
| **Plugin Task** | A constrained task definition within a plugin with specific allowed methods and system instructions |
| **Agent** | A configuration combining a system prompt with selected plugin methods |
| **Task** | A single execution of an agent with a user prompt |
| **Workspace** | An isolated directory per task where files are stored and shared with the Python sandbox |
| **Step** | One iteration of the ReAct loop: reason → act → observe |

---

## Architecture

```
┌────────────────────────────────────────────────────────────────────────┐
│                        Blazor WASM Client (SPA)                        │
│   Dashboard │ Task Queue │ Live Execution │ Plugins │ Users & Groups   │
└───────────────────────┬──────────────────────────────┬─────────────────┘
                        │                              │
    HTTP GET Assets     │                              │ Direct REST API &
    & appsettings.json  │                              │ SignalR WebSockets
┌───────────────────────▼─────────┐             ┌───────▼─────────────────┐
│  Server A: ClientHost (Kestrel) │             │  Server B: Api Backend  │
│  - Static WASM & DLL assets     │             │  - Agent Task Queue     │
│  - Dynamic ApiBaseUrl delivery  │             │  - ReAct Execution      │
│  - Compression (Brotli/Gzip)    │             │  - SignalR LogHub       │
│  - Security Headers & TLS       │             │  - Databases & Plugins  │
└─────────────────────────────────┘             └─────────────────────────┘
```

### Projects

| Project | Purpose |
|---------|---------|
| `SemanticAgentBroker.Sdk` | Plugin contracts - reference this from your plugin |
| `SemanticAgentBroker.Core` | Task state management, plugin host, SK integration, token counting |
| `SemanticAgentBroker.Api` | REST API, SignalR hub, ReAct engine, database, workspace endpoints |
| `SemanticAgentBroker.Client` | Blazor WASM frontend application |
| `SemanticAgentBroker.ClientHost` | Standalone ASP.NET Core Kestrel host for Blazor WASM (multi-server deployments) |
| `SemanticAgentBroker.Shared` | Domain models, config classes, entities |
| `SemanticAgentBroker.Plugins.BuiltIn.Required` | Core plugins automatically injected into every agent session |
| `SemanticAgentBroker.Plugins.BuiltIn.Optional` | Optional plugins that must be explicitly bound to agents |
| `SemanticAgentBroker.Plugins.MockData` | Mock data plugins for testing and demos |
| `SemanticAgentBroker.UnitTests` | Comprehensive MSTest unit & integration test suite |

---

## ReAct Execution Engine

The execution engine implements a Reasoning-Acting (ReAct) loop with a two-phase LLM interaction architecture. This section describes the internal flow in detail.

### Orchestrator Loop (`ReactExecutionEngine`)

The `ReactExecutionEngine.ExecuteAsync` method is the main orchestrator. Its lifecycle:

1. **Initialize** - Resolve task state, determine step budget (unbounded by default unless constrained by client request or plugin task definition via `maxSteps`), set up semaphore ownership tracking

2. **Loop** (unbounded by default, or up to `maxSteps` when specified):
   
   a. **Check user interruptions** - If a user interruption is queued via `SendInterruption`, the message is captured into `_task_state.json` and `_task_state.md` under `UserDirectives`, recorded as a non-error step, and exempted from consuming the agent's execution step quota (`stepsExecutedInRun`). The loop continues immediately to inject the directive into the next reasoning step
   
   b. **Build context** - `TaskStateManager.BuildContextInjectionAsync` produces a tiered state snapshot, dynamically calculating token budgets while reserving guaranteed completion tokens (`outputReserve = Math.Min(_aiOptions.MaxTokensPerRequest, maxContextTokens / 2)`) for model reasoning output. Active user directives are placed prominently under `## 🚨 Active User Directives` at the top of the context and are never collapsed
   
   c. **Reason** - `SubagentExecutor.ExecuteStepAsync` returns structured output (thought, plan, actions, final answer). When the model returns both executable tool actions and a `FinalAnswer`, valid actions are prioritized and executed first, deferring `FinalAnswer` until tool results are observed
   
   d. **Runtime plan deduplication** - If the model returns an updated `Plan:`, the engine compares it to the latest persisted plan version; identical plans are not re-saved to the database, re-broadcast over SignalR, or re-written to `_runtime_plan.md`, preventing version inflation and redundant disk I/O
   
   e. **Inferred action safety & loop detection** - Inferred tool actions (`IsInferred: true`) do not reset reasoning loop counters (`consecutivePlanUpdates`), and if the model emits consecutive repeated inferred actions without progress, a warning is logged and the loop terminates safely to prevent runaway cycles
   
   f. **Check termination** - If `FinalAnswer` is present (without deferred actions), anchor workspace references (`TryAnchorResultFromWorkspaceAsync`), finalize state (`FinalizeStateAsync`), and return
   
   g. **Partition actions** - Split into safe and dangerous tool calls
   
   h. **Execute safe actions** - Run in parallel via `Task.WhenAll`, collecting observations tagged with their original declaration index
   
   i. **Execute dangerous actions** - Release semaphore → prompt user approval → re-acquire semaphore → execute sequentially
   
   j. **Record observations** - Update state and chat message logs in the original declared order, correlating results by `(StepNumber, Action)` pair and `ActionInput`
   
   k. **Advance step counter** - Increments reasoning cycle counter (`step`) per ReAct turn (1, 2, ...), executing all approved batch actions within the step without artificially consuming future reasoning turns

3. **Finalize** - If loop exhausts without `FinalAnswer` or encounters an unhandled error, finalize state (`FinalizeStateAsync`) and return a failure message
- **Cancellation Token Propagation**: `ReactExecutionEngine`, `SubagentExecutor`, `LlmRepairJsonAsync`, and `ToolExecutionService` check for cancellation requests and rethrow `OperationCanceledException` immediately, ensuring external task cancellations abort loops cleanly without being swallowed as step or tool errors.
- **Terminal Result Anchoring**: `TryAnchorResultFromWorkspaceAsync` executes prior to `FinalizeStateAsync` across normal completion. Anchoring strictly rejects path traversal (`..`), empty or whitespace-only files, and internal framework files prefixed with `_` (`_task_state.md`, `_runtime_plan.md`, `_walkthrough.md`), and supports both markdown (`.md`) and text (`.txt`) reports. Bare file path detection is constrained to concise final answer texts (< 500 characters) to avoid false matches on long outputs.
- **Actionable Tool Failure Guidance**: When an agent attempts to invoke a non-existent plugin or function, `ToolExecutionService` returns actionable diagnostic feedback listing all available plugins or available functions within the target plugin, guiding the model to self-correct its tool selection.

### Two-Phase Subagent Architecture (`SubagentExecutor`)

Each step in the ReAct loop uses a two-phase LLM call:

**Phase 1 - Reasoning (no tools)**
- Sends the system prompt + structured state injection to the LLM with tool calling **disabled**
- The LLM produces free-form text containing structured tokens:
  - `Thought:` - reasoning about the current state
  - `Plan:` - implementation plan (optional)
  - `Action:` / `ActionInput:` - tool name and JSON arguments (repeatable for multi-action steps)
  - `FinalAnswer:` - terminal output
- Supports **multi-action parsing**: multiple `Action:`/`ActionInput:` pairs in a single response are extracted and executed in parallel in the same step
- Supports **markdown & code span sanitization**: actions and tool names formatted with backticks or markdown code spans (e.g. `Action:` `Plugin-Function` or `` `Action: Plugin-Function` ``) are automatically recognized by `SubagentResponseParser` and sanitized by `ToolExecutionService`, preventing parser mismatches or plugin lookup failures
- **Hallucinated Observation Cutoff**: Hallucinated `Observation:` blocks appearing after `Action:` in model responses are automatically truncated at parse time by `SubagentResponseParser`, preventing hallucinated observations from corrupting tool arguments or subsequent parsing
- **Word-Boundary and Past-Tense Intent Detection**: `SubagentResponseParser.ExtractToolNameFromText` uses word boundaries (`\b(will use|...|call)\b`) and past-tense negation (`called`, `already`, `previously`, `ran`, `used`) to avoid false-positive tool calls when the model reflects on past actions (e.g., "I called method1")
- **FinalAnswer Protection & Standalone Token Boundaries**: Fallback tool extraction is strictly suppressed whenever a valid `FinalAnswer` is present. Additionally, `SubagentResponseParser` enforces boundary checks ensuring tool names embedded in file paths, URLs, or compound identifiers (e.g. `/app/workdir/PythonSandbox-ExecutePythonScript_output.md`) are never mistakenly treated as tool invocations

**Phase 2 - Fallback (native tool calling & text extraction)**
- Only activates if Phase 1 fails (e.g., model produces unparseable output or context limit is hit)
- Broadcasts reasoning thought via SignalR (`_taskStore.BroadcastThoughtAsync`) before executing fallback tool calls
- Uses Semantic Kernel's native tool calling with tools enabled
- When native function calling returns no tool calls (e.g. for models that do not support native tool calling), extracts text-format `Action:` and `ActionInput:` declarations from the response content via `SubagentResponseParser.ExtractAllActionPairs`
- Includes retry logic for format correction

**Non-Transient LLM Error Fast-Path (`LlmCallService`)**:
- Immediately aborts retries without wasting retry attempts on permanent client HTTP errors (400 Bad Request, 401 Unauthorized, 403 Forbidden, 404 Not Found, 413 Payload Too Large, 422 Unprocessable Entity)

**JSON Self-Repair Pipeline:**
1. `JsonSanitizer.TrySanitize` - Progressive fast-path repair:
   - Early valid JSON bypass (returns valid JSON immediately without applying mutating normalization steps that could alter valid payload string contents or embedded code)
   - Conversational preamble/postamble payload extraction with candidate evaluation (evaluates `{...}` and `[...]` candidates when conversational brackets like `[Step 1]` or `[opt1, opt2]` precede object braces, cleanly selecting the structured payload while preserving array and object semantics)
   - Markdown code fence stripping (` ```json ... ``` ` or ` ``` ... ``` ` wrapping tool inputs and payloads)
   - Comment stripping (`// ...` single-line, `/* ... */` multi-line, and Python/shell `# ...` hash comments outside strings, protecting `http://` and `https://` URLs and `#` in hex colors/strings)
   - In-body semicolon normalization and ellipsis truncation marker stripping (converts in-body `;` separating properties to `,`, trims trailing `;`, and strips `...` and horizontal ellipsis `…` outside strings)
   - Unicode smart/curly quote normalization (`“`, `”`, `‘`, `’`, `«`, `»`, `„` → standard ASCII quotes)
   - JavaScript and TypeScript backtick template string normalization (converting ``` `...` ``` outside double/single quotes to standard double-quoted JSON strings with escaped quotes and control characters)
   - Python set literal normalization (repairing colon-free `{...}` collections outside strings into JSON arrays `[...]`, with string-literal tracking to protect embedded scripts, dictionaries, and f-strings)
   - Python multiline triple-quoted string normalization (`"""..."""` and `'''...'''` wrapping values converted to standard double quotes with inner quote escaping)
   - Language string prefix stripping (`r"..."`, `@"..."`, `b"..."`, `f"..."`, `$"..."` outside strings stripped to standard quotes)
   - Key-value assignment equals normalization (repairing `"key" = val` and `key = val` outside strings into standard JSON colon pairs `"key": val`)
   - Python tuple conversion (repairing `(...)` outside strings into JSON arrays `[...]`)
   - String concatenation merging (adjacent double-quoted strings joined with `+` outside strings merged into a single string literal)
   - Single-quoted JSON repair (`{'key': 'val'}` → `{"key": "val"}`) with escaped apostrophe preservation (`\'` → `'`) and English contraction apostrophe detection (`it's`, `don't`)
   - Python, SQL, and system literal normalization (`True`/`TRUE` → `true`, `False`/`FALSE` → `false`, `None`/`NULL`/`Null`/`NONE`/`nil`/`Nil`/`NIL`/`undefined` → `null`, signed and unsigned `NaN`/`nan` (`-NaN`, `+NaN`), `±Infinity`, and `±inf`/`inf` → `null` outside strings)
   - Unquoted object keys quotation (JSON5 / JavaScript bare keys, dotted keys like `user.name:`, schema `@` prefixes like `@type:`, `@id:`, and newline key boundaries across missing commas)
   - Numeric readability underscore separators removal (`1_000_000` → `1000000`, `0xFF_FF` → `0xFFFF` outside strings)
   - Non-decimal numbers normalization (binary `0b...`, octal `0o...`, and hexadecimal `0x...` with optional `+`/`-` signs → decimal integers outside strings, e.g. `0o755` → `493`, `0b1010` → `10`, `+0xFF` → `255`)
   - Float decimal point normalization (leading `.5` → `0.5`, trailing `5.` → `5.0`, and scientific notation `.5e2` → `0.5e2`, `-.5e-3` → `-0.5e-3` outside strings)
   - RFC 8259 leading zero integer normalization (`007` → `7`, `09` → `9`, `-012` → `-12`, `00` → `0` outside strings, preserving `0.5` and standalone `0`)
   - Unquoted string values, URLs, and file paths quotation in objects and arrays with newline lookahead (`"status": active` → `"status": "active"`, `[active, pending]` → `["active", "pending"]`, `{"url": https://api.github.com}` → `{"url": "https://api.github.com"}`, `{"path": C:\Users\data.csv}` → `{"path": "C:\\Users\\data.csv"}`, `{"path": /app/workdir/test.txt}` → `{"path": "/app/workdir/test.txt"}`)
   - RFC 8259 leading plus-sign number normalization (`+10`, `+0.75` → `10`, `0.75` outside strings, and leading-dot signed floats `+.5`, `+.5e2` → `0.5`, `0.5e2`)
   - 2-digit hex escape normalization (`\xHH` inside double-quoted strings converted to RFC 8259-compliant 4-digit `\u00HH` unicode escapes)
   - Invalid JSON escape sequence, unicode escape validation, and trailing backslash cleanup (`\'` → `'` in double-quoted strings; 4-hex-digit validation for `\u` ensuring paths like `C:\users\admin\utils` escape non-hex `\u` to `\\u`; and odd trailing backslashes before closing quotes `"path": "C:\test\"}` → `"path": "C:\test\\\"}`)
   - Missing comma insertion across both multi-line and compact single-line JSON (automatically inserts `,` between adjacent properties and array elements separated by whitespace or newlines)
   - Control character and URL-encoded sequence escaping
   - String-boundary-aware trailing comma removal and consecutive comma collapsing (`[1,, 2]` → `[1, 2]`, protecting string literals containing `,]` or `,}`)
   - Bracket balancing with LIFO delimiter stack, nested unclosed container auto-closing, unclosed string literal termination, and comma-truncated stream trimming
   - **Depth-aware value and array quote repair**: Distinguishes keys from string values, recognizing that a JSON value string is terminated only by a comma preceding a subsequent property key/array element/brace, newline preceding a property key, closing brace, bracket, or EOF, safely escaping interior quotes preceding commas in multi-key objects (`echo "hello", "world"`) and array elements (`["echo "foo", "bar""]`)
   - **Single-key code fast path**: Automatically repairs unescaped inner quotes for single-parameter payloads (`script`, `content`, `pattern`, `searchText`)
   - **Heuristic key-value extraction**: Regex fallback supporting hyphenated, dotted, or unquoted property names operating on sanitized JSON input
1. LLM-based repair - If fast-path repair fails, a dedicated 30-second LLM call attempts to reconstruct valid JSON. Respects cancellation tokens and immediately rethrows `OperationCanceledException` upon cancellation without masking it as a format failure
1. Raw fallback - If all repair stages fail, the raw input is passed through to tool execution

### Task State Management (`TaskStateManager`)

The state manager maintains a deterministic context snapshot without relying on LLM conversation history. Key behaviors:

- **Tiered observation display**: Very old steps are collapsed into a range summary (e.g., "Steps 1-15: earlier work"). Medium-age steps show truncated summaries. Recent steps include full tool inputs/outputs.
- **Active User Directives**: Mid-task user interruptions submitted via `SendInterruption` are recorded into `UserDirectives` in both `_task_state.json` and `_task_state.md`. In `BuildContextInjectionAsync`, these are rendered prominently under `## 🚨 Active User Directives` at the top of the context snapshot and are guaranteed to never be collapsed or truncated, taking precedence over earlier instructions.
- **Dynamic Plan Supersession**: When a runtime plan (`_runtime_plan.md`) is active, it is injected under `## Active Runtime Plan` with explicit guidance `(This is YOUR current active plan, superseding the baseline plan. Execute it step by step.)`, establishing runtime adaptations as the authoritative plan while preserving the original approved implementation plan.
- **Accurate Multi-Action Step Status Correlation & Failure Detection**: In `RecordStepAsync`, comprehensive error classification detects `Error:`, `Warning/Error output:`, `Traceback (most recent call last):`, `SyntaxError:`, `IndentationError:`, and execution exit code failures, ensuring `(Failed)` and `(Succeeded)` labels in injected context accurately reflect each individual tool execution's real outcome rather than misclassifying crashed Python scripts as successful. In `BuildContextInjectionAsync`, observations are correlated by `(StepNumber, Action)` pair and `ActionInput`.
- **Token budget enforcement**: Observations are trimmed from oldest to newest when the total exceeds `MaxStateInjectionTokens`. Trimming operates on the data model (removing entries from the observation list) and rebuilds the context string structurally.
- **Workspace manifest**: Active files are listed in the injected context so the agent knows what's available.
- **Plan injection**: The active implementation plan is included in the context with a character limit.
- **Cross-Scope Lock Synchronization**: Employs static ref-counted concurrency semaphores (`_sessionLocks`) across all DI scopes to guarantee thread-safe read/write operations between API controllers and background tasks.
- **Hierarchical Workspace Lock Management & Exception-Resilient Semaphore Release**: `WorkspaceLockManager` enforces strict hierarchical synchronization between whole-workspace exclusive locks and individual file-level locks without inversion deadlocks. When acquiring an exclusive workspace lock, `AcquireWorkspaceLockAsync` sets an atomic `IsWorkspaceLocked` flag to prevent new file locks, waits for active file locks to drain without holding the workspace semaphore, and releases file waiters cleanly using `WorkspaceLockReleasedTcs`. Furthermore, both workspace and file lock acquisition pathways track semaphore acquisition state (`wsSemaphoreAcquired`, `fileSemaphoreAcquired`) and verify cancellation tokens post-acquisition, ensuring that any cancellation or runtime exception immediately releases acquired semaphores and clears lock state, eliminating semaphore exhaustion and session deadlock.
- **Integrated Workspace File Synchronization**: File write operations (`_runtime_plan.md`, `final_result.md`, `_implementation_plan.md`, and large tool outputs) and file parameter auto-loading (`@/app/workdir/...`) in `ReactExecutionEngine`, `PlanningEngine`, `ToolExecutionService`, and `PluginMethodBase.SaveLargeResultAsync` acquire explicit file locks via `IWorkspaceLockManager`, ensuring full concurrency safety across simultaneous agent actions and background tasks.
- **Session cleanup with Deferred Disposal**: `CleanupSession(Guid)` uses an active reference-counting mechanism paired with a `CleanupRequested` flag. When a session is purged while in-flight asynchronous operations hold references or await acquisition, semaphores are deferred from disposal until the reference count reaches zero. In-flight acquisition threads that encounter cleaned-up sessions cleanly transition to `OperationCanceledException` without `ObjectDisposedException` crashes.

### Semaphore & Concurrency Model

The system uses a `SemaphoreSlim` to enforce `MaxConcurrentAgents` concurrent task slots:

- **`SemaphoreOwnershipState`** - A standalone thread-safe state object tracking whether the engine currently holds the semaphore slot. State transitions (`TryRelease()` and `TryAcquire()`) use atomic `Interlocked.CompareExchange` with memory barriers (`Volatile.Read`/`Volatile.Write`), preventing race conditions when tasks suspend and resume across thread pool worker threads.
- **Batch release pattern** - For dangerous actions, the semaphore is released once per batch (not per action), the user is prompted for approval, and it is re-acquired once the user responds.
- **Isolated Step Approval Lifecycle** - `TaskOperationsService.PrepareApprovalGate` allocates a dedicated, uncompleted `TaskCompletionSource` per approval event before broadcasting `AwaitingApproval` to the UI, ensuring that premature client clicks or network retries never bypass user consent for subsequent dangerous operations.
- **Deadlock-Free Plan Decision Synchronization** - `TaskOperationsService.ClearPlanDecisions` flushes stale channel items immediately *before* broadcasting the plan review event to the UI, ensuring fast user responses are never dropped by subsequent `WaitForPlanDecisionAsync` invocations.
- **Cancellation safety** - If cancellation fires between release and re-acquire, `IsHeld` is `false` and the `finally` block in `AgentTaskQueueService` safely skips double-releasing the semaphore.
- **Queued Task Pause-Resume Synchronization & Continuous In-Flight Tracking** - In-flight tracking in `AgentTaskQueueService` (`IsTaskQueued`, `IsTaskExecuting`, `IsTaskInFlight`) transitions tasks from queued to active by registering them into active tasks *before* removing them from queued tasks, eliminating TOCTOU false-negative windows where concurrent API calls could perceive an in-flight task as inactive and dispatch duplicate executions. Resuming a queued task resolves and removes its in-memory pause gate in `TaskOperationsService` and restores its state without duplicate re-enqueueing.
- **Per-Tool DI Scope Isolation for Multi-Action Execution** - When executing multiple approved actions concurrently via `Task.WhenAll`, `ReactExecutionEngine` dynamically creates an isolated `IServiceScope` per tool execution task, resolving an independent `ToolExecutionService` instance with its own scoped `AppDbContext`. This guarantees complete thread safety for EF Core database operations while preserving parallel tool execution performance.
- **Atomic Terminal State Guard in Task Cleanup** - `AgentTaskStore.DeleteTaskWithCleanupAsync` atomically verifies that an existing task session remains in a terminal state (`Completed`, `Failed`, `Cancelled`) before deleting database records and purging workspaces, preventing TOCTOU races where restarted active tasks are obliterated during background retention sweeps.
- **Atomic Throttling and Bounded Container Termination** - In `PythonSandboxAgentPlugin`, container cleanup sweeps use atomic 64-bit tick timestamps (`Interlocked.Read`/`Interlocked.Exchange`) with double-checked locking inside static semaphores to eliminate torn reads on all platforms and avoid redundant container enumeration sweeps. In addition, container timeouts enforce a bounded 10-second cancellation token during `KillContainerAsync`, ensuring unresponsive Docker daemons cannot block agent loops indefinitely.
- **Deadlock-Free Synchronous Plugin Unloading** - `PluginLoader.UnloadPlugin` offloads asynchronous unloading tasks onto thread pool workers via `Task.Run`, preventing synchronization context capture and thread pool starvation deadlocks when called synchronously.
- **Planning Failure State Attribution** - If plan generation fails or the implementation plan is rejected by the user/system, `AgentTaskQueueService` attributes the session state directly to `TaskState.Failed` with explicit failure logs in `AgentTaskStore`, releasing concurrency slots cleanly and preventing ambiguous intermediate states.
- **Session lock lifecycle** - In `TaskStateManager`, `AcquireSessionLockAsync` returns a disposable scope tracking active callers, ensuring that `CleanupSession(Guid)` preserves mutual exclusion across trailing asynchronous operations and only purges the session lock from memory once the active reference count drops to zero.
- **Host Teardown Deadlock Prevention & Bounded Shutdown** - In `AgentTaskQueueService`, worker dispatch offloads tasks to `Task.Run` without passing the host cancellation token to the scheduler invocation itself, eliminating task-rejection failure modes where ThreadPool scheduling skips delegate execution and leaves completion sources unresolved. Furthermore, `AgentTaskQueueService.ExecuteAsync` enforces a bounded 10-second timeout on `Task.WhenAll(_activeTasks.Values)`, guaranteeing that lingering tasks or external service delays never deadlock application host shutdown.
- **Teardown-Aware Execution Abort on Semaphore Disposal** - When re-acquiring `_executionSemaphore` following plan reviews, dangerous tool approval gates, or user pause periods, `PlanningEngine`, `ReactExecutionEngine`, and `AgentTaskQueueService` catch `ObjectDisposedException` (triggered when the host disposes synchronization semaphores during process shutdown) and immediately abort execution via `OperationCanceledException`. This ensures agents never execute reasoning steps, tool actions, or LLM requests during host teardown or without holding an authorized execution permit.
- **Superseded and Cancelled Queued Request Invalidation** - `AgentTaskQueueService` tracks per-task request dispatch tokens (`RequestId`) in `_latestRequestIds`. When a task is restarted, proceeded, or cancelled while earlier requests remain queued in the channel, `CancelQueuedTask` removes the tracking entry. When dequeued by the background worker (and prior to execution start or post-pause acquisition), requests whose `RequestId` does not match the active entry or whose entry was removed via cancellation are skipped cleanly (`!_latestRequestIds.TryGetValue(taskId, out var latestReqId) || latestReqId != request.RequestId`), preventing stale, superseded, or cancelled tasks from executing or blocking new user requests.
- **Unique Workspace File Naming for Parallel Tool Output Auto-Save** - In `ToolExecutionService`, auto-saved large tool results include a unique short GUID discriminator in the generated filename (`{safeActionName}_step{step}_{uniqueDiscriminator}_output{extension}`). This eliminates TOCTOU file collision races where parallel tool executions in the same reasoning step evaluate `File.Exists` concurrently, guaranteeing each parallel tool action writes to its own distinct workspace file without overwriting concurrent outputs.
- **Synchronized Blazor UI Collection Reads & Atomic Render Debouncing** - In `LiveExecution.razor.cs`, debounced UI timer rendering reads `.Count` on `chatMessages` and `logs` strictly within `lock (_chatLock)` and `lock (_logLock)` scopes, preventing concurrent modification data races between ThreadPool timer callbacks and incoming SignalR streaming handlers. Furthermore, render debouncing uses atomic `Interlocked.CompareExchange` and `Interlocked.Exchange` operations on `_isStateDirty` across concurrent SignalR streaming callbacks and ThreadPool timer invocations, preventing missed renders from CPU cache incoherence.

---

## Authentication and RBAC

Semantic Agent Broker includes a secure, server-side authentication system with Role-Based Access Control (RBAC).

### Default Admin Account

Upon the first launch, the system automatically creates a default Admin account using credentials defined in `appsettings.json`:
- **Default Username:** `admin`
- **Default Password:** `admin` (Change this in production)

### User Roles

There are two primary roles:
- **Admin**: Has unrestricted access to all plugins, tasks, agents, and system configurations. Admins can access the **Plugins** and **Users** tabs in the UI.
- **User**: Has restricted access. They can only see and execute specific tasks granted to them by an Admin.

### Groups & Granular Permissions

Users can be organized into groups. Admins can assign granular permissions to users or groups for specific tasks (Plugin + Task combinations). Permissions dictate what the user can do during execution:
- **AutoExecution**: Allows the user to run tasks in `Auto` mode. If disabled, users are restricted to `Planned` mode.
- **UploadFiles**: Allows the user to upload files to the task's isolated workspace.
- **EditFiles**: Allows the user to modify or delete files in the workspace.
- **ConsoleView**: Grants access to the raw developer console logs and split-view during live execution.
- **DebugMode / TraceMode**: Permits the user to toggle these diagnostic flags when launching a task.
- **Delete**: Allows the user to cancel running tasks and delete completed, failed, or cancelled tasks along with their workspaces.

The UI dynamically adapts to hide elements (like execution modes, upload buttons, or the console tab) if the user lacks the required permissions. The backend APIs strictly enforce these limits regardless of UI state.

### Session Security & Rate Limiting

The system uses secure, persistent server-side session tokens stored in a dedicated SQLite database (`tokens.db`):
- **Cryptographic Token Hashing (SHA-256)**: Session tokens generated on login are cryptographically hashed using SHA-256 before being persisted into `tokens.db`. Raw tokens are returned once to the authenticating client and never stored at rest, protecting user sessions against database compromises and offline theft.
- **Persistent Sessions**: Sessions survive API application restarts and process recycles.
- **Device & Browser Fingerprinting**: Tokens are bound to client IP addresses and browser IDs (`X-Browser-Id`), preventing hijacked token replay attacks across different machines.
- **Instant Session Invalidation on Group/Role Updates**: When an administrator updates a user's permissions, changes a user's role, or updates/deletes a group, all active sessions belonging to the affected users are immediately revoked from `tokens.db`. This prevents privilege retention and forces users to re-login to acquire their updated permission sets.
- **Reverse Proxy & Forwarded Headers Security**: Forwarded headers (`X-Forwarded-For`, `X-Forwarded-Proto`) strictly validate upstream proxies against configured known proxies/CIDR networks and loopback interfaces (`127.0.0.1`, `::1`), preventing malicious header spoofing and IP lockout bypasses. In production, always specify explicit `KnownProxies` (e.g. `["10.0.0.1"]`) or `KnownNetworks` (e.g. `["10.0.0.0/8"]`) instead of `TrustAllProxies = true`, ensuring that untrusted clients cannot spoof arbitrary `X-Forwarded-For` addresses to trigger brute-force account lockouts on legitimate users or bypass rate limiting.
- **Automatic Expiration Pruning**: Expired tokens are purged dynamically upon validation and cleaned in batches by the background maintenance service (`TasksCleanupService`).
- **Rate Limiting**: Built-in ASP.NET Core rate limiting middleware (`UseRateLimiter`) protects sensitive endpoints (such as `/api/auth/login` and `/api/auth/validate`) against automated credential stuffing and brute-force attacks.
- **Composite-Key Account Lockout with Guaranteed Expiration**: Anti-bruteforce protection tracks failed login attempts using a composite key `(IPAddress, Username)` combined with aggregate per-username tracking and striped in-memory user locks (`_userLockStripes`). When aggregate failed attempts across distributed IPs reach `MaxFailedAttempts`, the system guarantees that a valid `LockoutEnd` timestamp is assigned, preventing permanent lockout states and ensuring accounts automatically recover once the lockout duration elapses. Upon successful authentication, failed attempt records are reset across all IPs for the authenticated username (alongside untagged legacy IP records) under lock to eliminate concurrency race conditions, preventing attackers from accumulating stale failed attempts or manipulating lockout state. Token validation uses an in-memory sliding debounce cache to update session last-activity timestamps without SQLite database write contention.
- **RFC 7235 / RFC 6750 Case-Insensitive Bearer Auth Scheme**: The authentication filter and middleware accept all case variations of the HTTP `Authorization` header scheme (e.g. `Bearer <token>`, `bearer <token>`, `BEARER <token>`), ensuring full interoperability with diverse HTTP client implementations.
- **Case-Insensitive Entity Uniqueness**: All entity registration and management endpoints (User accounts, User Groups, and Agent Definitions) enforce case-insensitive uniqueness checks to prevent duplicate records with differing casing (e.g. preventing `"ADMIN"` if `"admin"` exists, or `"DEVTEAM"` if `"DevTeam"` exists).
- **Case-Insensitive Plugin & Task Permission Evaluation**: Permissions for plugins and task IDs are matched case-insensitively across both execution authorization (`PermissionEvaluator`) and REST management APIs (`/api/plugins`, `/api/plugins/{id}/tasks`), guaranteeing consistent catalog listings and tool execution regardless of casing differences.
- **Client Auto-Logout Resilience**: The Blazor WebAssembly client's `UnauthorizedResponseHandler` automatically purges local authentication tokens and notifies the UI on unexpected 401s from protected resource endpoints, while safely ignoring expected 401 responses on `/api/auth/validate` and `/api/auth/login` to prevent disruption of login forms.
- **Per-Request DelegatingHandler Authentication**: The Blazor WebAssembly client registers an `AuthHeaderHandler` (`DelegatingHandler`) in its HTTP message processing pipeline. Each outgoing HTTP request automatically receives `Authorization: Bearer <token>` and `X-Browser-Id` headers retrieved directly from local authentication storage, eliminating race conditions and avoiding mutation of shared `HttpClient.DefaultRequestHeaders`.

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Container Runtime (for Python sandbox plugin):
  - **Windows**: [Rancher Desktop](https://rancherdesktop.io/) (with `dockerd (moby)` engine) or [Docker Desktop](https://www.docker.com/)
  - **Linux / macOS**: Docker CE, Rancher Desktop, or containerd/Podman
- An LLM provider (Ollama, OpenAI, vLLM, etc.)

### First Run

#### 1. Start the API Backend (Server B)

From the repository root:

```bash
dotnet build SemanticAgentBroker/SemanticAgentBroker.slnx
dotnet run --project SemanticAgentBroker/SemanticAgentBroker.Api
```
*(Or navigate first: `cd SemanticAgentBroker/SemanticAgentBroker.Api` and `dotnet run`)*

On first launch, the system will:
1. Create the SQLite databases (`broker.db`, `auth.db`, `tokens.db`)
2. Scan the `Plugins/` directory and load all plugin DLLs
3. Seed default agents (`DataAnalyst`, `LogAnalyzer`, `GeneralAssistant`) and the default admin user
4. Start the SignalR hub for real-time logs and telemetry

The API will be available at `https://localhost:5100` by default (using your trusted .NET developer certificate or configured TLS cert).

#### 2. Start the Blazor WASM Client Host (Server A)

In a separate terminal or separate machine (from the repository root):

```bash
dotnet run --project SemanticAgentBroker/SemanticAgentBroker.ClientHost
```
*(Or navigate first: `cd SemanticAgentBroker/SemanticAgentBroker.ClientHost` and `dotnet run`)*

The ClientHost will start Kestrel on `https://localhost:7050`, serve the Blazor WASM SPA over HTTPS, and dynamically deliver the `ApiBaseUrl` pointing to your API server (`https://localhost:5100/`).

Navigate to `https://localhost:7050` in your browser to open the UI.

### Container Sandbox Setup (Docker & Rancher Desktop)

The Python sandbox plugin executes scripts in an isolated, network-disabled container running the `ai-sandbox-python:latest` image.

#### Building the Sandbox Image

From the repository root:

```powershell
# Using PowerShell helper script:
cd SemanticAgentBroker\Sandbox
powershell -ExecutionPolicy Bypass -File .\build-sandbox.ps1
```

Or building directly via Docker / Rancher Desktop CLI from the repository root:
```bash
docker build -t ai-sandbox-python:latest SemanticAgentBroker/Sandbox
```

This image includes: Python 3.12, pandas, numpy, scipy, matplotlib, seaborn, scikit-learn, openpyxl, pypdf, python-docx, beautifulsoup4, Pillow, and other core analysis packages.

#### Using Rancher Desktop on Windows

[Rancher Desktop](https://rancherdesktop.io/) can be used instead of Docker Desktop on Windows. It is completely free, open-source, and provides a seamless drop-in container environment:

1. **Select Container Engine**: In Rancher Desktop Settings, go to **Application** → **Container Engine** and select **`dockerd (moby)`**.
2. **WSL Integration**: In Settings → **WSL**, ensure integration with your WSL2 distribution is enabled.
3. **Verification**: Open PowerShell and run `docker version`. You should see the Client and Server engine information active.
4. **Auto-Detection**: `PythonSandboxAgentPlugin` automatically connects to the Windows named pipe `npipe://./pipe/docker_engine` (or reads `DOCKER_HOST`), so no custom connection strings or proxy configurations are needed.

---

## Using the UI

### Login (`/login`)

The authentication gateway for platform operators:
- **Credentials** - enter username and password (initial admin account defaults to `admin` / `admin`).
- **Browser Fingerprinting** - automatically generates and persists a unique client identifier (`X-Browser-Id`), binding session tokens cryptographically to the browser instance to prevent token replay attacks.
- **Persistent State** - successful authentication stores the session token in browser local storage, transparently managed by `CustomAuthStateProvider` and attached to outgoing API requests via `AuthHeaderHandler`.
- **Anti-Bruteforce Defense** - repeated failed attempts trigger temporary account lockouts (`LockoutMinutes`, default 5 minutes) tracked across both IP address and username.

### Dashboard (`/`)

- **Stats** - active, completed, failed, and total task counts
- **Available Tasks** - catalog of all discovered Plugin Tasks with one-click "Run Task" execution modals
- **Run Task Modal** - select execution mode (Auto / Planned), enter prompt, toggle debug/trace flags, and attach initial workspace files (guarded up to 10 files per selection batch)

### Task Queue (`/queue`)

Lists all tasks with status, agent name, and timestamps. Supports:
- **Bulk delete** - select multiple tasks and delete at once
- **Restart** - re-run failed/completed tasks
- **Navigate** - click to see live execution

### Live Execution (`/live/taskId`)

Real-time view of agent execution:
- **View Modes**:
  - **Split** (default for users with `ConsoleView` permission) - displays both the Chat and Developer Console panels side by side.
  - **Chat** (default for users without `ConsoleView` permission) - focused conversational view of thoughts, plans, and answers.
  - **Console** - full-width developer log console.
  - **Files** - task workspace explorer with file uploads, live status, and markdown editing.
- **Chat panel** - shows thoughts, actions, tool results, plans, and final answers
- **Event log** - full developer console with timestamped events
- **Workspace panel** - file list with upload (up to 10 files per selection batch), download, view, edit, and delete
- **Pause/Resume/Cancel** controls
- **User input** - send messages to interruptible agents
- **Plan review** - approve/modify/reject implementation plans (with question support)
- **Step approval** - approve or deny individual dangerous tool calls
- **Follow-up** - send continuation prompts to completed tasks

#### File Viewer & Editor

Click any text file in the workspace to open the file viewer modal:

- **Markdown files (`.md`)**: Toggle between **Preview** (rendered) and **Source** (raw text) views
- **All text files**: Click **Edit** to switch to an inline editor with save/cancel
- Changes are saved via `PUT` to the workspace API and broadcast via SignalR
- Binary files are available for download only

### Plugins (`/plugins`)

View and configure all discovered plugins and their methods:
- **Plugin Manifest** - view loaded version, author, category, and dependencies
- **Method Manifest** - inspect parameters, requirements, and safety status (methods requiring human-in-the-loop approval display a `⚠️ Dangerous` badge)
- **Plugin Tasks** - explore pre-packaged tasks with curated tool subsets
- **Configuration** - modify plugin settings (e.g. Docker images, timeouts) dynamically

### User Management (`/users`) *(Admin only)*

Manage platform operator accounts:
- **Search & Filter** - live search users by username
- **Add User** - register new user accounts with designated roles (`Admin`, `User`) and group memberships
- **Edit User** - change passwords, update roles, and manage group assignments
- **Delete User** - delete user accounts (with immediate session revocation in `tokens.db`)

### Group Management (`/groups`) *(Admin only)*

Organize users into permission tiers:
- **Search & Filter** - search groups by name
- **Add Group** - create new security groups
- **Configure Granular Permissions** - assign allowed tools per plugin task and configure execution capabilities (`AutoExecution`, `UploadFiles`, `EditFiles`, `ConsoleView`, `DebugMode`, `TraceMode`, `Delete`)
- **Group Membership** - assign and remove users from groups
- **Delete Group** - remove groups (triggers immediate session invalidation for members to refresh permissions)

### About & Platform Info (`/about`)

System overview and runtime telemetry:
- **System Badges** - version number (`v1.0.0`), author credit, and component status
- **Core Capabilities Highlight** - quick architecture cards (ReAct Engine, Dual Execution Modes, Dynamic Plugin Assemblies, Air-Gapped Sandbox)
- **Runtime Documentation & Licensing** - quick access to open-source licenses, third-party attributions, and architecture guides

---

## API Reference

### Auth & User Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/auth/login` | Authenticate user and receive session token |
| `POST` | `/api/auth/logout` | Terminate current session (requires Bearer token) |
| `GET` | `/api/auth/validate` | Validate session token and return user info |
| `GET` | `/api/users` | List all users (Admin only) |
| `POST` | `/api/users` | Create a new user (Admin only) |
| `PUT` | `/api/users/{id}` | Update user (Admin only) |
| `DELETE` | `/api/users/{id}` | Delete user (Admin only) |

#### Auth Request / Response Schemas

- **`POST /api/auth/login`**:
  - Request body (`LoginRequest`): `{ "username": "admin", "password": "...", "browserId": "..." }`
  - Response (`200 OK`): `{ "token": "<session_token>", "role": "Admin" | "User", "serverInstanceId": "<guid>" }`
  - Error (`401 Unauthorized`): `{ "error": "Invalid username or password." | "Account temporarily locked. Try again later." }`
- **`GET /api/auth/validate`**:
  - Header: `Authorization: Bearer <token>`, `X-Browser-Id: <browserId>`
  - Response (`200 OK`): `{ "username": "admin", "role": "Admin", "userId": "<guid>", "serverInstanceId": "<guid>" }`
- **`POST /api/auth/logout`**:
  - Header: `Authorization: Bearer <token>`, optional `X-Browser-Id: <browserId>`
  - Request body (optional `LogoutRequest`): `{ "browserId": "..." }`
  - Response (`200 OK`): Empty body upon successful termination
  - Error (`401 Unauthorized`): Missing/invalid bearer token or unauthenticated session

> **Note on `serverInstanceId`**: Returned on both login and validation to allow clients (such as the Blazor WASM frontend) to detect backend process recycles and re-authenticate promptly.

### Group Management (Admin only)

All group operations require Admin privileges.

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/groups` | List all groups (Admin only) |
| `POST` | `/api/groups` | Create a new group (Admin only) |
| `PUT` | `/api/groups/{id}` | Update group (Admin only) |
| `DELETE` | `/api/groups/{id}` | Delete group (Admin only) |

### Plugin Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/plugins` | List all loaded plugins (filtered for non-admin users based on group permissions) |
| `GET` | `/api/plugins/{id}` | Plugin details with config values (Admin only) |
| `POST` | `/api/plugins/{id}/toggle` | Enable/disable a plugin (Admin only) |
| `PUT` | `/api/plugins/{id}/config` | Update plugin configuration (Admin only) |
| `POST` | `/api/plugins/reload` | Force rescan plugins directory (Admin only) |
| `POST` | `/api/plugins/{id}/reload` | Reload a single plugin (Admin only) |
| `GET` | `/api/plugins/{id}/tasks` | List plugin tasks with enabled state |
| `POST` | `/api/plugins/{id}/tasks/{taskId}/toggle` | Enable/disable a plugin task (Admin only) |

### Agent Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/agents` | List all agents |
| `GET` | `/api/agents/{name}` | Agent details with method bindings |
| `POST` | `/api/agents` | Create a new agent (Admin only) |
| `PUT` | `/api/agents/{name}` | Update an agent (Admin only; metadata and optionally tool bindings; omitting `methods` preserves existing bindings) |
| `DELETE` | `/api/agents/{name}` | Delete an agent (Admin only) |
| `POST` | `/api/agents/{name}/duplicate` | Clone an existing agent (Admin only) |
| `GET` | `/api/agents/{name}/preview-prompt` | Preview the generated system prompt |

### Task Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/tasks` | Create and enqueue a new task (supports JSON & multipart with initial files) |
| `GET` | `/api/tasks` | List tasks with pagination (`?page=1&pageSize=20`, default `pageSize` 20, clamped `[1, 100]`). Returns `{ items, totalCount, page, pageSize, totalPages }` |
| `GET` | `/api/tasks/stats` | Aggregated execution statistics across all tasks (total, active, completed, failed) |
| `GET` | `/api/tasks/{id}` | Get task details |
| `DELETE` | `/api/tasks/{id}` | Cancel and delete a task |
| `POST` | `/api/tasks/{id}/pause` | Pause an active or queued task (only non-terminal tasks can be paused; returns 400 Bad Request if task is Completed, Failed, or Cancelled). Paused queued tasks are held before acquiring execution concurrency slots and resume cleanly without task loss. |
| `POST` | `/api/tasks/{id}/resume` | Resume a paused task |
| `POST` | `/api/tasks/{id}/restart` | Restart a failed/completed task (clears runtime plan/events and re-initializes chat history preserving initial user prompt) |
| `POST` | `/api/tasks/{id}/proceed` | Send follow-up prompt to completed task (request body: raw JSON string prompt) |
| `POST` | `/api/tasks/bulk-delete` | Delete multiple tasks at once (Admin only, request body: `{ "taskIds": ["<guid>", ...] }`, max 500 tasks per batch) |
| `GET` | `/api/tasks/{id}/events` | Get task event log |
| `GET` | `/api/tasks/{id}/chat` | Get chat history |
| `GET` | `/api/tasks/{id}/logs` | Get task log entries (legacy, paginated) |
| `GET` | `/api/tasks/{id}/plan` | Get latest implementation plan |
| `GET` | `/api/tasks/{id}/plans` | Get all plan versions |
| `POST` | `/api/tasks/{id}/interruption` | Send interruption message to running task (request body: raw JSON string message) |
| `POST` | `/api/tasks/{id}/step/approve` | Approve a pending dangerous tool call (see Step Approval below) |
| `POST` | `/api/tasks/{id}/step/reject` | Reject a pending dangerous tool call with feedback (see Step Approval below) |

#### Plan Review Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/tasks/{id}/plan/approve` | Approve the current plan |
| `POST` | `/api/tasks/{id}/plan/modify` | Modify and approve the plan (request body: raw JSON string containing modified Markdown) |
| `POST` | `/api/tasks/{id}/plan/reject` | Reject the plan with feedback (request body: raw JSON string feedback) |
| `POST` | `/api/tasks/{id}/plan/question` | Ask a clarifying question about the plan (request body: raw JSON string question) |

#### Implementation Plan Schema (GET `/api/tasks/id/plan` and `/api/tasks/id/plans`)

Returns the current active plan (`/plan`) or the history of all plan revisions (`/plans`):

| Property | Type | Description |
|----------|------|-------------|
| `id` | `Guid` | Unique plan identifier |
| `sessionId` | `Guid` | Associated task session ID |
| `version` | `int` | Plan version number (increments with each revision) |
| `planMarkdown` | `string` | Full Markdown plan text generated by the agent |
| `openQuestions` | `string?` | Questions or ambiguities requiring user input |
| `status` | `PlanStatus` | Current plan state (`Draft`, `AwaitingReview`, `Revised`, `Approved`, `Rejected`, `RuntimeUpdate`) |
| `createdAt` | `DateTime` | Plan generation timestamp (UTC) |
| `approvedAt` | `DateTime?` | Timestamp when the plan was approved (UTC) |
| `userFeedback` | `string?` | User feedback provided during plan rejection or modification |

##### `PlanStatus` Enum Values
- **`Draft`**: Initial plan generation in progress.
- **`AwaitingReview`**: Plan generated in Planned mode; execution is paused waiting for operator review.
- **`Revised`**: Plan revised by the operator or regenerated by the agent following feedback.
- **`Approved`**: Plan approved by the operator (or auto-approved in Auto mode); autonomous tool execution proceeds.
- **`Rejected`**: Operator rejected the plan with feedback; agent incorporates feedback to draft a revision.
- **`RuntimeUpdate`**: Plan updated dynamically during active ReAct execution.

#### Step Approval Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/tasks/{id}/step/approve` | Approve a pending dangerous tool call |
| `POST` | `/api/tasks/{id}/step/reject` | Reject a pending dangerous tool call with feedback (request body: raw JSON string feedback) |

#### Task Creation Payload (`AgentTaskRequest`)

When submitting tasks via `POST /api/tasks` (either as `application/json` or as form field `"request"` in `multipart/form-data`):

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `AgentName` | `string` | `"RuntimeAgent"` | Target agent name (e.g. `"RuntimeAgent"`, `"LogAnalyzer"`, `"DataAnalyst"`) |
| `Prompt` | `string` | *(Required)* | The user directive / task prompt for the agent |
| `Mode` | `ExecutionMode` / `int` | `Auto` (`0`) | Execution mode: `Auto` (`0`) or `Planned` (`1`) |
| `DebugMode` | `bool` | `false` | When true, logs raw LLM output, parsed fields, tool args, and all internal decisions |
| `TraceMode` | `bool` | `false` | When true, persists full chat history exchanges to trace files |
| `MaxSteps` | `int?` | `null` | Maximum allowed ReAct reasoning steps (unbounded if omitted) |
| `IsPrivate` | `bool` | `false` | Restricts task visibility and control exclusively to creator and admins |
| `SourcePluginId` | `string?` | `null` | Required when executing a Plugin Task template (e.g. `"SampleData"`) |
| `PluginTaskId` | `string?` | `null` | Required when executing a Plugin Task template (e.g. `"ServerLogDiag"`) |

> [!NOTE]
> **Execution Mode Defaulting Nuance**: When submitting tasks via raw `application/json` without specifying `Mode`, it defaults to `Auto` (`0`). When submitting via `multipart/form-data` with individual form fields, omitting the `mode` field defaults to `Planned` (`1`) to prioritize user plan review when uploading workspace assets. Specifying an explicit `"mode"` value in JSON or form data always takes precedence.

#### Task Details Response Schema (`GET /api/tasks/id`)

| Property | Type | Description |
|----------|------|-------------|
| `id` | `Guid` | Unique session / task ID |
| `agentName` | `string` | Agent assigned to this task |
| `userPrompt` | `string` | Initial user prompt |
| `mode` | `ExecutionMode` | `Auto` (`0`) or `Planned` (`1`) |
| `debugMode` | `bool` | Whether debug logging was enabled for this task |
| `traceMode` | `bool` | Whether full conversation trace persistence was enabled |
| `state` | `TaskState` | Current execution state (`Draft`, `Queued`, `Planning`, `PlanReview`, `AwaitingApproval`, `AwaitingInput`, `InProgress`, `Paused`, `Completed`, `Failed`, `Cancelled`) |
| `sourcePluginId` | `string?` | Source plugin ID if created from a plugin task template |
| `pluginTaskId` | `string?` | Plugin task ID if created from a plugin task template |
| `createdAt` | `DateTime` | Task creation timestamp (UTC) |
| `startedAt` | `DateTime?` | Timestamp when execution began (UTC) |
| `completedAt` | `DateTime?` | Timestamp when task reached a terminal state (UTC) |
| `finalResult` | `string?` | Terminal answer or failure explanation |
| `maxSteps` | `int?` | Configured step budget |
| `createdByUserId` | `Guid?` | User ID of the task creator |
| `createdByUserName` | `string?` | Username of the task creator |
| `allowedOptions` | `string[]` | Options permitted for the caller (`AutoExecution`, `UploadFiles`, `EditFiles`, `ConsoleView`, `DebugMode`, `TraceMode`, `Delete`) |

##### `TaskState` Enum Lifecycle

| State | Category | Description |
|-------|----------|-------------|
| `Draft` | Initial | Task created but not yet enqueued or initialized |
| `Queued` | Waiting | Task enqueued, waiting for an available execution concurrency slot (semaphore) |
| `Planning` | Planning | Agent is drafting or updating the implementation plan |
| `PlanReview` | Waiting | Implementation plan generated; execution paused awaiting user review, modification, or questions |
| `AwaitingApproval` | Waiting | Agent emitted an action flagged with `IsDangerous = true` (or planned tool call); execution paused awaiting operator approval |
| `AwaitingInput` | Waiting | Agent encountered ambiguity or paused awaiting clarifying feedback from the user |
| `InProgress` | Active | Active ReAct reasoning and tool execution loop is running |
| `Paused` | Paused | Task execution paused mid-flight by operator |
| `Completed` | Terminal | Task finished successfully with a final answer |
| `Failed` | Terminal | Task aborted due to unrecoverable exception, timeout, or exceeded reasoning steps |
| `Cancelled` | Terminal | Task cancelled by operator |


### Workspace Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/tasks/{id}/workspace` | List all workspace files |
| `GET` | `/api/tasks/{id}/workspace/{fileName}` | Read/download a file (enforces `MaxDownloadSizeMb` limit, default 50 MB) |
| `PUT` | `/api/tasks/{id}/workspace/{fileName}` | Update text file content |
| `POST` | `/api/tasks/{id}/workspace/upload` | Upload file(s) (multipart) |
| `DELETE` | `/api/tasks/{id}/workspace/{fileName}` | Delete a file |

> **Path Normalization & Traversal Security**: All workspace file endpoints (`GET`, `PUT`, `DELETE`, `POST upload`), the `FileIOAgentPlugin`, and `ToolExecutionService` normalize both Windows (`\`) and Unix (`/`) directory separators across platforms. Subdirectory paths are stripped with `Path.GetFileName` and directory traversal sequences (`..`, leading separators) are strictly rejected with `400 BadRequest`.
>
> **Session Lock & State Cleanup**: Deleting tasks (`DELETE /api/tasks/{id}` or `POST /api/tasks/bulk-delete`), restarting tasks (`POST /api/tasks/{id}/restart`), and automated background retention sweeps (`TasksCleanupService`) invoke `CleanupSession(taskId)` on both `IWorkspaceLockManager` and `ITaskStateManager` to purge active locks and state tracking from memory.
>
> **Task Execution Lifecycle Timestamps**: Tasks record `CreatedAt` upon initial creation. When execution begins (transitioning to `Planning` or `InProgress`), `AgentTaskStore` automatically assigns `StartedAt = DateTime.UtcNow` and preserves this timestamp throughout execution across subsequent phases. Upon reaching a terminal state (`Completed`, `Failed`, `Cancelled`), `CompletedAt` is populated. Restarting a task (`POST /api/tasks/{id}/restart`) resets both `StartedAt` and `CompletedAt` to null, allowing them to be cleanly recorded for the new execution run.

### Configuration

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/config/upload-limit` | Get maximum upload file size in MB |

### Execution Modes

| Value | Name | Description |
|-------|------|-------------|
| `Auto` | Auto | Generate plan → Auto-approve (no user review) → Execute autonomously. Accepts mid-flight interruptions. |
| `Planned` | Planned | Generate plan → User reviews/approves → Execute autonomously. Accepts mid-flight interruptions. |

> **Note**: Both modes always go through the planning phase. The only difference is whether the plan is auto-approved or presented for user review.

---

## SignalR Real-Time API

The system uses SignalR for real-time, bidirectional communication between the server and clients. The hub is available at `/loghub`.

### Connection

Connect to `/loghub` using either query string parameters or HTTP request headers:
- **Query String Authentication (Standard for SignalR WebSockets)**:
  `/loghub?access_token={token}&browserId={browserId}`
- **HTTP Header Authentication**:
  - `Authorization: Bearer {token}` - session token from login
  - `X-Browser-Id: {browserId}` - browser fingerprint matching the session

Unauthenticated connections are rejected with `401 Unauthorized` on connect.

### Client → Server Methods

These are methods the client can invoke on the hub:

| Method | Parameters | Description |
|--------|-----------|-------------|
| `SubscribeToTask` | `taskId` (Guid) | Join the SignalR group for a task to receive its events |
| `UnsubscribeFromTask` | `taskId` (Guid) | Leave the SignalR group for a task |
| `SendInterruption` | `taskId` (Guid), `message` (string) | Send an interruption message to a running task |
| `ApproveStep` | `taskId` (Guid) | Approve a pending dangerous tool call |
| `RejectStep` | `taskId` (Guid), `feedback` (string) | Reject a pending dangerous tool call with feedback |
| `ApprovePlan` | `taskId` (Guid) | Approve the current implementation plan |
| `ModifyPlan` | `taskId` (Guid), `modifiedPlanMarkdown` (string) | Submit a modified implementation plan |
| `RejectPlan` | `taskId` (Guid), `feedback` (string) | Reject the plan with feedback for revision |
| `AskPlanQuestion` | `taskId` (Guid), `question` (string) | Ask a clarifying question about the plan |

> **Note**: All client → server methods (except `UnsubscribeFromTask`) enforce strict privilege separation:
> - **Read Subscription (`SubscribeToTask`)**: Allowed for Admins, the task creator, users with matching plugin task permissions, or all authenticated users if the task is public (`!IsPrivate`).
> - **Control Actions (`SendInterruption`, `ApproveStep`, `RejectStep`, `ApprovePlan`, `ModifyPlan`, `RejectPlan`, `AskPlanQuestion`)**: Strictly restricted to Admins, the task creator, or users with effective permissions on a bound plugin task. Non-owners cannot execute control actions or approve steps on public custom tasks.

### Server → Client Events

These are events broadcast to clients subscribed to a task's group:

| Event Name | Payload Type | Description |
|-----------|-------------|-------------|
| `ReceiveLog` | `AgentTaskLog` | Legacy log entry (backward compatibility) |
| `TaskStateChanged` | `TaskStateChangedEvent` | Task transitioned to a new state (broadcast to all dashboard views for public tasks, and task/user groups for private tasks) |
| `AgentThought` | `AgentThoughtEvent` | Agent emitted a reasoning thought |
| `PlanGenerated` | `PlanGeneratedEvent` | Agent generated an implementation plan |
| `ToolExecution` | `ToolExecutionEvent` | Tool started or finished execution |
| `AgentActivity` | `AgentActivityEvent` | Activity label update (spinner text) |
| `PlanReviewRequested` | `PlanReviewRequestedEvent` | Plan ready for user review (Planned mode) |
| `PlanRevision` | `PlanRevisionEvent` | Agent revised the plan after feedback |
| `ContextCompaction` | `ContextCompactionEvent` | Context was compacted to fit token limits |
| `WorkspaceFileChanged` | `WorkspaceFileChangedEvent` | File created, modified, or deleted in workspace |
| `TokenCountUpdated` | `TokenCountUpdatedEvent` | Token usage updated during execution |

### Event Payload Schemas

| Type | Fields |
|------|--------|
| `TaskStateChangedEvent` | `TaskId` (Guid), `NewState` (TaskState), `Detail` (string?) |
| `AgentThoughtEvent` | `TaskId` (Guid), `Thought` (string), `AgentName` (string), `Observation` (string?) |
| `PlanGeneratedEvent` | `TaskId` (Guid), `PlanMarkdown` (string), `RequiresApproval` (bool) |
| `ToolExecutionEvent` | `TaskId` (Guid), `ToolName` (string), `Args` (string), `Result` (string?), `IsStarting` (bool), `AgentName` (string) |
| `AgentActivityEvent` | `TaskId` (Guid), `ActivityLabel` (string), `AgentName` (string) |
| `PlanReviewRequestedEvent` | `TaskId` (Guid), `PlanMarkdown` (string), `OpenQuestions` (string?), `Version` (int) |
| `PlanRevisionEvent` | `TaskId` (Guid), `PlanMarkdown` (string), `Version` (int) |
| `ContextCompactionEvent` | `TaskId` (Guid), `OriginalMessageCount` (int), `CompactedMessageCount` (int) |
| `WorkspaceFileChangedEvent` | `TaskId` (Guid), `FileName` (string), `ChangeType` (string) |
| `TokenCountUpdatedEvent` | `TaskId` (Guid), `TokenCount` (int), `TokenLimit` (int), `Scope` (string) |

### Client Connection Resiliency & Event Synchronization

To ensure continuous telemetry and zero event loss during network interruptions and initial page load:
- **Automatic Task Re-subscription**: The client-side `SignalRService` tracks active task subscriptions in memory. When the connection drops and reconnects (via `InfiniteRetryPolicy`), the client automatically executes `ResubscribeAllAsync()` to rejoin all active task groups on the hub without requiring user interaction.
- **Concurrent Log & Chat Snapshot Merging**: When navigating to a task in `LiveExecution.razor`, initial state is loaded via REST API while SignalR streams live events in parallel. The UI merges REST snapshots with live in-flight events (`MergeLogs` and `MergeChatMessages`), deduplicating events by ID/timestamp and maintaining strict chronological order without clobbering live events received during initialization.
- **Plan Review & Revision Event Synchronization**: When implementation plans are modified by users (`Modify`), revised by AI after review feedback (`Reject`), or regenerated in response to user questions (`AskQuestion`), the server broadcasts `PlanRevision` (`PlanRevisionEvent`) with the incremented version to update live subscribers, and the client UI maintains persistent review gates across real-time state changes.

---

## Plugin Development Guide

### Quick Start

1. Create a **Class Library** targeting `net10.0`
2. Reference `SemanticAgentBroker.Sdk`
3. Implement your plugin and methods
4. Build, copy the DLL to `Plugins/`, restart

```xml
<ItemGroup>
  <!-- Reference SDK but do NOT copy it to output - the host already provides it.
       This prevents type identity mismatch across collectible AssemblyLoadContexts. -->
  <ProjectReference Include="..\SemanticAgentBroker.Sdk\SemanticAgentBroker.Sdk.csproj">
    <Private>false</Private>
    <ExcludeAssets>runtime</ExcludeAssets>
  </ProjectReference>
</ItemGroup>
```

> ⚠️ **Critical**: Set `<Private>false</Private>` and `<ExcludeAssets>runtime</ExcludeAssets>` on the SDK reference to prevent type identity mismatch across `AssemblyLoadContext` boundaries.

> 💡 **Windows Hot Reload & File Locking**: On Windows systems, active plugin DLLs loaded into an `AssemblyLoadContext` are locked by the OS file system. To update a DLL in-place, disable/unload the plugin first via `POST /api/plugins/{id}/toggle`, replace the DLL in `Plugins/{Folder}/`, and reload via `POST /api/plugins/{id}/reload` or let `FileSystemWatcher` auto-discover the updated directory. Unloading via `IPluginLoader.UnloadPlugin` explicitly calls `Unload()` on the collectible `AssemblyLoadContext` and disposes the plugin instance, releasing assemblies and native handles from runtime memory.

> 🛡️ **Event Subscriber Isolation & Resilience**: `PluginLoader` isolates its internal state from external event handlers. Notifications (`PluginLoaded`, `PluginUnloaded`, `PluginLoadFailed`) are dispatched defensively with individual try-catch boundaries, ensuring that third-party event listener exceptions do not corrupt registration dictionaries, create duplicate loaded/discovered entries, or prematurely dispose active plugin instances.

---

### Plugin Lifecycle & Categories (`PluginCategory`)

The framework classifies plugins into three distinct categories defined by the `PluginCategory` enum in `SemanticAgentBroker.Sdk`:

| Category | Typical Plugins | Lifecycle & Toggling | Runtime Execution Injection |
| :--- | :--- | :--- | :--- |
| **`BuiltInRequired`** | `PythonSandbox`, `FileIO`, `SystemInfo`, `WorkspaceInfo` | Loaded unconditionally during startup. **Cannot be disabled or unloaded** (`POST /api/plugins/{id}/toggle` returns HTTP 400 Bad Request). | **Auto-Injected**: Every agent and task session automatically binds all methods of every `BuiltInRequired` plugin. |
| **`BuiltInOptional`** | `HttpClient`, `ComputerVision` | Disabled by default (`IsEnabled = false`). Admins can enable or disable them dynamically via the UI or REST API. Disabling unloads the assembly. | **Not Auto-Injected**: Available only if explicitly bound to an agent (`MethodBindings`) or referenced by a plugin task (`ExternalMethods` / `RequiredPlugins`). |
| **`Custom`** | Third-party external DLLs in `Plugins/` | Discovered on startup, disabled by default. Managed dynamically by administrators. | **Not Auto-Injected**: Operates under the same binding rules as `BuiltInOptional`. |

`PluginBase.Category` defaults to `PluginCategory.Custom`. When developing custom third-party plugins, leave this property default or set it explicitly to `PluginCategory.Custom`.

---

### Plugin Structure

Implement `IAgentPlugin` or inherit from `PluginBase` (which provides sensible default virtuals and resource disposal):

```csharp
using SemanticAgentBroker.Sdk;

namespace MyCompany.WeatherPlugin;

public class WeatherPlugin : PluginBase
{
    public override string Id => "Weather";
    public override string DisplayName => "Weather Service";
    public override string Description => "Fetches weather data for cities.";
    public override string Version => "1.0.0";
    public override string Author => "Your Name";
    public override string Icon => "cloud";
    public override IReadOnlyList<string> Tags => ["weather", "api"];

    // Optional: Declare dependency on an optional plugin (e.g. HttpClient).
    // All methods of the required plugin will be whitelisted for tasks exported by this plugin.
    public override IReadOnlyList<string> RequiredPlugins => ["HttpClient"];

    // Plugin category (defaults to PluginCategory.Custom)
    public override PluginCategory Category => PluginCategory.Custom;

    // Export plugin methods (tools)
    public override IReadOnlyList<IPluginMethod> GetMethods() => [new GetWeatherMethod()];

    // Optional: Define predefined, safety-scoped tasks (use cases)
    public override IReadOnlyList<PluginTaskDef> GetTasks() =>
    [
        new PluginTaskDef
        {
            Id = "analyze-weather",
            Name = "Weather Analysis",
            Description = "Analyze weather conditions for a city using current data and external API lookups.",
            SystemInstruction = "You are a weather analyst. Use GetCurrentWeather to fetch metrics and HttpClient to fetch remote bulletins.",
            AllowedMethods = ["GetCurrentWeather"],
            ExternalMethods =
            [
                new ExternalMethodRef
                {
                    PluginId = "HttpClient",
                    MethodId = "HttpGet",
                    IsDangerousOverride = false // mark safe for this specific task workflow
                }
            ]
        }
    ];

    // Optional: Declare configuration schema rendered in the UI
    public override IReadOnlyList<PluginConfigEntry> GetConfigurationSchema() =>
    [
        new() { Key = "ApiKey", DisplayName = "API Key", Description = "Weather service API key", IsRequired = true, IsSensitive = true },
        new() { Key = "BaseUrl", DisplayName = "API Endpoint", Description = "Base URL for the weather service", IsRequired = false, DefaultValue = "https://api.weather.example.com" }
    ];
}
```

---

### Method Implementation

Inherit from `PluginMethodBase` to take advantage of argument validation and output management helpers:

```csharp
using System.Net.Http;
using System.Text.Json;
using SemanticAgentBroker.Sdk;

namespace MyCompany.WeatherPlugin;

public class GetWeatherMethod : PluginMethodBase
{
    public override string Id => "GetCurrentWeather";
    public override string DisplayName => "Get Current Weather";
    public override string Description => "Gets the current weather metrics for a specified city.";
    public override string ReturnDescription => "JSON string containing temperature, humidity, windSpeed, and conditions.";
    public override bool IsDangerous => false;

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "city", Description = "City name (e.g. 'London', 'Tokyo')", ParameterType = typeof(string), IsRequired = true },
        new() { Name = "units", Description = "Measurement units ('metric' or 'imperial')", ParameterType = typeof(string), IsRequired = false, DefaultValue = "metric" }
    ];

    public override async Task<string> ExecuteAsync(
        IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct = default)
    {
        var city = GetRequiredString(arguments, "city");
        var units = GetOptionalString(arguments, "units", "metric");
        var apiKey = context.Configuration.GetValueOrDefault("ApiKey", "");
        var baseUrl = context.Configuration.GetValueOrDefault("BaseUrl", "https://api.weather.example.com");

        // Resolve framework services via IPluginContext.GetService<T>()
        var httpFactory = context.GetService<IHttpClientFactory>();
        var client = httpFactory?.CreateClient() ?? new HttpClient();

        context.Logger.LogInformation("Querying weather for {City} ({Units})", city, units);

        var url = $"{baseUrl}/weather?city={Uri.EscapeDataString(city)}&units={units}&key={apiKey}";
        var response = await client.GetStringAsync(url, ct);

        // Auto-save large outputs (>2000 chars) to workspace and return a reference pointer
        return await SaveLargeResultAsync(response, "weather", context, maxLength: 2000, extension: "json");
    }
}
```

---

### Defining Plugin Tasks (`PluginTaskDef`)

Plugin tasks define constrained, safety-scoped execution templates with curated tool subsets. When launched (e.g. from the UI Dashboard or API via `sourcePluginId` + `pluginTaskId`), the orchestrator dynamically spins up an **ephemeral runtime agent** (`RuntimeAgent`) configured specifically with the task's `SystemInstruction` and allowed tools-it does not reuse predefined agents from `AgentRegistry`, nor does it register persistent agents in the database. Users can launch them directly from the catalog without manually composing an agent.

```csharp
public override IReadOnlyList<PluginTaskDef> GetTasks() =>
[
    new PluginTaskDef
    {
        Id = "analyze-weather",
        Name = "Weather Analysis",
        Description = "Analyze weather patterns for a region and cross-reference with satellite data.",
        SystemInstruction = "You are a weather analyst. Use GetCurrentWeather and HttpClient-HttpGet to collect telemetry. Summarize findings in Markdown.",
        AllowedMethods = ["GetCurrentWeather"],
        ExternalMethods =
        [
            new ExternalMethodRef
            {
                PluginId = "HttpClient",
                MethodId = "HttpGet",
                IsDangerousOverride = false // Override: allow GET without pausing for step approval in Planned mode
            }
        ]
    }
];
```

> [!NOTE]
> **Core Tools Are Already Auto-Injected**: Methods from `BuiltInRequired` plugins (`FileIO`, `PythonSandbox`, `SystemInfo`, and `WorkspaceInfo`) are automatically injected into every agent and task session. You **do not** need to include `PythonSandbox` or `FileIO` in `ExternalMethods` or `RequiredPlugins`.

---

### Cross-Plugin Tool Referencing & Dependencies

When designing modular plugins, understanding how cross-plugin access works is critical:

#### 1. Direct In-Process C# Calling Is Disallowed
Each plugin is compiled into an independent DLL and loaded into an isolated, collectible `AssemblyLoadContext` (`PluginAssemblyLoadContext`). Plugins **cannot directly reference or invoke C# methods of another plugin assembly in code**.

#### 2. Accessing Non-Required Plugins Requires Direct Referencing
Because optional and custom plugins are not auto-injected into sessions, an agent running a task exported by Plugin A will have **zero access** to tools from Plugin B unless Plugin B or its methods are **directly referenced**:

* **Task-Level Direct Referencing (`PluginTaskDef.ExternalMethods`)** *(Recommended for granular control)*:
  Specifies exact foreign methods allowed for that specific task, with optional per-method safety overrides:
  ```csharp
  ExternalMethods =
  [
      new ExternalMethodRef
      {
          PluginId = "ComputerVision",
          MethodId = "AnalyzeImage",
          IsDangerousOverride = false
      }
  ]
  ```
* **Plugin-Level Direct Referencing (`IAgentPlugin.RequiredPlugins`)** *(Coarse-grained dependency)*:
  Declares that this entire plugin depends on another plugin. When any task from this plugin executes, the broker loops through `RequiredPlugins` and whitelists **all methods** of the referenced plugin:
  ```csharp
  public override IReadOnlyList<string> RequiredPlugins => ["ComputerVision"];
  ```

#### 3. Mandatory Runtime Prerequisite: The Target Plugin Must Be Enabled
For tools from a referenced non-required plugin (`ComputerVision`, `HttpClient`, or custom plugins) to be available during execution, the target plugin **must be loaded and enabled** in the broker (`PluginRegistrations.IsEnabled == true`). If the target plugin is disabled, `_pluginRegistry.GetPlugin(...)` returns `null`, the method is not registered on the kernel, and the agent will fail if it attempts invocation.

#### 4. Inter-Plugin Data Exchange Patterns
Plugins exchange information through:
- **LLM Tool Orchestration**: Output from one tool (e.g. `HttpClient-HttpGet`) is fed as an observation to the LLM, which uses it in subsequent tool calls (e.g. `Weather-GetCurrentWeather`).
- **Shared Workspace Files (`/app/workdir/`)**: Tools write files to the isolated workspace (synchronized via `IWorkspaceLockManager`), which other tools or Python scripts read.
- **Framework DI Services**: Shared runtime utilities resolved via `context.GetService<T>()`.

---

### Dangerous Methods & Safety Overrides

Methods marked with `IsDangerous => true` (or overridden via `MethodSafetyOverride` / `ExternalMethodRef.IsDangerousOverride`) require explicit human-in-the-loop approval in `Planned` mode before execution. The engine presents the tool name and arguments to the user via SignalR, pausing execution until the user approves or denies the call.

* **Default Method Safety**: Declared on the method via `public override bool IsDangerous => true;`.
* **Task External Safety Override**: Configured per external method in `PluginTaskDef.ExternalMethods` via `IsDangerousOverride = false`.
* **Task Internal Safety Override**: Configured for local methods in `PluginTaskDef.SafetyOverrides` via `new MethodSafetyOverride { MethodId = "...", IsDangerous = false }`.

> **Safety Evaluation Contract**: `ToolExecutionService.IsDangerousAction` evaluates the registered tool's `IsDangerous` metadata property. Unregistered or unresolvable tool names evaluate as non-dangerous (`false`), allowing the execution engine to fail fast in `ExecuteToolAsync` with actionable suggestions (`"Tool not found. Did you mean..."`) rather than pausing execution for step approval.

---

### `IPluginContext` Reference

Provides scoped access to the task workspace, logging, configuration, and framework services without coupling plugins to the internal DI container:

| Member | Type | Description |
| :--- | :--- | :--- |
| `WorkspacePath` | `string` | Absolute path to the isolated task workspace directory (`/app/workdir/`). |
| `SessionId` | `Guid` | Unique session / task identifier. |
| `Logger` | `ILogger` | Scoped logger for your plugin. |
| `Configuration` | `IReadOnlyDictionary<string, string>` | Hybrid configuration (database values configured in UI override `appsettings.json`). |
| `GetService<T>()` | `T? where T : class` | Resolves framework-provided services (e.g. `IHttpClientFactory`, `IWorkspaceLockManager`). Returns `null` if unavailable. |

---

### `PluginMethodBase` Helpers Reference

| Method | Return Type | Description |
| :--- | :--- | :--- |
| `GetRequiredString(arguments, key)` | `string` | Extracts a required string argument. Handles both JSON string primitives and `JsonElement` values. Throws `ArgumentException` if missing or empty. |
| `GetOptionalString(arguments, key, defaultValue)` | `string` | Extracts an optional string argument, returning `defaultValue` if omitted or empty. |
| `SaveLargeResultAsync(result, prefix, context, maxLength = 2000, extension = "txt")` | `Task<string>` | If `result` length exceeds `maxLength`, saves content to `/app/workdir/{prefix}_output.{extension}` using workspace locks and returns a pointer reference string (`"Data saved to /app/workdir/..."`). If below threshold, returns `result` directly. |

---

### SDK Contracts & Data Schemas

#### `PluginTaskDef`
| Property | Type | Description |
| :--- | :--- | :--- |
| `Id` | `string` | Unique identifier for this task within the plugin. |
| `Name` | `string` | Human-readable name displayed in the UI task catalog. |
| `Description` | `string` | Detailed task objective and description. |
| `AllowedMethods` | `IReadOnlyList<string>` | Internal method IDs (from this plugin) whitelisted for this task. |
| `SystemInstruction` | `string?` | Custom system prompt for the task agent (falls back to generic task instructions if `null`). |
| `ExternalMethods` | `IReadOnlyList<ExternalMethodRef>` | Whitelisted cross-plugin method references with optional safety overrides. |
| `SafetyOverrides` | `IReadOnlyList<MethodSafetyOverride>` | Per-method safety overrides for internal methods in this task. |
| `EnabledByDefault` | `bool` | Whether the task is enabled upon plugin loading (default: `true`). |

#### `ExternalMethodRef`
| Property | Type | Description |
| :--- | :--- | :--- |
| `PluginId` | `string` | ID of the target plugin (e.g. `"HttpClient"`, `"ComputerVision"`). |
| `MethodId` | `string` | ID of the method in the target plugin (e.g. `"HttpGet"`, `"AnalyzeImage"`). |
| `IsDangerousOverride` | `bool?` | Optional safety override for this method when called within this task. |

#### `PluginParameter`
| Property | Type | Description |
| :--- | :--- | :--- |
| `Name` | `string` | Parameter name matching the JSON argument key. |
| `Description` | `string` | Parameter description provided to the LLM for tool selection. |
| `ParameterType` | `Type` | .NET data type (defaults to `typeof(string)`). |
| `IsRequired` | `bool` | Whether the parameter is mandatory (default: `true`). |
| `DefaultValue` | `object?` | Default value if optional. |

#### `PluginConfigEntry`
| Property | Type | Description |
| :--- | :--- | :--- |
| `Key` | `string` | Configuration key (e.g. `"ApiKey"`, `"BaseUrl"`). |
| `DisplayName` | `string` | UI label for the configuration input form. |
| `Description` | `string` | Help text displayed in the UI. |
| `IsRequired` | `bool` | Whether this setting must be populated for the plugin to function. |
| `IsSensitive` | `bool` | If `true`, the value is masked in the UI and redacted from non-admin logs. |
| `DefaultValue` | `string?` | Optional initial default value. |

---

## Creating Custom Agents

In Semantic Agent Broker, there is an important architectural distinction between **Persistent Named Agents** (`AgentDefinition`) and **Plugin Tasks / Ephemeral Runtime Agents** (`PluginTaskDef`):

- **Persistent Named Agents (`AgentDefinition`)**:
  - Reusable agent personas registered in SQLite (`AgentDefinitions` table) and cached in `IAgentRegistry`.
  - Defined with a unique name, system prompt, accent/icon metadata, and explicit cross-plugin method bindings (`MethodBindings`).
  - Plugins themselves **do not** register persistent agents into `IAgentRegistry`. Instead, custom named agents are created, modified, or duplicated programmatically via the **REST API** (`POST /api/agents`, `PUT /api/agents/{name}`, `DELETE /api/agents/{name}`) (Admin-only).
  - Executed directly via API by specifying `agentName` (e.g. `POST /api/tasks` with `{ "agentName": "DataAnalyst", "prompt": "..." }`).
- **Plugin Tasks & Ephemeral Runtime Agents (`PluginTaskDef`)**:
  - Task execution templates declared directly in C# plugin code via `plugin.GetTasks()`.
  - When triggered from the UI Dashboard or API (via `sourcePluginId` + `pluginTaskId`), the orchestrator does **not** reuse a predefined agent from `AgentRegistry`.
  - Instead, the broker dynamically synthesizes an **ephemeral runtime agent** (`RuntimeAgent`) on the fly, configured with the task's `SystemInstruction` and strictly bounded to the task's `AllowedMethods`, `ExternalMethods`, and required plugins. Once the task finishes, this runtime agent is discarded.

### Agent Definition Structure (REST API)

Each persistent agent created via the REST API is composed of:
1. **Name** - unique identifier (e.g. `"DataAnalyst"`, `"WebResearcher"`)
2. **System Instruction** - the core LLM prompt defining agent behavior and domain expertise
3. **Method Bindings** - which plugin methods this agent can use (`PluginId` and `MethodId` pairs)
4. **UI metadata** - icon, accent color, and descriptive purpose

You don't have to bind ALL methods from an optional plugin. For example, give an agent `HttpClient-HttpGet` but NOT `HttpClient-HttpPost` to create a read-only web browsing agent.

> **Note**: Core plugins categorized as `BuiltInRequired` - such as `FileIO`, `PythonSandbox`, `SystemInfo`, and `WorkspaceInfo` - are automatically injected into every agent session and cannot be partially bound or disabled.

Available icons: [Google Material Symbols](https://fonts.google.com/icons).
Available accent classes: `indigo`, `cyan`, `emerald`, `amber`, `rose`.

### Default Seeded Agents

On first launch, if no agent definitions exist in the database, `ApplicationInitialization` automatically seeds three pre-configured agents:

| Agent Name | Icon | Accent | Bound Plugins | Primary Purpose & Capabilities |
| :--- | :--- | :--- | :--- | :--- |
| **`DataAnalyst`** | `trending_up` | `cyan` | `SampleData`, `PythonSandbox`, `FileIO` | Fetches sample datasets (`GetSalesData`, `GetUserList`, etc.) and performs statistical data science analysis via Python in the sandbox container. Outputs structured Markdown summary tables. |
| **`LogAnalyzer`** | `search` | `amber` | `SampleData`, `PythonSandbox`, `FileIO` | Diagnoses server logs (`GetServerLogs`), identifies errors and warnings, groups failures by microservice, and generates structured remediation reports. |
| **`GeneralAssistant`** | `smart_toy` | `indigo` | `PythonSandbox`, `FileIO`, `HttpClient`, `SampleData` | Versatile assistant equipped with Python code execution, workspace file management, outbound HTTP requests, and sample data testing. |

---

## Configuration Reference

All configuration lives in `appsettings.json`. The system uses a configuration-first approach where all inference, sandbox, and tokenization settings are defined centrally and injected via DI.

### `AiProviders` Section

#### Root Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DefaultTimeoutMinutes` | `int` | `60` | HTTP timeout for LLM API calls |
| `MaxTokensPerRequest` | `int` | `8192` | Max tokens the LLM can generate per request |
| `MaxAssistantOutputLength` | `int` | `24000` | Max chars for assistant output before truncation |
| `MaxToolObservationLength` | `int` | `24000` | Max chars for tool observation before auto-save |
| `AutoSaveToolOutputThreshold` | `int` | `3000` | Tool output char threshold before auto-saving to workspace |
| `ToolOutputPreviewLength` | `int` | `500` | Characters displayed at both the beginning and end of the inline chat preview when large tool output is auto-saved |

#### `Inference` Section

Controls the ReAct execution engine and planning behavior. Reasoning step execution is unbounded by default unless explicitly bounded per-task via client request or per-plugin task definition (`maxSteps`).

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxParallelToolCalls` | `int` | `3` | Maximum tools the agent can execute concurrently in a single step |
| `MaxLlmRetries` | `int` | `-1` | Max retries for LLM failures (connection refused, HTTP 5xx/429, timeout). Set to `-1` for infinite retries until server comes online |
| `LlmRetryDelaySeconds` | `int` | `5` | Initial delay in seconds between LLM retries |
| `MaxLlmRetryDelaySeconds` | `int` | `30` | Max cap in seconds for exponential backoff retry delay |
| `MaxFormatRetries` | `int` | `3` | Max format correction retries before aborting |
| `MaxFinalAnswerInlineChars` | `int` | `4000` | FinalAnswer char limit before auto-spilling to file |
| `StopMarkers` | `string[]` | `[...]` | Token sequences that signal end of generation |
| `ThoughtPrefixesToStrip` | `string[]` | `[...]` | Control/channel tokens stripped from reasoning output (e.g. `<channel|>Thought`, `<|thought|>`) |
| `ArtifactReplacements` | `dict` | `{...}` | Mapping of strings to replace in generation |

##### PromptExecutionSettings Profiles

Four freeform dictionaries that map directly to `PromptExecutionSettings.ExtensionData`. These are provider-specific key-value pairs (e.g., Ollama uses `num_predict`, `num_ctx`; OpenAI uses `max_tokens`).

If a profile is `null`, the engine builds settings from the typed properties above (backward compatible).

| Profile | Used By | Purpose |
|---------|---------|---------|
| `MainExecutionPromptSettings` | SubagentExecutor | Main reasoning and fallback LLM calls |
| `NudgedExecutionPromptSettings` | SubagentExecutor | Nudged brevity retry with elevated temperature/penalties |
| `PlanningPromptSettings` | PlanningEngine | Plan generation calls |
| `SummarizationPromptSettings` | ContextCompactionService | Context summarization calls |

Example:
```json
"MainExecutionPromptSettings": {
    "temperature": 0.0,
    "num_predict": 8192,
    "max_tokens": 8192,
    "num_ctx": 32768,
    "stop": ["<|im_end|>", "Observation:"]
}
```

#### `HealthCheck` Section

Controls automated health probing and failover routing across LLM provider connections.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Enable periodic health check probes for AI providers. When `false`, background probing is disabled and all configured enabled endpoints are considered healthy by default |
| `IntervalSeconds` | `int` | `30` | Time between health check cycles |
| `TimeoutSeconds` | `int` | `5` | Request timeout per health check probe |
| `EndpointPaths` | `Dictionary<string, string>` | `{"OpenAiCompatible": "/v1/models"}` | Health probe relative paths per provider type |

#### `ModelRouting` Section

Maps task roles to specific models. If `ProviderId` is omitted, the load balancer picks the best connection.

| Role | Description |
|------|-------------|
| `PrimaryTextModel` | Main model for ReAct execution and reasoning |
| `SummarizationModel` | Model for context compaction summarization |
| `VisionModel` | Model for image analysis used by ComputerVisionPlugin |

> **Note**: Model routing, provider resolution, token counting (`MlTokenCounter`), and load balancing match Model IDs and Provider IDs case-insensitively (`StringComparison.OrdinalIgnoreCase`).

#### `Connections` Array

Each connection defines an LLM provider endpoint.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `string` | Unique identifier (used for load balancing) |
| `EndpointName` | `string` | Display name |
| `ProviderType` | `string` | `"Ollama"`, `"OpenAiCompatible"`, or `"OpenAi"` |
| `Endpoint` | `string` | Base URL of the provider |
| `IsEnabled` | `bool` | Whether this connection is active |
| `ApiKey` | `string` | API key (for OpenAI-compatible providers) |
| `HttpBasicAuthUsername/Password` | `string` | Optional HTTP basic auth |
| `TrustUntrustedCertificates` | `bool` | Accept self-signed TLS certs. **WARNING**: Intended ONLY for local development/testing. Do not enable in production to avoid MITM vulnerabilities. |
| `Models` | `array` | List of available models with `Type`, `MaxContextTokens`, `TokenCounting`, and optional per-model `ThoughtPrefixesToStrip` |

> **Custom Endpoint Routing**: For `"Ollama"`, `"OpenAiCompatible"`, and custom connection endpoints, both `KernelProviderService` and `ComputerVisionPlugin` explicitly forward the configured connection `Endpoint` to Semantic Kernel's `AddOpenAIChatCompletion`, guaranteeing that all LLM queries and vision analyses reach the designated local or remote endpoint URL instead of default OpenAI cloud hosts.

##### `TokenCounting` Section (per model)

Configured per model under `AiProviders:Connections:[i]:Models:[j]:TokenCounting`:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `TokenizerModel` | `string` | `"gpt-4o"` | ML.Tokenizers model ID for BPE token counting |
| `FallbackDivisor` | `double` | `2.5` | Character divisor when tokenizer is unavailable |
| `PerMessageOverhead` | `int` | `20` | Token overhead per message for role markers |
| `TokenizerJsonPath` | `string` | `null` | Optional path to custom tokenizer.json file |

#### Ollama & vLLM Context Window Management

The broker provides automated application-level context window configuration across local providers:

- **Ollama**:
  - **Smart Registry Initialization**: On endpoint health probe startup, `EndpointHealthCheckService` inspects the model's registered parameters via `POST /api/show`. If `num_ctx` already matches `MaxContextTokens` (e.g., `32768`), no change or model unload occurs (zero startup delay). If a mismatch is detected, it automatically updates Ollama's registry via `POST /api/create` and flushes only mismatched runners via `POST /api/generate` (`keep_alive: 0`).
  - **Payload Interception**: `OllamaCompatibilityDelegatingHandler` automatically formats outgoing HTTP POST requests with both top-level `"num_ctx": N` and nested `"options": { "num_ctx": N }`.
- **vLLM**:
  - Uses OpenAI-compatible endpoints (`/v1/chat/completions`). Extra properties (`options`, `num_ctx`) in request payloads are safely ignored by vLLM's Pydantic parser, while `MaxContextTokens` in `appsettings.json` is enforced by the broker for token counting and Context Compaction.

#### `PythonSandbox` Section

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ImageName` | `string` | `"ai-sandbox-python:latest"` | Docker image for sandbox |
| `TimeoutSeconds` | `int` | `60` | Execution timeout per script |
| `MemoryLimitMb` | `int` | `512` | Container memory limit |
| `MaxOutputMb` | `int` | `50` | Maximum container output in MB before stream bounding/truncation |
| `DockerUri` | `string` | `""` (auto-detect) | Container daemon URI (auto-detects `DOCKER_HOST`, Windows named pipe `npipe://./pipe/docker_engine` for Docker Desktop and Rancher Desktop, or Unix socket `unix:///var/run/docker.sock` on Linux) |
| `ContainerDataPath` | `string` | `"/app/workdir"` | Mount path inside container |
| `HostDataPath` | `string` | `"TaskWorkspaces"` | Host-side workspace base path |
| `CpuQuota` | `long?` | `null` | CPU quota in microseconds (empty = no limit) |
| `CpuPeriod` | `long` | `100000` | CPU CFS period in microseconds |

#### `ContextCompaction` Section

Controls how the task state manager trims and compacts context for long-running conversations.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `CompactionThreshold` | `double` | `0.80` | Token ratio threshold to trigger compaction |
| `KeepRecentMessages` | `int` | `6` | Number of recent messages to preserve |
| `MaxSummaryTokens` | `int` | `1200` | Max tokens for the rolling summary |
| `EnableStructuredStateTracking` | `bool` | `true` | Track task state in `_task_state.md` |
| `NudgeReductionAfterStep` | `int` | `5` | Reduce verbose nudges after N steps |
| `MaxStepLogChars` | `int` | `1500` | Max chars per step in state log |
| `MaxStateInjectionTokens` | `int` | `16000` | Max tokens injected from state into prompt |
| `RecentObservationWindowSize` | `int` | `3` | Number of recent tool observations to keep in full fidelity |
| `RecentStepFullObservationChars` | `int` | `4000` | Max chars for recent step tool outputs |

##### Adaptive Context Window Scaling

Rather than applying a rigid threshold across diverse models, `ContextCompactionOptions` automatically scales compaction thresholds, preserved message counts, and force-compaction ceilings based on the model's configured `MaxContextTokens`:

| Model Context (`MaxContextTokens`) | Effective Threshold (`GetEffectiveThreshold`) | Preserved Messages (`GetEffectiveKeepCount`) | Force Compaction Ceiling (`GetMaxMessagesBeforeForceCompaction`) |
| :--- | :--- | :--- | :--- |
| **>= 256K** | `Math.Max(CompactionThreshold, 0.90)` (relaxed) | `Math.Max(KeepRecentMessages, 10)` | 100 messages |
| **>= 64K** | `Math.Max(CompactionThreshold, 0.85)` | `Math.Max(KeepRecentMessages, 10)` | 60 messages |
| **>= 32K** | `Math.Min(CompactionThreshold, 0.65)` (earlier compaction for quality) | `Math.Max(KeepRecentMessages, 8)` | 40 messages |
| **>= 16K** | `Math.Min(CompactionThreshold, 0.55)` (aggressive) | `Math.Min(KeepRecentMessages, 4)` | 20 messages |
| **>= 8K** | `Math.Min(CompactionThreshold, 0.45)` (very aggressive) | `Math.Min(KeepRecentMessages, 2)` | 12 messages |
| **< 8K** | `Math.Min(CompactionThreshold, 0.40)` | `Math.Min(KeepRecentMessages, 2)` | 8 messages |

### `AgentSystem` Section

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxConcurrentAgents` | `int` | `3` | Concurrent task execution slots (semaphore size) |
| `DatabaseConnectionString` | `string` | `"Data Source=...broker.db"` | SQLite connection string |
| `AuthDatabaseConnectionString` | `string` | `"Data Source=...auth.db"` | SQLite auth DB connection string |
| `TokensDatabaseConnectionString` | `string` | `"Data Source=...tokens.db"` | SQLite tokens DB connection string (session tokens, browser fingerprints) |
| `SessionDays` | `int` | `7` | Duration of active sessions |
| `MaxFailedAttempts` | `int` | `10` | Auth failure threshold for lockout |
| `LockoutMinutes` | `int` | `5` | Lockout duration after max failures |
| `TraceLogsDirectory` | `string` | `"TraceLogs"` | Directory for trace log files |
| `MaxFileReadChars` | `int` | `8000` | Max chars when reading workspace files |
| `PluginsDirectory` | `string` | `"Plugins"` | Plugin DLL directory (relative to app base) |
| `TaskWorkspacesPath` | `string` | `"TaskWorkspaces"` | Workspace base directory |
| `CleanupWorkspacesOnTaskDelete` | `bool` | `true` | Delete workspace folders when tasks are deleted |
| `MaxUploadSizeMb` | `int` | `10` | File upload limit in megabytes |
| `MaxDownloadSizeMb` | `int` | `50` | Maximum file download size in MB via workspace endpoints |
| `MaxFileEditSizeMb` | `int` | `50` | Maximum file size in MB for text file edits via workspace endpoints |
| `MaxAutoLoadFileSizeMb` | `int` | `50` | Maximum file size in MB allowed for `@/app/workdir/` file auto-loading into tool parameters |
| `GlobalTaskTimeoutMinutes` | `int` | `360` | Maximum allowed overall execution time in minutes before a task is failed with a timeout error |
| `GlobalTaskTimeoutSeconds` | `int?` | `null` | Optional override for global task timeout in seconds |
| `DefaultAdminUsername` | `string` | `"admin"` | Username for the auto-seeded admin account |
| `DefaultAdminPassword` | `string` | `"admin"` | Password for the auto-seeded admin account (change in production!) |
| `DatabaseProvider` | `string` | `"Sqlite"` | Database provider: `Sqlite`, `Postgres`, or `SqlServer` |
| `IsTasksCleanupServiceEnabled` | `bool` | `true` | Enable automated task retention sweep service |
| `TasksCleanupDays` | `int` | `14` | Auto-delete completed, failed, and cancelled tasks older than N days |
| `ArchivedPlansToKeep` | `int?` | `null` | Number of archived plans to keep per task (`null` = keep all) |
| `Cors` | `object` | `{ AllowAll: true }` | Cross-Origin Resource Sharing configuration (`AllowAll`, `AllowedOrigins`) |
| `HttpClientSecurity` | `object` | `{...}` | SSRF protection configuration (`EnableBlocklist`, `EnableAllowlist`, `BlockedPatterns`, `AllowedPatterns`) |
| `Plugins` *(section)* | `object` | `{...}` | Dynamic plugin configurations (`AgentSystem:Plugins:{pluginId}`) resolved at runtime via `IConfiguration` |

#### `AgentSystem:RestApi` Section

Configured under the `AgentSystem:RestApi` section:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `IsEnabled` | `bool` | `true` | Enable/disable the REST API |
| `Host` | `string` | `"0.0.0.0"` | Network interface to bind (`"0.0.0.0"` for all IPs, `"localhost"`, or specific IP) |
| `HttpPort` | `int?` | `null` | Optional HTTP port (`null` to disable HTTP listening) |
| `HttpsPort` | `int?` | `5100` | HTTPS port for TLS termination (uses trusted dev cert or configured cert) |
| `Port` | `int` | `5100` | Port for the REST API (backward compatibility mapping to `HttpsPort`/`HttpPort`) |
| `RedirectHttpToHttps` | `bool` | `false` | When true, redirects HTTP traffic to HTTPS (requires `HttpsPort` and `HttpPort`) |
| `TrustAllProxies` | `bool` | `false` | When true, trusts all upstream proxies. WARNING: Use only in secure isolated internal networks. |
| `KnownProxies` | `string[]` | `[]` | Explicit list of trusted proxy IP addresses (e.g. `["127.0.0.1", "10.0.0.1"]`). If empty and `TrustAllProxies` is false, defaults to loopback IPs (`127.0.0.1`, `::1`). |
| `KnownNetworks` | `string[]` | `[]` | Explicit list of trusted IP networks in CIDR format (e.g. `["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]`). |

#### `AgentSystem:Certificate` Section

Configured under the `AgentSystem:Certificate` section:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Path` | `string?` | `null` | Path to a PFX certificate file |
| `Password` | `string?` | `null` | Password for the PFX file |
| `StoreName` | `string?` | `null` | Windows certificate store name (e.g. `My`) |
| `StoreLocation` | `string?` | `null` | Windows store location (`CurrentUser` or `LocalMachine`) |
| `Thumbprint` | `string?` | `null` | Certificate thumbprint for store lookup |

#### `AgentSystem:Cors` Section

Configured under the `AgentSystem:Cors` section:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `AllowAll` | `bool` | `true` | When true, all origins are permitted (equivalent to `SetIsOriginAllowed(_ => true)`) |
| `AllowedOrigins` | `string[]` | `[]` | Explicit list of allowed CORS origins (enforced when `AllowAll` is false) |

> **Path resolution**: Relative paths in `TaskWorkspacesPath`, `PluginsDirectory`, and `TraceLogsDirectory` are resolved from `AppContext.BaseDirectory` (the application's output directory). Absolute paths are used as-is.

---

## Client Hosting and Multi-Server Deployment

Semantic Agent Broker includes `SemanticAgentBroker.ClientHost`, an ASP.NET Core Kestrel-powered host application designed to serve the Blazor WebAssembly client frontend across multi-server network topologies.

### Architecture Overview

In a multi-server setup:
- **Server A (Frontend Host)**: Runs `SemanticAgentBroker.ClientHost` on Kestrel (e.g. `https://server-a:7050` or `https://client.example.com`). It serves compressed static files (`.wasm`, `.dll`, `.js`, `.css`, `index.html`) and delivers dynamic runtime configuration via `/appsettings.json`.
- **Server B (Backend API)**: Runs `SemanticAgentBroker.Api` (e.g. `https://server-b:5100` or `https://api.example.com`). It executes the agent tasks, manages database states, runs Python sandboxes, and hosts the SignalR hub.
- **Client Browser**: Opens the application from Server A. During startup, Blazor WASM automatically fetches `/appsettings.json` from Server A, which points `ApiBaseUrl` directly to Server B. The client browser then initiates direct cross-origin REST and WebSocket connections to Server B.

```
                    ┌─────────────────────────┐
                    │      Client Browser     │
                    │   (Blazor WASM Engine)  │
                    └───────┬─────────▲───────┘
                            │         │
          1. GET /          │         │ 3. Direct REST API &
          2. GET /appsettings.json    │    SignalR WebSockets
                            │         │    (Bearer auth & CORS)
            ┌───────────────▼┐       ┌┴────────────────┐
            │    Server A    │       │    Server B     │
            │  (ClientHost)  │       │  (Agent Broker) │
            │  HTTPS :7050   │       │  HTTPS :5100    │
            └────────────────┘       └─────────────────┘
```

### Dynamic Client Configuration (`/appsettings.json`)

When `SemanticAgentBroker.Client` boots in the user's browser, `WebAssemblyHostBuilder.CreateDefault()` automatically requests `/appsettings.json` from its origin (Server A). 

`SemanticAgentBroker.ClientHost` dynamically intercepts this route and produces a JSON payload constructed from `ClientHost:ClientSettings` in its own `appsettings.json`:

```json
{
  "ApiBaseUrl": "https://server-b:5100/",
  "Environment": "Production"
}
```

This dynamic delivery allows system administrators to deploy the exact same precompiled Blazor WASM binaries across staging, testing, and production environments, simply changing the target API URL in Server A's `appsettings.json` without recompiling client code.

> [!NOTE]
> `SemanticAgentBroker.ClientHost` registers custom dynamic configuration middleware **before** `appsettings.json` or other static files can be served by `UseStaticFiles`. Requests matching `/appsettings.json`, `/appsettings.*.json`, or `/config` are always intercepted and served dynamically from the host configuration, preventing physical static configuration files on disk in `wwwroot` from shadowing the runtime environment configuration. In addition, SPA fallback requests routed via `MapFallbackToFile("index.html")` apply cache control headers (`no-cache, must-revalidate`) configured in `StaticFiles:HtmlCacheMaxAgeSeconds` to ensure client browsers always receive the latest application entry point.

---

### `ClientHost` Configuration Reference (`appsettings.json`)

All configuration for `SemanticAgentBroker.ClientHost` resides under the `"ClientHost"` section of `appsettings.json`.

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "ClientHost": {
    "Host": "0.0.0.0",
    "HttpPort": null,
    "HttpsPort": 7050,
    "RedirectHttpToHttps": false,

    "ClientSettings": {
      "ApiBaseUrl": "https://server-b:5100/",
      "EnvironmentName": "Production",
      "CustomSettings": {}
    },

    "Certificate": {
      "Path": null,
      "Password": null,
      "StoreLocation": null,
      "StoreName": null,
      "Thumbprint": null
    },

    "Cors": {
      "AllowAll": true,
      "AllowedOrigins": [ "https://client.example.com" ],
      "AllowedMethods": [ "GET", "POST", "OPTIONS" ],
      "AllowedHeaders": [ "*" ],
      "AllowCredentials": true
    },

    "ForwardedHeaders": {
      "Enabled": true,
      "TrustAllProxies": false,
      "KnownProxies": [ "10.0.0.1" ],
      "KnownNetworks": [ "10.0.0.0/8", "172.16.0.0/12" ]
    },

    "SecurityHeaders": {
      "EnableHsts": true,
      "HstsMaxAgeDays": 365,
      "HstsIncludeSubdomains": true,
      "HstsPreload": false,
      "XContentTypeOptions": "nosniff",
      "XFrameOptions": "SAMEORIGIN",
      "ReferrerPolicy": "strict-origin-when-cross-origin",
      "ContentSecurityPolicy": null,
      "PermissionsPolicy": null
    },

    "Compression": {
      "EnableCompression": true,
      "EnableForHttps": true
    },

    "StaticFiles": {
      "FrameworkCacheMaxAgeSeconds": 31536000,
      "HtmlCacheMaxAgeSeconds": 0
    },

    "HealthCheck": {
      "Enabled": true,
      "Path": "/health"
    }
  }
}
```

#### Configuration Options Table

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Host` | `string` | `"0.0.0.0"` | Network interface to bind (`"0.0.0.0"` for all IPs, `"localhost"`, or specific IP) |
| `HttpPort` | `int?` | `null` | HTTP port for serving the application (`null` to disable HTTP) |
| `HttpsPort` | `int?` | `7050` | HTTPS port for TLS termination (`null` to disable HTTPS) |
| `RedirectHttpToHttps` | `bool` | `false` | When true, redirects HTTP traffic to HTTPS (requires `HttpsPort`) |
| `ClientSettings:ApiBaseUrl` | `string` | `"https://localhost:5100/"` | URL of the backend API (Server B) served to WASM clients |
| `ClientSettings:EnvironmentName` | `string?` | `"Production"` | Optional environment indicator passed to client |
| `ClientSettings:CustomSettings` | `object` | `{}` | Key-value pairs merged into client configuration |
| `Certificate:Path` | `string?` | `null` | Path to `.pfx` TLS certificate file |
| `Certificate:Password` | `string?` | `null` | Password for the `.pfx` certificate |
| `Certificate:StoreName` | `string?` | `null` | Certificate store name (e.g., `My`, `Root`) |
| `Certificate:StoreLocation` | `string?` | `null` | Certificate store location (`CurrentUser` or `LocalMachine`) |
| `Certificate:Thumbprint` | `string?` | `null` | SHA-1 thumbprint for certificate store lookup (throws `InvalidOperationException` at startup if configured thumbprint is not found, preventing plaintext fallback) |
| `Cors:AllowAll` | `bool` | `true` | When true, permits all origins to request static assets |
| `Cors:AllowedOrigins` | `string[]` | `[]` | Explicit origin allowlist (when `AllowAll = false`) |
| `Cors:AllowedMethods` | `string[]` | `[]` | Allowed HTTP methods (`[]` means all) |
| `Cors:AllowedHeaders` | `string[]` | `[]` | Allowed HTTP headers (`[]` means all) |
| `Cors:AllowCredentials` | `bool` | `true` | Allow credentials in CORS requests |
| `ForwardedHeaders:Enabled` | `bool` | `true` | Enable ASP.NET Core Forwarded Headers middleware |
| `ForwardedHeaders:TrustAllProxies` | `bool` | `false` | Trust all reverse proxies (use in private container networks only) |
| `ForwardedHeaders:KnownProxies` | `string[]` | `[]` | Trusted reverse proxy IP addresses |
| `ForwardedHeaders:KnownNetworks` | `string[]` | `[]` | Trusted reverse proxy CIDR subnets |
| `SecurityHeaders:EnableHsts` | `bool` | `false` | Enable HTTP Strict Transport Security (HSTS) |
| `SecurityHeaders:HstsMaxAgeDays` | `int` | `365` | HSTS max-age duration in days |
| `SecurityHeaders:HstsIncludeSubdomains` | `bool` | `true` | Include `includeSubDomains` directive in HSTS header |
| `SecurityHeaders:HstsPreload` | `bool` | `false` | Include `preload` directive in HSTS header |
| `SecurityHeaders:XContentTypeOptions` | `string` | `"nosniff"` | `X-Content-Type-Options` header |
| `SecurityHeaders:XFrameOptions` | `string` | `"SAMEORIGIN"` | `X-Frame-Options` clickjacking protection |
| `SecurityHeaders:ReferrerPolicy` | `string` | `"strict-origin-when-cross-origin"` | `Referrer-Policy` header |
| `SecurityHeaders:ContentSecurityPolicy`| `string?`| `null` | Custom CSP header string |
| `SecurityHeaders:PermissionsPolicy` | `string?` | `null` | Custom `Permissions-Policy` header string |
| `Compression:EnableCompression` | `bool` | `true` | Enable Brotli + Gzip response compression |
| `Compression:EnableForHttps` | `bool` | `true` | Enable compression on HTTPS responses |
| `StaticFiles:FrameworkCacheMaxAgeSeconds` | `int` | `31536000` | Cache-Control `max-age` for `_framework/*` binaries (1 year, immutable) |
| `StaticFiles:HtmlCacheMaxAgeSeconds` | `int` | `0` | Cache-Control `max-age` for `index.html` (0 = no-cache for instant updates) |
| `HealthCheck:Enabled` | `bool` | `true` | Enable health check endpoint |
| `HealthCheck:Path` | `string` | `"/health"` | Health check endpoint route |

---

### Multi-Server Setup Guide

Follow this guide to deploy Server A (Client Host) and Server B (API Engine) on separate physical or virtual machines.

#### Step 1: Configure Server B (API Backend)

On Server B (`192.168.1.20` / `api.internal`):
1. Open `SemanticAgentBroker.Api/appsettings.json`.
2. Configure `RestApi:Port` to your desired port (e.g. `5100`).
3. Configure `AgentSystem:Cors` to allow requests from Server A:

```json
{
  "AgentSystem": {
    "RestApi": {
      "IsEnabled": true,
      "Host": "0.0.0.0",
      "HttpPort": null,
      "HttpsPort": 5100,
      "Port": 5100
    },
    "Cors": {
      "AllowAll": false,
      "AllowedOrigins": [
        "https://192.168.1.10:7050",
        "https://client.internal:7050"
      ]
    }
  }
}
```

4. Launch Server B:
```bash
cd SemanticAgentBroker.Api
dotnet run --configuration Release
```

#### Step 2: Configure Server A (Client Host)

On Server A (`192.168.1.10` / `client.internal`):
1. Open `SemanticAgentBroker.ClientHost/appsettings.json`.
2. Set `ClientSettings:ApiBaseUrl` to Server B's address:

```json
{
  "ClientHost": {
    "Host": "0.0.0.0",
    "HttpPort": null,
    "HttpsPort": 7050,
    "ClientSettings": {
      "ApiBaseUrl": "https://192.168.1.20:5100/"
    }
  }
}
```

3. Launch Server A:
```bash
cd SemanticAgentBroker.ClientHost
dotnet run --configuration Release
```

#### Step 3: Verify Connectivity

1. From a client machine, navigate to `https://192.168.1.10:7050` in a web browser.
2. The browser loads the Blazor WASM client from Server A.
3. The client fetches `/appsettings.json` and connects to `https://192.168.1.20:5100`.
4. Open the browser's developer console (`F12`):
   - Check the **Network** tab: REST requests (`/api/auth/validate`, `/api/tasks`) and SignalR WebSockets (`/loghub`) connect directly to Server B.
   - Status 200 OK and 101 Switching Protocols confirm successful cross-origin communication.

---

## Advanced Features

### Structured State Tracking

When `EnableStructuredStateTracking` is `true`, the engine maintains a `_task_state.md` file in the workspace. The `TaskStateManager` builds a deterministic context snapshot that includes:

- **Objective** - the original user prompt
- **Completed steps** - tiered by recency (collapsed → summarized → full detail)
- **Key facts** - extracted data points and references
- **Files manifest** - current workspace contents
- **Active plan** - the approved implementation plan (if any)
- **Compaction count** - how many times context has been compacted
- **Crash-Resilient Atomic Writes** - both `_task_state.json` and `_task_state.md` are written using an atomic temporary-file pattern (`.tmp` staging followed by `File.Move(..., overwrite: true)`), guaranteeing state files are never truncated or corrupted if an application crash or power loss occurs mid-write.
- **Guaranteed Workspace Materialization** - All workspace file persistence across `TaskStateManager` (`_task_state.json`, `_task_state.md`, `_walkthrough.md`) and `ReactExecutionEngine` (`_runtime_plan.md`, large answer auto-save `final_result.md`) guarantees physical directory creation on disk before writing, preventing `DirectoryNotFoundException` if the workspace directory was not previously initialized or was cleaned up.

This approach ensures context continuity across long-running tasks and enables task recovery after app restarts.

### Thread-Safe Async Trace Logging

`TraceLoggingContext` utilizes `AsyncLocal<bool>` and `AsyncLocal<Guid?>` to isolate trace logging state per asynchronous execution flow. When a task runs with `TraceMode = true`, its outgoing HTTP requests to LLM provider endpoints are logged to timestamped files tagged with the task ID (e.g. `20260814_140000_123_{TaskId}_{ProviderId}_{RequestId}.log`) without cross-task contamination or static race conditions. If an HTTP request is aborted, timed out, or encounters a network error, `TraceLoggingDelegatingHandler` logs a terminal marker (`=== CANCELLED / TIMED OUT ===` or `=== EXCEPTION ===`) using unconstrained tokens before rethrowing to preserve the complete audit trail without swallowing cancellation tokens.

### Context Budget Management

Long-running tasks with many tool calls can exceed the LLM's context window. The system manages this through:

1. **Token counting** - ML.Tokenizers provides accurate BPE token counts for each context injection
2. **Tiered observation display** - Old steps are progressively collapsed, medium steps show truncated summaries, and only the most recent steps include full observations
3. **Structural trimming** - When the context exceeds `MaxStateInjectionTokens`, observations are removed from the oldest steps first. The context string is rebuilt from the trimmed data model (not via string manipulation)
4. **Overhead calculation** - The engine computes token budget by subtracting system prompt tokens and `SubagentExecutor.ReasoningFramingText` overhead from the model's max context window

### Plan Review Workflow

Both execution modes go through a planning phase:
1. Agent generates an implementation plan
2. **Auto mode**: Plan is auto-approved immediately
3. **Planned mode**: Plan is stored in the database and broadcast via SignalR
   - User can **Approve**, **Modify** (edit the plan markdown), **Reject** (with feedback), or **Ask a question**
   - On rejection, the agent regenerates the plan incorporating feedback
   - Plans are versioned - all revisions are stored
4. The approved plan is appended to the system prompt for execution

### Follow-up / Continuation Tasks

Completed tasks support continuation via `POST /api/tasks/{id}/proceed`:
- Previous task state (`_task_state.md`, `_task_state.json`) and workspace files are injected as context.
- The new prompt is appended as a follow-up objective to the existing task state.
- Step numbering continues monotonically from the previous maximum step (e.g. continuing after step 5 resumes at step 6).
- Step limit budgeting (`maxSteps`) applies relative to the continuation run (unbounded by default if omitted).
- The agent continues in the same workspace without recreating existing files.
- Chat history is preserved across continuations.

### Load Balancing

When multiple connections serve the same model:
- **Least-used** strategy distributes requests across healthy endpoints based on real-time active request counters
- **Case-Insensitive Resolution**: Provider connection IDs and model IDs are matched and tracked case-insensitively across configuration, load balancing, health monitoring, and kernel construction
- **Health checks** run periodically to detect unhealthy endpoints. Endpoints start under a pessimistic assumption (unhealthy until the first successful probe), preventing early routing to unresponsive endpoints
- **Disabled Health Checks**: When `HealthCheck:Enabled` is set to `false`, periodic probing is bypassed and all configured provider connections are treated as healthy without warning fallbacks
- **Automatic failover** routes around failed connections

### Multi-Action Steps

The SubagentExecutor supports parsing multiple `Action:`/`ActionInput:` pairs from a single reasoning response. When multiple actions are returned:

- **Safe actions** (methods where `IsDangerous == false`) are executed in parallel
- **Dangerous actions** are presented to the user for individual approval and executed sequentially
- The step counter advances per reasoning turn (`step++`), allowing multiple parallel safe actions or approved dangerous actions to execute within a single reasoning step without artificially consuming subsequent reasoning turns
- `Action: None` responses are filtered out and treated as plan-only steps
- **Script Draft Collapse**: When reasoning models output multiple iterations or draft blocks calling `PythonSandbox-ExecutePythonScript` in a single response, `SubagentResponseParser` collapses them to the last action, executing only the final script block rather than executing preliminary draft attempts.

### JSON Self-Repair & Unicode Normalization

LLM-generated JSON (especially from local models like Ollama/vLLM) is frequently malformed or corrupted by tokenizer byte artifacts. The `JsonSanitizer` provides a multi-stage repair and normalization pipeline:

1. **Markdown code fence stripping** - Extracts payload from enclosing ```` ```json ... ``` ```` or ```` ``` ... ``` ```` fences before parsing.
2. **Comment stripping** - Strips single-line (`// ...`) and multi-line (`/* ... */`) comments outside strings while preserving URLs (e.g. `https://`).
3. **Trailing semicolon trimming** - Trims trailing semicolons `;` appended by code generation models.
4. **Unicode & Byte-Fallback Normalization** - Replaces non-breaking spaces (`\u00A0`, `\u202F`, etc.) with ASCII spaces, strips zero-width spaces (`\u200B`, `\uFEFF`), decodes literal JSON unicode escapes, decodes URL-encoded character and quotation sequences (e.g. `%22` -> `"`), and repairs tokenizer byte-fallback path corruptions (e.g. `/app/<0xC2>dir/` -> `/app/workdir/`).
5. **Curly quote normalization** - Replaces smart/curly quotes (`“`, `”`, `‘`, `’`, `«`, `»`, `„`) with standard ASCII quotes.
6. **Single-quoted JSON repair** - Converts Python-style single-quoted dictionaries into valid JSON double quotes while safely repairing escaped apostrophes (`\'` → `'`) and detecting word-internal contractions (`it's`, `don't`).
7. **Python & Float literal normalization** - Translates unquoted Python booleans and nulls (`True` → `true`, `False` → `false`, `None` → `null`) and non-standard float literals (`NaN`, `Infinity`, `-Infinity` → `null`) outside strings.
8. **Unquoted object key quotation** - Quotes bare JavaScript / JSON5 object keys (`{action: "run", params: {id: 123}}` → `{"action": "run", "params": {"id": 123}}`).
9. **URL-encoded sequence decoding** - Decodes safe URL-encoded sequences (`%22`, `%27`, `%0A`, `%0D`, `%09`, `%25`, `%20`).
10. **Invalid escape sequence & trailing backslash repair** - Normalizes illegal `\'` escape sequences inside double-quoted strings to plain `'`, escapes invalid escape sequences like `\Users`, and repairs unescaped trailing backslashes immediately preceding closing quotes (e.g. `"path": "C:\test\"}`).
11. **Control character escaping** - Escapes unescaped literal newlines, tabs, and carriage returns within JSON strings.
12. **String-boundary-aware trailing comma removal** - Strips trailing commas before `}` or `]` outside string literals without corrupting string content.
13. **LIFO bracket balancing & stream truncation repair** - Closes unclosed string literals (handling escape characters), trims trailing commas outside strings on cut-off outputs, and appends missing closing braces and brackets in strict LIFO order using a delimiter stack.
14. **Structural quote analysis** - Detects interior quotes in string values and automatically escapes them.
15. **Heuristic key-value extraction** - Regex fallback supporting hyphenated, dotted, or unquoted property names operating on normalized JSON input.
16. **LLM fallback repair** - A dedicated, low-temperature LLM call to fix complex JSON structures that the fast path couldn't repair.

### Reasoning Truncation Recovery & Fallback

When using local models or complex prompts, an LLM's internal reasoning phase may occasionally consume its entire output token budget (`finish_reason: "length"`) or enter a repetitive thinking loop before outputting an action. Instead of immediately failing the task:

1. **Tier 1: Nudged Brevity Retry** - The engine initiates a retry with `NudgedExecutionPromptSettings` (elevated temperature/repetition penalties) and appends an explicit system brevity nudge (`[SYSTEM ALERT] Keep your Thought to 1-2 brief sentences and IMMEDIATELY output Action and ActionInput`).
2. **Tier 2: Single-Call Tool Invocation Fallback** - If reasoning continues to truncate or return empty actions, the engine automatically falls back to single-call direct tool calling without the detached reasoning step.
3. **Thought Loop Deduplication** - `SubagentResponseParser` collapses degenerate repeating thought lines to prevent polluting logs and future context windows.

### Atomic Task Creation and Workspace File Uploads

Tasks can be created with initial workspace files attached via a single multipart `POST /api/tasks` request, and workspace files can be added during execution via `POST /api/tasks/{id}/workspace/upload`. Both endpoints enforce strict **upfront atomic batch validation and compensating rollback**:
- All uploaded file names, path traversal patterns (`..`, forbidden separators), and file sizes (`MaxUploadSizeMb`) are validated across the entire batch before creating workspace directories or staging any files to disk.
- If any file in the batch fails validation, the entire upload is rejected atomically with `400 BadRequest`, preventing orphaned or partially written files on disk.
- **Compensating Workspace Rollback**: If a database error or constraint conflict occurs after staging workspace files, the endpoint automatically executes compensating cleanup to recursively remove the staged directory from disk, ensuring zero orphaned workspace folders.

### Granular Workspace RBAC Enforcement

All workspace operations enforce effective permissions:
- **File Upload** (`POST /api/tasks/{id}/workspace/upload`): Requires the `UploadFiles` permission option for non-creator users.
- **File Listing & Download** (`GET /api/tasks/{id}/workspace` & `GET /api/tasks/{id}/workspace/{fileName}`): Requires Admin, task Creator, or group `AllowedTasks` membership (for non-private tasks).
- **Text File Editing** (`PUT /api/tasks/{id}/workspace/{fileName}`): Requires the `EditFiles` permission option and enforces `MaxFileEditSizeMb` limits.
- **File Deletion** (`DELETE /api/tasks/{id}/workspace/{fileName}`): Requires the `EditFiles` permission option.
- **Custom Agent Tasks**: For tasks created from custom agents (`SourcePluginId == null`), the creator is automatically granted full workspace operation permissions.
- **Agent-Side System File Protection**: The agent's `FileIOPlugin` prevents the AI from directly modifying or deleting internal system state files (prefixed with `_`, e.g. `_task_state.json`, `_task_state.md`, `_runtime_plan.md`). Human operators can freely edit these files via workspace endpoints/UI to steer task state or inspect progress.

### Multi-Database Architecture & SQLite WAL Concurrency

The broker isolates data across three specialized databases:
- `broker.db` (`AppDbContext`) - Task sessions, event sourcing logs, plugin registrations, and agent definitions.
- `auth.db` (`AuthDbContext`) - User accounts, group memberships, task-level RBAC permissions, and brute-force lockout records.
- `tokens.db` (`TokenDbContext`) - Active persistent session tokens, browser fingerprints, and expiration timestamps.

To ensure high concurrency without lock contention (`SQLITE_BUSY`), all SQLite databases automatically initialize in **Write-Ahead Logging (WAL)** mode (`PRAGMA journal_mode=WAL;`) with a 5000ms busy timeout (`PRAGMA busy_timeout=5000;`). This enables non-blocking concurrent readers alongside active write operations.

### Workspace File Concurrency Synchronization & Lock Lifecycle

To prevent race conditions and file corruption when agents and human operators interact with workspace files simultaneously, the platform integrates `IWorkspaceLockManager` and `ITaskStateManager`:
- Granular asynchronous semaphore locks coordinate reads, uploads, edits, and deletions per task and per file.
- Locks are automatically released upon request completion and cleaned up when task sessions terminate.
- **Mutual Exclusion and Cancellation Safety**: In `WorkspaceLockManager`, when a file lock acquisition is cancelled while waiting on a semaphore, the active lock count is safely decremented and any waiting whole-workspace lock is transitioned with mutual exclusion flags intact, preventing race conditions between newly arriving file locks and the exclusive workspace lock.
- **Comprehensive Session Cleanup**: `CleanupSession(taskId)` is invoked across both `IWorkspaceLockManager` and `ITaskStateManager` during single task deletion, bulk task deletion (up to 500 tasks per batch), task restarts, and automated retention cleanup sweeps (`TasksCleanupService`), ensuring zero memory leaks or dangling file locks for purged tasks. In `WorkspaceLockManager`, deferred resource disposal ensures that active in-flight file lock operations complete safely without `ObjectDisposedException` and dispose underlying semaphores upon final lock release.
- **Deadlock-Free Dispatch Pause/Resume Synchronization**: In `AgentTaskQueueService`, dispatch synchronization validates both in-memory pause gates via `taskOps.IsPausedAsync` and fresh database state, eliminating TOCTOU race conditions where tasks resumed by users while queued would otherwise be falsely re-paused by stale local snapshots and deadlock indefinitely.
- **Synchronous Task Registration & Coordinated Control Awaiting**: In `AgentTaskQueueService` and `TaskEndpoints`, task registration in `TaskOperationsService` occurs synchronously upon tracking active tasks before worker dispatch. The addition of `queue.WaitForTaskCompletionAsync` alongside `taskOps.WaitForTaskCompletionAsync` ensures that control endpoints (`TaskRestart`, `TaskProceed`, `DeleteTask`) reliably await background execution completion before clearing state or workspaces.
- **Approval Gate Cancellation & Idempotent Task Registration**: In `TaskOperationsService`, `UnregisterTask` explicitly cancels any pending `_approvalGates` `TaskCompletionSource`, ensuring awaiting callers never hang indefinitely when a task terminates. `RegisterTask` cleanly disposes prior CTS instances, signals stale completion gates, completes channel writers, and cancels stale approval gates upon task re-registration while preserving pre-registration pause gates established while queued. In `CancelTask`, `AggregateException` handling ensures that pause gates, approval gates, and plan decision channels release even if individual cancellation callbacks throw.
- **Universal ChangeTracker Concurrency Protection**: In `AgentTaskStore`, all state transitions and entity modifications (`UpdateStateAsync`, `SetFinalResultAsync`, `ApprovePlanAsync`, `LogEventAsync`, `SaveChatMessageAsync`, `SavePlanAsync`) catch `DbUpdateException` (including `DbUpdateConcurrencyException`), cleanly clearing `ChangeTracker` without poisoning the `DbContext` or crashing callers during concurrent task deletions.
- **Symmetric Concurrency Semaphore Tracking & Defensive Capacity Configuration**: In `PlanningEngine`, `ReactExecutionEngine`, and `AgentTaskQueueService`, temporary release of the execution concurrency permit (`SemaphoreSlim _executionSemaphore`) during human review, dangerous tool approval, or user pauses is strictly tracked via a local `semaphoreReleased` flag. Re-acquisition in `finally` blocks executes *if and only if* a permit was successfully surrendered to the pool, preventing self-deadlocks on single-agent limits (`MaxConcurrentAgents = 1`), permanent permit leaks/concurrency exhaustion on multi-agent pools, and unhandled `ObjectDisposedException` instances during host shutdown. Additionally, `AgentTaskQueueService` defensively clamps `MaxConcurrentAgents` to `Math.Max(1, systemOptions.MaxConcurrentAgents)` during initialization to prevent application startup crashes on non-positive configuration values.
- **Cross-Platform Path Normalization**: All workspace endpoints, `FileIOAgentPlugin`, and `ToolExecutionService` normalize both Windows (`\`) and Unix (`/`) directory separators across platforms. Directory components are stripped using `Path.GetFileName`, and directory traversal attempts (`..`, leading separators, and suspicious paths) are strictly rejected.
- **Transparent Workspace File Parameter Resolution & Symlink Defense**: Tool execution parameters targeting workspace files using `@/app/workdir/{fileName}` or `@\app\workdir\{fileName}` prefixes are transparently resolved and loaded from the task workspace directory with path traversal and symbolic link target containment checks to prevent symlink-based workspace escapes.
- **Guaranteed Load Balancer Connection Release**: In `AgentTaskQueueService`, the entire agent kernel initialization and dynamic plugin resolution lifecycle is wrapped in defensive error handling guaranteeing that leased load balancer connection slots are promptly released back to the pool if kernel building or plugin configuration queries fail.
- **Terminal State Cancellation Guards**: In `TaskQueue` (`CanCancel`), the Cancel action is strictly guarded and hidden for all terminal states (`Completed`, `Failed`, `Cancelled`). This guarantees that tasks that have terminated cannot be transitioned back to `Cancelled` or inadvertently trigger workspace deletion sweeps, preserving failure diagnostics and terminal execution history.

### Robust Diagnostics & Global Error Handling

- **Global Exception Middleware**: Unhandled exceptions are intercepted by `app.UseExceptionHandler()`, returning sanitized RFC 7807 `ProblemDetails` responses to clients to prevent internal stack trace leakage.
- **Windows Event Log**: On Windows hosts, unhandled errors and critical system events automatically forward to the Windows Application Event Log (`SemanticAgentBroker` source) for Event Viewer inspection.
- **Linux systemd Journal (`journald`)**: On Linux hosts and containers, logging automatically switches to `AddSystemdConsole()`, embedding syslog priority levels (e.g. `<3>` for Error, `<4>` for Warning, `<6>` for Info) for native filtering via `journalctl -u semanticagentbroker -p err`.

---

## Built-in Plugins Reference

> **Note**: Plugins marked as **[Required]** (`BuiltInRequired`) are automatically injected into every agent and task session to ensure core system functionality. Plugins marked as **[Optional]** are disabled by default and are never auto-injected; they must be enabled by an administrator and either explicitly bound to an agent via `MethodBindings` or directly referenced by a plugin task via `ExternalMethods` or `RequiredPlugins`.

### PythonSandbox [Required]
Executes Python 3 scripts in a network-disabled Docker container.

| Method | Parameters | Description |
|--------|-----------|-------------|
| `ExecutePythonScript` | `script` (string) | Runs Python code in sandbox |

**Config:** `ImageName`, `TimeoutSeconds`, `MemoryLimitMb`, `MaxOutputMb`, `DockerUri`, `ContainerDataPath`, `CpuQuota`, `CpuPeriod`

> **Output Limit Calculation**: Output streaming uses `CalculateMaxOutputBytes` with 64-bit integer arithmetic, safely clamping limits between 1 MB and `int.MaxValue` without integer overflow when large `MaxOutputMb` values (>= 2048 MB) are configured.
>
> **Pre-Flight Script Sanitization**: `PythonScriptSanitizer` automatically sanitizes scripts before container execution, repairing common local LLM syntax mistakes:
> - Appends missing trailing colons (`:`) to compound statement headers (`for`, `if`, `elif`, `else`, `while`, `try`, `except`, `finally`, `def`, `class`, `with`) when delimiter brackets and parentheses are balanced.
> - Corrects file open mode typos (e.g. `open(..., 'n')` -> `open(..., 'w')`).
> - Normalizes single-quoted f-strings in Python 3.12 by converting inner single quotes inside `{...}` expressions to double quotes and repairing accidental quote-dot variable interpolations (`{'.var'}` -> `{var}`).
> - Cleans up loop target token stutters (e.g. `for s in filtered 'filtered_sensors':` -> `for s in filtered_sensors:`).
>
> **Exit Code Verification & Diagnostic Hints**: Inspects the container's exit code (`StatusCode`) and output streams. Non-zero exit codes return `Error: Python script execution failed (exit code N):` along with targeted diagnostic hints for local LLM syntax, indentation, colon omission, file mode, and division-by-zero errors. Furthermore, if a script exits with status 0 but contains a caught or dumped exception traceback, it is classified as an execution error rather than being mislabeled as a benign warning. Benign stderr output without tracebacks (e.g. Pandas deprecation notices) returns `Warnings:`.
>
> **Scripting Prompt Best Practices & Schema Reflection**: In `AgentBuilder`, tool declarations in system prompts automatically include `Returns: <ReturnDescription>` metadata to provide explicit schema visibility (e.g. sensor property names and casing). System prompt instructions explicitly guide LLMs to:
> - Avoid wrapping top-level script execution in broad `try ... except Exception: pass` blocks, ensuring true runtime tracebacks and syntax errors are emitted for targeted self-correction.
> - Avoid flooding the console by never printing full DataFrames, entire datasets, or full generated reports to standard output; output only `.head(10)` previews or summaries, and save full reports to `/app/workdir/` files.
> - Emit a single, self-contained Python script per turn instead of multiple iterative script drafts.
> - Format tabular output clearly using `tabulate` or `df.to_markdown()` for structured Markdown reporting.

### FileIO [Required]
Read/write files in the agent's isolated workspace.

| Method | Parameters | Description |
|--------|-----------|-------------|
| `SaveToWorkspace` | `filename`, `content` | Save text file to workspace (supports subdirectories with automatic parent directory creation; validates symlinks and directory containment) |
| `AppendToWorkspace` | `filename`, `content` | Append content to file (supports subdirectories; creates file and parent directories if missing) |
| `ReadFromWorkspace` | `filename` | Read text file (supports subdirectories; truncated for large files) |
| `ListWorkspaceFiles` | *(none)* | Recursively list all files with relative paths and sizes |
| `ViewWorkspaceFile` | `filename`, `startLine`, `endLine` | Read specific line range from a file (supports subdirectories) |
| `GrepWorkspaceFile` | `filename`, `pattern`, `contextLines?`, `maxResults?` | Search file with regex pattern using memory-efficient line streaming and a sliding context buffer |
| `ReplaceInWorkspaceFile` | `filename`, `searchText`, `replaceText` | Find and replace text in file (supports subdirectories while strictly preserving relative path hierarchy; schema marks `replaceText` required, but accepts empty string `""` for deletion; rejects empty `searchText`; guarded against files exceeding 50 MB) |

**Config:** `MaxReadChars`

### SystemInfo [Required]
Provides current date/time information essential for time-aware tasks.

| Method | Parameters | Description |
|--------|-----------|-------------|
| `GetCurrentDateTime` | *(none)* | Returns local date/time with timezone in ISO 8601 format |
| `GetCurrentUtcDateTime` | *(none)* | Returns UTC date/time in ISO 8601 format |

### WorkspaceInfo [Required]
Provides workspace spatial awareness - directory tree, file search, and disk usage.

| Method | Parameters | Description |
|--------|-----------|-------------|
| `GetWorkspaceTree` | *(none)* | Returns formatted directory tree with file sizes |
| `SearchFiles` | `pattern` (glob) | Search for files matching a glob pattern (sanitized against directory traversal `'..'` and rooted paths) |
| `GetDiskUsage` | *(none)* | Summarize workspace disk usage and largest file |

**Config:**
- `MaxDepth`: Maximum directory recursion depth (default: `100`)
- `MaxFiles`: Maximum number of files to enumerate before safely bounding disk summary (default: `50000`)

> **Concurrent File System Resilience**: `GetWorkspaceTree` and `GetDiskUsage` are hardened against concurrent agent file modifications, deletions, and locks. Transient missing files, locked files, and inaccessible subdirectories are safely tolerated without throwing unhandled exceptions.

### HttpClient [Optional]
Make HTTP requests to external APIs.

> [!CAUTION]
> **Security Advisory & SSRF Defense**: Because agents can generate arbitrary request URLs, the `HttpClientAgentPlugin` incorporates built-in **Server-Side Request Forgery (SSRF) validation** configured via `AgentSystem:HttpClientSecurity` in `appsettings.json`:
> - **Blocklist Enforcement** (`EnableBlocklist: true` by default): Automatically blocks requests to loopback addresses (`127.0.0.1`, `localhost`, `::1`), private LAN networks (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), RFC 6598 Carrier-Grade NAT (`100.64.0.0/10`), RFC 2544 benchmark networks (`198.18.0.0/15`), IPv6 link-local addresses, and cloud instance metadata services (`169.254.169.254`).
> - **DNS Rebinding Defense**: Socket connections are validated via `SocketsHttpHandler.ConnectCallback` using `NetworkSecurityUtils.IsPrivateOrLoopbackIp`. Even if a malicious domain passes initial URL hostname checks, IP addresses resolved at socket connection time are validated immediately prior to the TCP handshake, eliminating Time-of-Check to Time-of-Use (TOCTOU) DNS rebinding attacks while preserving TLS Server Name Indication (SNI). Direct IP endpoints (IPv4/IPv6 literals like `http://127.0.0.1:11434` or `http://192.168.1.50`) are parsed directly via `IPAddress.TryParse` without triggering redundant DNS lookups.
> - **Allowlist Enforcement** (`EnableAllowlist`): When enabled, restricts outbound HTTP calls strictly to explicitly permitted domain names, IP hostnames, or URL prefixes defined in `AllowedPatterns`. Host patterns (e.g. `api.github.com`, `*.trusted.org`, `api.stripe.com`) match strictly against `uri.DnsSafeHost` and cannot be bypassed via query parameters, URL path segments, or subdomains. Substring matching is strictly prohibited.
> - **Auto-Redirect Protection (`AllowAutoRedirect = false`)**: The HTTP client automatically rejects unvalidated HTTP `301`/`302` redirects, returning the `Location` header to the agent so that redirect targets must pass SSRF validation before being queried.
> - **Bounded Response Streaming**: Both `HttpGet` and `HttpPost` utilize `HttpCompletionOption.ResponseHeadersRead` and stream content via `ReadBoundedStringAsync` up to `MaxResponseChars` (default 8,000 characters). This prevents memory exhaustion and denial-of-service from massive or streaming HTTP payloads.
> 
> Both `HttpGet` and `HttpPost` are explicitly marked as dangerous (`IsDangerous = true`) to require user approval in Planned mode.

| Method | Parameters | Description |
|--------|-----------|-------------|
| `HttpGet` | `url`, `headers?` | GET request (⚠️ dangerous - requires approval & SSRF check) |
| `HttpPost` | `url`, `body`, `contentType?`, `headers?` | POST request (⚠️ dangerous - requires approval & SSRF check) |

**Config:** `TimeoutSeconds`, `MaxResponseChars`

### ComputerVision [Optional]
Process images using vision language models. Supports local workspace image files (`.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.bmp`, `.svg`, `.tiff`) with automatic MIME type classification and remote `http://`/`https://` image URLs.

> [!NOTE]
> Requires the `VisionModel` role configured in `AiProviders:ModelRouting` (or will use the default `IChatCompletionService`). If unconfigured or unavailable, returns a simulated analysis.

| Method | Parameters | Description |
|--------|-----------|-------------|
| `AnalyzeImage` | `imagePathOrUrl`, `prompt` | Describe or extract text from an image (supports local workspace paths with multi-format MIME detection or HTTP/HTTPS URLs) |

---

## Mock Data Plugins Reference

These plugins are designed for testing and provide sample datasets.

### SampleData [MockData]
Provides mock datasets for testing.

| Method | Parameters | Description |
|--------|-----------|-------------|
| `GetSalesData` | `format?` (csv/json) | 200 sales records (auto-saved to workspace file when exceeding threshold) |
| `GetServerLogs` | *(none)* | 100 timestamped log entries (auto-saved to workspace file when exceeding threshold) |
| `GetUserList` | *(none)* | 25 user profiles (JSON) |
| `GetProductCatalog` | *(none)* | Product catalog with 4 categories |

#### Tasks Exported by `SampleData`

| Task ID | Task Name | Allowed Methods | Description |
|---------|-----------|-----------------|-------------|
| `SalesAnalysis` | Sales Data Analysis | `GetSalesData` | Fetch mock sales dataset and perform comprehensive statistical and KPI analysis |
| `ServerLogDiag` | Server Log Diagnostic | `GetServerLogs` | Retrieve latest server logs, identify critical errors, and summarize application health |

### SampleSensors [MockData]
Provides mock IoT sensor data for testing.

| Method | Parameters | Description |
|--------|-----------|-------------|
| `GetSensors` | `sensorType` *(optional)* | List all available sensors, optionally filtered by type |
| `SearchSensors` | `query` | Fuzzy search sensors by name, room number, or building |
| `GetReadings` | `sensorIdsOrFilePath`, `fromUtc`, `toUtc` | Get readings for sensor(s) within a date range (max 7 days) |

Supports batch retrieval via comma-separated IDs or file path references (e.g., `@/app/workdir/ids.txt`).

#### Tasks Exported by `SampleSensors`

| Task ID | Task Name | Allowed Methods | Description |
|---------|-----------|-----------------|-------------|
| `SensorAnalysis` | Sensor Data Analysis | `GetSensors`, `SearchSensors`, `GetReadings` | Multi-step sensor telemetry workflow: fuzzy search, date resolution, batch readings, and Python analysis |

### SampleImages [MockData]
Provides task definitions for image analysis workflows using the ComputerVision plugin.

This plugin exports no standalone methods. It defines the following task:

| Task | Name | Tools | Description |
|------|------|-------|-------------|
| `AnalyzeWorkspaceImages` | Analyze Uploaded Images | `ComputerVision-AnalyzeImage` | Finds images in the workspace and describes them using Computer Vision |

Requires the `ComputerVision` plugin as an enabled dependency (configured via `RequiredPlugins => ["ComputerVision"]` and `ExternalMethods = [ new ExternalMethodRef { PluginId = "ComputerVision", MethodId = "AnalyzeImage" } ]`).

## Example Scenarios

### Scenario 1: Sales Data Analysis (Auto)

**Agent:** DataAnalyst | **Mode:** Auto

```
Calculate the standard deviation and mean of the Revenue column.
First fetch the sales data, save it as CSV, then analyze it with Python.
```

Flow: `SampleData-GetSalesData` → `FileIO-SaveToWorkspace` → `PythonSandbox-ExecutePythonScript` → Result

### Scenario 2: Sensor Report with Plan Review (Planned)

**Plugin Task:** SampleSensors / SensorAnalysis (or custom agent with `SampleSensors` bindings) | **Mode:** Planned

```
Generate a temperature report for all sensors showing average, max, and min
temperatures from yesterday. Save as a formatted markdown report.
```

Flow: Plan generated → User reviews → Approved → `SampleSensors-GetSensors` → `SampleSensors-GetReadings` → `PythonSandbox-ExecutePythonScript` → `FileIO-SaveToWorkspace` → Report

### Scenario 3: Multi-Action Step (Auto)

**Agent:** DataAnalyst | **Mode:** Auto

```
Fetch the sales data and product catalog simultaneously, then correlate them.
```

In this scenario, the agent may emit two actions in a single reasoning step:
```
Action: SampleData-GetSalesData
ActionInput: {"format": "csv"}
Action: SampleData-GetProductCatalog
ActionInput: {}
```

Both safe actions execute in parallel, and their observations are injected into the next reasoning step together. The step counter advances once per reasoning turn (step++), avoiding artificial consumption of reasoning turns.

### Scenario 4: Interactive Data Transformation (Auto)

**Agent:** GeneralAssistant | **Mode:** Auto

```
Fetch the user list and transform it into XML format.
```

Mid-execution, send: *"Wait, actually make it CSV instead."*
The agent reads your message and adapts its approach.

### Integration Client Samples (`Samples/` Directory)

The `Samples/` folder provides complete, standalone runnable integration examples demonstrating how to interact with Semantic Agent Broker programmatically:

1. **`Samples.Common`** - Reusable client library featuring `SemanticAgentBrokerClient`, thread-safe `TokenService`, and transparent 401 retry `AuthHandler`.
2. **`Samples.RestApiIntegrationExample`** - Complete request-await-fetch lifecycle:
   - Submits task via `CreateTaskAsync`
   - Awaits completion via `AwaitTaskCompletionAsync` polling loop
   - Extracts `FinalResult`
   - Retrieves audit events via `GetTaskEventsAsync`
   - Inspects and downloads workspace output artifacts via `ListWorkspaceFilesAsync` & `GetWorkspaceTextFileAsync`
   - Demonstrates session continuation via `ProceedTaskAsync`
3. **`Samples.TaskManagementExample`** - Administrative task lifecycle operations:
   - Aggregated system statistics via `GetTaskStatsAsync`
   - Paginated task query via `ListTasksAsync`
   - Mid-flight pause (`PauseTaskAsync`) and resume (`ResumeTaskAsync`)
   - Re-running finished tasks with clean slate (`RestartTaskAsync`)
   - Single task deletion (`DeleteTaskAsync`) and bulk purging (`BulkDeleteTasksAsync`)
4. **`Samples.RealTimeStreamingExample`** - Real-time event streaming and Human-in-the-Loop client via SignalR (`/loghub`):
   - Streams thoughts (`AgentThought`), tool telemetry (`ToolExecution`), and status changes (`TaskStateChanged`)
   - Simulates human plan approval for Planned mode (`ApprovePlan`)
   - Sends mid-flight user directives (`SendInterruption`)
5. **`Samples.FileIngestionExample`** - Atomic file staging and artifact retrieval:
   - Atomically stages input files on creation via multipart `POST /api/tasks`
   - Supplementary workspace file uploads via `POST /api/tasks/{id}/workspace/upload`
   - Downloads generated reports from workspace

For complete documentation, prerequisites, and CLI run commands, see [`SemanticAgentBroker/Samples/README.md`](SemanticAgentBroker/Samples/README.md).

---

## Testing and Quality Assurance

Semantic Agent Broker includes a comprehensive automated test suite (`SemanticAgentBroker.UnitTests`) (1000+ tests across Core, API, Client, Plugins, Shared, and SDK) verifying core reasoning algorithms, security and authentication rules, API endpoints, plugin contracts, background maintenance services, and concurrency locks.

### Test Architecture & Design Philosophy

The testing framework is built around three core principles:
1. **Full Isolation**: Tests never read from or write to production databases, configuration files, or user workspace directories.
2. **High Concurrency & Speed**: Tests execute in parallel with method-level granularity (`[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]`).
3. **End-to-End API Integration**: ASP.NET Core endpoints are tested in-memory against a virtual host with authentic database transactions, authentication middleware, and RBAC rules.

```
┌────────────────────────────────────────────────────────────────────────┐
│                     MSTest Parallel Test Runner                        │
└───────┬───────────────────────────┬────────────────────────────┬───────┘
        │                           │                            │
┌───────▼─────────────────┐ ┌───────▼──────────────────┐ ┌───────▼───────┐
│ CustomWebApplication    │ │ TestDatabaseFixture      │ │ TestWorkspace │
│ Factory<Program>        │ │ - Isolated SQLite in-temp│ │ Fixture       │
│ - In-memory Kestrel host│ │ - Auto schema migration  │ │ - Temp session│
│ - Auth & Bearer helper  │ │ - Clean Dispose teardown │ │   directories │
└─────────────────────────┘ └──────────────────────────┘ └───────────────┘
```

---

### Test Stack & Dependencies

The test suite uses the following libraries:

| Dependency | Version | Purpose |
|------------|---------|---------|
| `MSTest` | `4.5.1` | Test execution engine, test lifecycle attributes (`[TestClass]`, `[TestMethod]`, `[TestInitialize]`), and assertions |
| `Moq` | `4.21.0` | Mocking interfaces, HTTP clients, and external service contracts |
| `Microsoft.AspNetCore.Mvc.Testing` | `10.0.12` | In-memory `WebApplicationFactory<Program>` test server for end-to-end REST API & integration testing |
| `Microsoft.AspNetCore.SignalR.Client` | `10.0.12` | Real-time WebSocket test client for testing live event streaming, thought broadcasting, and approval gates |
| `Microsoft.EntityFrameworkCore.Sqlite` | `10.0.12` | Isolated SQLite database provider for repository, authentication, and token log testing |

---

### Test Infrastructure & Isolation Fixtures

The `TestInfrastructure` folder provides reusable fixtures that guarantee test repeatability and zero side-effects:

#### 1. `TestDatabaseFixture`
- Allocates a dedicated SQLite test directory under `%USERPROFILE%\SemanticAgentBrokerDataTests\test_{guid}\`.
- Creates fresh instances of `AppDbContext`, `AuthDbContext`, and `TokenDbContext` using `context.Database.EnsureCreated()`.
- Implements `IDisposable` to automatically purge temporary database files upon test completion.

#### 2. `TestWorkspaceFixture`
- Generates isolated workspace sandbox directories (`%USERPROFILE%\SemanticAgentBrokerDataTests\workspaces\{sessionGuid}\`).
- Provides pre-configured `AgentSystemOptions` pointing to the temporary workspace root.
- Used to test file I/O operations, workspace directory locks, and state serialization without risking corruption of host files.

#### 3. `CustomWebApplicationFactory`
- Subclasses `WebApplicationFactory<Program>` to spin up an in-memory ASP.NET Core test server.
- Replaces standard database registrations with isolated SQLite connections from `TestDatabaseFixture`.
- Disables external REST API triggers during tests (`AgentSystem:RestApi:IsEnabled = false`).
- Includes a helper method `CreateAuthenticatedClientAsync(username, role, browserId)` which registers a test user, performs a login request, and sets the returned `Bearer` token and `X-Browser-Id` header on the returned `HttpClient`.

---

### Test Suite Module Breakdown

```
SemanticAgentBroker.UnitTests/
├── Api/
│   ├── Agents/
│   │   └── AgentEndpointsTests.cs                      # REST API agent CRUD, duplication, prompt preview (non-admin & admin), distinct plugin listing, 404 not found paths, and non-admin 403 RBAC
│   ├── Auth/
│   │   ├── AuthEndpointsTests.cs                       # ASP.NET Core integration tests for /api/auth endpoints with Bearer session tokens, 401 unauthenticated paths, lockout message mapping, and session invalidation
│   │   ├── AuthServiceTests.cs                         # Password hashing, token gen/validation, IP striped lockouts, lockout expiration auto-reset, failed attempt cleanup on success, IPv6 matching, token pruning, concurrent expired token cleanup safety, and activity cache memory leak prevention
│   │   ├── CustomAuthSchemeHandlerTests.cs             # Authentication handler challenge (401), forbidden (403), and no-result paths
│   │   ├── PermissionEvaluatorTests.cs                 # Role permissions, task ownership, wildcard matching, empty permission negative branches, and plugin method authorization
│   │   ├── SignalRAuthMiddlewareTests.cs               # SignalR query string & header token extraction, session binding, 401 challenge
│   │   └── UserAndGroupEndpointsTests.cs               # User & UserGroup CRUD, input validation, non-admin 403 authorization, primary admin protection, task permissions, name conflicts, non-existent 404 handlers, and session revocation
│   ├── Data/
│   │   └── DbContextModelTests.cs                      # EF Core model configuration, cascade deletions, and JSON value converter change tracking for AppDbContext, AuthDbContext, and TokenDbContext
│   ├── Extensions/
│   │   ├── ApplicationInitializationTests.cs           # Startup recovery for orphaned tasks, missing disk plugin synchronization, and default user/agent seeding
│   │   └── EndpointExtensionsTests.cs                  # Centralized API route mapping registration for all protected and public endpoint groups
│   ├── Filters/
│   │   └── AuthFilterTests.cs                          # Endpoint authentication filter, Bearer token extraction, X-Browser-Id, null IP fallback to "unknown", and SessionInfo injection
│   ├── Hubs/
│   │   ├── AgentLogHubTests.cs                         # SignalR hub task authorization, user interruptions, step approvals, unsubscription, and plan review forwarding
│   │   └── SignalRIntegrationTests.cs                  # Real-time WebSocket connection, subscription, event broadcast, step approvals, and plan review/question gates
│   ├── Plugins/
│   │   └── PluginEndpointsTests.cs                     # REST API plugin catalog, config masking, sensitive value preservation, optional toggle, single reload, and hot reload
│   ├── ReAct/
│   │   ├── JsonSanitizerTests.cs                       # Robust LLM JSON parsing, conversational array/object postamble & preamble extraction with candidate evaluation, markdown code fence stripping, backtick template strings, Python sets, in-body semicolons, ellipsis truncation markers, Python triple quotes, C-style and Python hash comment removal, Python tuple conversion, assignment equals (=), unquoted keys (dotted & schema @) & string values in objects/arrays, unquoted URLs & Windows file paths, hex/bin/oct numbers (0x/0b/0o), float leading/trailing dots & scientific exponents, signed leading-dot floats (+.5), 2-digit hex escapes (\xHH), missing comma insertion (multi-line & compact single-line), undefined/nil/Null/NONE normalization, signed NaNs/infinities, leading plus signs, LIFO delimiter balancing with nested container auto-closing, consecutive & trailing comma removal, unclosed string & unicode escape repair (\u with non-hex paths), lookahead quote repair preceding commas, and URL-encoded quote decoding (%22)
│   │   ├── LlmCallServiceTests.cs                      # Outgoing LLM call execution, retries, function calls, max_tokens/length truncation, trace logs, and immediate fail-fast on non-transient HTTP errors (400, 401, 403, 404, 413, 422)
│   │   ├── PlanningEngineTests.cs                      # Plan generation, user review cycles, question clarifications with live revision broadcasting, max revisions, and cancellation semaphore safety
│   │   ├── ReactExecutionEngineTests.cs                # ReAct loop orchestration, dangerous tool approval/rejection, pause/resume semaphore release, subagent errors, continuations, anchoring traversal defense, completion token reserve, ordered multi-action recording, cancellation propagation, and concise-only terminal path anchoring
│   │   ├── SemaphoreOwnershipStateTests.cs             # Atomic task concurrency semaphore state machine, idempotency, multithreaded release/acquire race condition prevention
│   │   ├── SubagentExecutorTests.cs                    # Phase 1 thought parsing, broadcasting, FinalAnswer extraction, JSON repair, nudged retries, fatal LLM exception error recovery, action priority over FinalAnswer, fallback thought broadcasting, and cancellation propagation
│   │   ├── SubagentResponseParserTests.cs              # Phase 1 structured response parser (Thought, Action, FinalAnswer), parameterless action extraction, model prefix/think tag stripping, command argument wrapping, code wrapper safety, hallucinated observation truncation, trailing Action: None stripping, past-tense intent detection, FinalAnswer protection against file path hijacking, standalone token boundary checks, and multi-draft Python script collapse
│   │   └── ToolExecutionServiceTests.cs                # Dynamic method invocation, metadata-only IsDangerous evaluation, unregistered tool safety, structured state marker injection, auto-save threshold, observation truncation, surrogate pair emoji safety, cross-platform @/app/workdir/ & @\app\workdir\ resolution, symlink containment checks, and cancellation token propagation
│   └── Tasks/
│       ├── AgentTaskQueueServiceTests.cs               # Task queueing, semaphore concurrency limit, in-flight queue tracking (IsTaskQueued, IsTaskExecuting, IsTaskInFlight), synchronous TaskOperationsService registration eliminating dispatch race windows, TOCTOU-free queue pause/resume resolution, WaitForTaskCompletionAsync completion awaiting, duplicate task execution rejection (activeTasks.TryAdd), global task execution timeout attribution to TaskState.Failed, cancellation, and load balancer connection release on agent build failure
│       ├── AgentTaskStoreTests.cs                      # Task lifecycle persistence, DB concurrency catches (UpdateStateAsync, SetFinalResultAsync, ApprovePlanAsync, LogEventAsync), filter log forwarding, max sequence calculations, final result updates, retention queries, SignalR group broadcasts, and direct SQL ExecuteDeleteAsync deletions
│       ├── TaskEndpointsCrudTests.cs                   # REST API task creation, listing, detail retrieval, bulk deletion with ExecuteDeleteAsync (capped at 500 tasks per batch), session lock cleanup, and concurrent multi-task wait batching
│       ├── TaskLifecycleEndpointsTests.cs              # REST API cancel, pause, resume, queue pause-resume pause-gate resolution preventing worker hang/deadlock, plan modify/reject/question, and step approval/rejection endpoints
│       ├── TaskOperationsServiceTests.cs               # In-memory pause/resume gates, approval waiters, unregister approval cancellation, re-registration gate cleanup, exception-resilient CancelTask, step rejection feedback reset & unregister cleanup, plan decisions (approve/modify/reject/question), and FIFO interruptions
│       ├── TasksCleanupServiceTests.cs                 # Background task retention deletion, physical workspace folder cleanup, active task protection, restart race safeguards, lock manager session cleanup, and isolated per-task DI scopes
│       └── WorkspaceEndpointsTests.cs                  # REST API workspace file upload/edit/download/delete, cross-platform path normalization, traversal rejection, non-existent task 404 handling, non-multipart 400 validation, upload size bounds, MaxDownloadSizeMb download limits, and binary downloads
├── Client/
│   ├── AboutTests.cs                                   # Blazor About documentation metadata, document listing, and component disposal lifecycle
│   ├── AuthHeaderHandlerTests.cs                       # DelegatingHandler per-request Bearer token & X-Browser-Id attachment, local storage fallback, existing header preservation, JS error resilience
│   ├── ClientAuthServiceTests.cs                       # Blazor client login/logout flow, X-Browser-Id sync, empty token response fallback, non-401 server error handling, and 401/network error recovery
│   ├── ClientComponentModelsTests.cs                   # Blazor client component models and view modes (About.DocInfo record, About.DocViewMode enum)
│   ├── ClientFileUploadGuardTests.cs                   # File upload selection count guard (<= 10 files) in Home and LiveExecution, InvalidOperationException catch, and user alert verification
│   ├── CustomAuthStateProviderTests.cs                 # Claims principal extraction, localStorage batching, batch JS failure key-by-key fallback, JS interop exception fallback, concurrent logout protection
│   ├── InfiniteRetryPolicyTests.cs                     # SignalR reconnection backoff delay schedule, null retry reason handling, and HTTP 401 Unauthorized permanent abort handling
│   ├── InteropServiceTests.cs                          # JS interop module wrappers for clipboard, themes (with themeManager fallback to module/light), dialogs, smooth scrolling, and transient import failure retry
│   ├── LiveExecutionTests.cs                           # Blazor execution monitoring models, chat history mapping, action/parameterless tool extraction, safe indexing guards, concurrent log/chat merging, route navigation state reset on task ID change, and complete SignalR event unsubscription on disposal (OnTokenCountUpdated)
│   ├── MarkdownServiceTests.cs                         # XSS HTML sanitization, Markdig pipeline, async chunked rendering, short async rendering with emojis, GFM tables, and cancellation
│   ├── NavMenuTests.cs                                 # Blazor navigation menu component disposal idempotency, SignalR state change safety, and reconnection loop throttling delay
│   ├── PluginsPageTests.cs                             # Blazor plugin management page IAsyncDisposable implementation and lifecycle disposal safety
│   ├── SignalRServiceTests.cs                          # Client SignalR hub URL construction, browserId query params, default state lifecycle, event handler subscription and invocation, active task tracking, automatic re-subscription on reconnect, disconnected interruption/review action guards, null-safe loop references, and non-blocking reconnect loop with deadlock-free connect lock release and disposal
│   ├── TaskQueueTests.cs                               # Blazor TaskQueue CanCancel state predicate asserting cancellation blocking on terminal states (Failed, Completed, Cancelled) and permission on active states
│   └── UnauthorizedResponseHandlerTests.cs             # DelegatingHandler 401 auto-logout, null RequestUri safety, recursive validation loop prevention
├── Core/
│   ├── Builders/
│   │   └── AgentBuilderTests.cs                        # Fluent agent construction, unbound plugin resolution, system instruction getter, method bindings, prompt branching, and kernel configuration
│   ├── DependencyInjection/
│   │   └── CoreServiceCollectionExtensionsTests.cs     # Core IoC/DI service collection registration, sub-options & scoped filter resolution, path token expansion, and resolution validation
│   ├── Features/
│   │   ├── Agents/
│   │   │   ├── AgentRegistryTests.cs                   # Dynamic agent registration, lookup, duplicate overwrite handling, missing agent removal, and defaults
│   │   │   ├── TaskWorkspacePathTests.cs               # Task workspace path resolution, directory lifecycle, uninitialized context initial state, safe uninitialized cleanup, and fallback handling
│   │   │   └── WorkspaceLockManagerTests.cs            # Hierarchical workspace vs file locking, concurrency control, case-insensitive whitespace and path-separator (`\` vs `/`) normalized file locking, wait cancellation cleanup, atomic workspace lock acquisition, mutual exclusion during file lock wait cancellation, and deferred session cleanup disposing after in-flight locks release
│   │   ├── PluginHost/
│   │   │   ├── PluginExecutionContextTests.cs          # Configuration cascading merge (appsettings, code, DB), kernel vs root scoped service resolution, null kernel fallback, and unregistered service handling
│   │   │   ├── PluginHostTests.cs                      # Dynamic plugin registry discovery, duplicate registration ignoring, missing unregistration safety, context factory, and SK function adapters
│   │   │   ├── PluginLoaderTests.cs                    # Isolated AssemblyLoadContext plugin loading, on-demand AssemblyLoadContext sharing for multi-plugin DLLs, multi-plugin assembly unload prevention when one plugin is loaded, SHA-256 hashing, dynamic unloading, asynchronous unloading (UnloadPluginAsync) preventing thread pool starvation under SemaphoreSlim wait contention, debounced batch file watcher events, load-locked thread-safe unloading, duplicate instance disposal, shared AssemblyLoadContext reference-safe unloading, dynamic scan dependency skipping, and safe subscriber event notification preventing state corruption and improper disposal
│   │   │   └── SemanticKernelPluginAdapterTests.cs     # KernelFunction metadata conversion, IsDangerous reflection overrides, parameter mapping, and kernel service provider forwarding
│   │   ├── TaskState/
│   │   │   └── TaskStateManagerTests.cs                # State context injection, multi-action step observation status matching, Strategy 3 compaction with step header preservation and collapsed sub-lines, compaction counting, step recording, files manifest scanning/formatting with concurrent file deletion resilience, concurrent cleanup safety, semaphore disposal, null-safe key facts handling, and walkthrough generation
│   │   └── TokenCounting/
│   │       ├── MlTokenCounterAdvancedTests.cs          # Environment variable expansion (%TEMP%), concurrent tokenizer instance caching
│   │       └── MlTokenCounterTests.cs                  # ML.Tokenizers BPE and Tiktoken token counting with HuggingFace parsing, empty history and null message safety, invalid fallback divisor sanitization, and fallback calculations
│   ├── Filters/
│   │   └── LogObservationFiltersTests.cs               # SK function invocation, prompt render logging, and invocation exception propagation filters
│   ├── Infrastructure/
│   │   ├── Http/
│   │   │   └── OllamaCompatibilityDelegatingHandlerTests.cs # Ollama compatibility options num_ctx injection, format fallback, and finish reason normalizations
│   │   ├── HttpClientBuilderExtensionsTests.cs         # AI provider HTTP client configuration, handlers, auth, and SSL bypass
│   │   └── HttpHandlerTests.cs                         # Basic auth (with empty username guard) and trace logging delegating handlers with text/event-stream preservation
│   ├── KernelProvider/
│   │   └── KernelProviderServiceTests.cs               # Direct override, load-balanced selection, missing model detection, failure connection release, and Ollama/OpenAiCompatible endpoint requirement validation
│   ├── LoadBalancing/
│   │   ├── EndpointHealthCheckServiceTests.cs          # Health monitor initialization, 200 OK & 500 transitions, untracked endpoint defaults, Ollama context parameter initialization & transient failure retries, endpoint recovery re-initialization, and resilient error recovery preventing background service aborts
│   │   └── LeastUsedConnectionLoadBalancerTests.cs     # LLM connection routing, least-busy load distribution, and unknown connection zero request queries
│   └── Services/
│       ├── ModelRoutingHelperTests.cs                  # Primary and fallback model resolution logic, enabled connection filtering, non-existent sticky provider and missing model fallback
│       ├── PromptSettingsHelperTests.cs                # Temperature, topP, max token, JsonElement values, multi-format stop sequences (JsonElement arrays, string arrays, object lists, single strings), non-OpenAI fallbacks, and 2-parameter parsing
│       └── ProviderResolutionHelperTests.cs            # Primary model provider resolution with sticky provider IDs, model mappings, and disabled connection filters
├── Plugins/
│   ├── ComputerVisionPluginTests.cs                    # Image bounds validation, remote URLs, file:/// URI scheme resolution, traversal clamping, missing required parameters, vision chat invocation & failure handling, AiOptions routing, and empty response handling
│   ├── FileIOAgentPluginTests.cs                       # Built-in FileIO plugin read/write/list/view/grep/replace operations, range bounds, directory creation, path prefix stripping, cross-platform separator normalization, fallback filename sanitization, and defensive optional line parameter handling
│   ├── FileIOPluginGuardTests.cs                       # Grep regex syntax errors, surrounding context line clamping, 50MB file size guard on ReplaceInWorkspaceFile, truncation and accurate truncated match counting
│   ├── HttpClientAgentPluginTests.cs                   # Optional HTTP client plugin GET/POST requests, socket-level DNS rebinding SSRF safety, allowlist bypass defense with strict host vs URL pattern validation, streaming bounded payload limits, network failure capture, parameter validation, and header parsing
│   ├── PluginUnitTests.cs                              # Plugin reflection, discovery, configuration schema, SystemInfo, WorkspaceInfo (tree depth truncation, recursive file search with inaccessible directory tolerance, negative MaxDepth/MaxFiles clamping, concurrent file churn and deletion tolerance during tree and disk usage enumeration, empty/missing/non-matching search, path traversal and rooted pattern rejection), and HttpClientAgentPlugin tests
│   ├── PythonSandboxPluginPruningTests.cs              # Bounded stream output memory limits, truncation warning markers, schema validation
│   ├── PythonSandboxPluginTests.cs                     # Docker multiplex stream parsing, bounded output truncation, host workspace pre-creation, safe initialize catch, unreachable daemon error handling, caught traceback error classification, targeted diagnostic hints, and schema
│   ├── PythonScriptSanitizerTests.cs                   # Pre-flight Python script syntax repair, missing trailing colons on compound statements, 'n' mode correction, f-string quote collision normalization, and loop target token stutter removal
│   └── SamplePluginsTests.cs                           # Mock data plugins (SampleData, SampleSensors with range limits, subdirectory sensor ID reading, cross-platform workspace file reading, date validation, SampleImages, task definitions)
├── Sdk/
│   ├── PluginMethodBaseTests.cs                        # Plugin method reflection, parameter null checks and helper guards, default virtual properties, JsonElement conversions, empty string defaults, custom extensions, SaveLargeResultAsync IWorkspaceLockManager file lock synchronization, and path traversal sanitization with trailing separator normalization
│   └── SdkUnitTests.cs                                 # SDK abstractions, PluginBase context guards, virtual defaults, and parameter validation
├── Shared/
│   ├── Configuration/
│   │   ├── AiProviderOptionsDefaultsTests.cs           # Default options, inference/sandbox/system settings, and profile fallback validation
│   │   ├── ClientHostOptionsTests.cs                   # ClientHost options binding, security headers, CORS, compression, static files, and forwarded headers
│   │   ├── ContextCompactionOptionsTests.cs            # Dynamic context window threshold scaling, message keep count bounds, small context fallback clamping, force compaction limits, and default options values
│   │   └── PathResolutionHelperTests.cs                # Path sanitization, %MYDOCUMENTS% and tilde (~) expansion, in-memory SQLite (:memory:, file::memory:) connection string preservation, quoted connection strings with semicolons and spaces, and directory tree creation
│   ├── ConstantsAndRoutesTests.cs                      # ApiRoutes /api/ prefix enforcement, HubMethodNames reflection validation, and Core constant defaults
│   ├── DomainContractsTests.cs                         # SignalR records, TaskSession event sourcing defaults, DTO bindings, WorkspaceFileUtils text classification, and SelectOptionItem models
│   └── NetworkSecurityUtilsTests.cs                    # Private, link-local, multicast, metadata (169.254.169.254), RFC 6598 CGNAT (100.64.0.0/10), RFC 2544 benchmark (198.18.0.0/15), IPv6 unspecified address (:: / IPv6Any), null-safe validation, and public IP classification for DNS rebinding SSRF defense
├── TestInfrastructure/
│   ├── CustomWebApplicationFactory.cs                  # In-memory ASP.NET Core test server with auth helpers, decoupled background workers, and isolated test SQLite databases
│   ├── TestDatabaseFixture.cs                          # Isolated SQLite database fixture with non-pooled connections (Pooling=False) and lock-free cleanup
│   ├── TestDatabaseFixtureTests.cs                     # Database test fixture lifecycle, isolated SQLite instances, schema initialization, and disposal teardown
│   └── TestWorkspaceFixture.cs                         # Isolated filesystem workspace fixture with lock-free teardown
└── MSTestSettings.cs                                   # Global assembly-level parallelization configuration ([assembly: Parallelize(Scope = ExecutionScope.MethodLevel)])
```

---

### Parallel Test Execution & Concurrency Architecture

The test suite runs with method-level concurrency (`[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]`), executing all 1000+ unit and integration tests simultaneously across available CPU cores:

- **Zero Stop-The-World GC Pauses**: Elimination of synchronous `GC.Collect()` and `GC.WaitForPendingFinalizers()` from test fixture teardown (`TestDatabaseFixture`, `TestWorkspaceFixture`). This prevents CLR thread suspension and thread pool starvation under high parallel test load.
- **Isolated Non-Pooled SQLite Test Databases (`Pooling=False`)**: Database connection strings for test SQLite instances (`broker.db`, `auth.db`, `tokens.db`) configure `Pooling=False`. This guarantees that connections close immediately upon DbContext disposal, eliminating file lock contention and preventing cross-test safe handle invalidation (`SQLitePCL.sqlite3` ObjectDisposedException) during concurrent runs.
- **Bounded Asynchronous Gates**: Integration tests awaiting asynchronous SignalR actions, step approval gates, and plan decision reviews (`WaitForApprovalAsync`, `WaitForPlanDecisionAsync`) utilize bounded cancellation tokens (`TimeSpan.FromSeconds(15)`) rather than unbounded `CancellationToken.None`, preventing tests from hanging indefinitely under scheduling delays.
- **Immediate Task Cancellation Synchronization**: Tests verifying active task cancellation register token cancellation callbacks (`taskCt.Register(() => taskOps.UnregisterTask(taskId))`), allowing asynchronous wait gates (`WaitForTaskCompletionAsync`) to unblock in < 1 ms without timing out.
- **Decoupled Test Host Background Services**: `CustomWebApplicationFactory` unregisters unmocked background hosted services (`AgentTaskQueueService`, `EndpointHealthCheckService`, `TasksCleanupService`), preventing unmocked background loops from triggering extraneous network probes or file locks during REST API and SignalR hub testing.

---

### Running Unit & Integration Tests

#### 1. Run the Entire Test Suite
From the repository root:
```powershell
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj
```

#### 2. Run Tests with Detailed Verbosity
```powershell
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --verbosity normal
```

#### 3. Filter by Specific Test Class
```powershell
# Run only authentication & hub authorization tests (including SignalR state change broadcast)
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --filter "FullyQualifiedName~AuthServiceTests|FullyQualifiedName~AgentLogHubTests|FullyQualifiedName~SignalRAuthMiddlewareTests|FullyQualifiedName~SignalRIntegrationTests"

# Run only ReAct execution engine & LLM parser tests
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --filter "FullyQualifiedName~ReactExecutionEngineTests|FullyQualifiedName~SubagentResponseParserTests"

# Run only background maintenance & task queue tests (including CleanupWorkspacesOnTaskDelete tests)
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --filter "FullyQualifiedName~TasksCleanupServiceTests|FullyQualifiedName~AgentTaskQueueServiceTests"

# Run only workspace locking, Python sandbox, and plugin SDK tests (including SaveLargeResultAsync directory creation)
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --filter "FullyQualifiedName~WorkspaceLockManagerTests|FullyQualifiedName~PythonSandboxPluginTests|FullyQualifiedName~FileIOPluginGuardTests|FullyQualifiedName~PluginMethodBaseTests"

# Run only API endpoint integration tests (task lifecycle, restart state reset, TaskProceed user identity, and workspace extensionless text files)
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --filter "FullyQualifiedName~TaskEndpointsCrudTests|FullyQualifiedName~TaskLifecycleEndpointsTests|FullyQualifiedName~WorkspaceEndpointsTests"

# Run only Blazor Client authentication & UI helper tests (including LiveExecution AwaitingInput input dispatch)
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --filter "FullyQualifiedName~LiveExecutionTests|FullyQualifiedName~CustomAuthStateProviderTests|FullyQualifiedName~MarkdownServiceTests"

# Run shared domain contracts & utilities (including WorkspaceFileUtils text classification)
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --filter "FullyQualifiedName~DomainContractsTests"
```

#### 4. Filter by Module or Namespace
```powershell
# Run all Client module tests (Blazor WASM auth, markdown, HTTP delegating handlers)
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --filter "FullyQualifiedName~SemanticAgentBroker.UnitTests.Client"

# Run all Core module tests
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --filter "FullyQualifiedName~SemanticAgentBroker.UnitTests.Core"

# Run all API module tests
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --filter "FullyQualifiedName~SemanticAgentBroker.UnitTests.Api"

# Run all Plugin module tests
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --filter "FullyQualifiedName~SemanticAgentBroker.UnitTests.Plugins"
```

#### 5. Collect Code Coverage
```powershell
dotnet test SemanticAgentBroker\SemanticAgentBroker.UnitTests\SemanticAgentBroker.UnitTests.csproj --collect:"XPlat Code Coverage"
```

---

<a id="license--attribution"></a>
## License & Attribution

- **License**: Distributed under the terms of the **[MIT License](LICENSE)**.
- **Third-Party Notices**: Full list of third-party open-source components, fonts, and assets can be found in **[NOTICE](NOTICE)**.
- **Synchronizing Package Versions in Docs**: When upgrading NuGet packages, run the automated sync script to update version numbers across `NOTICE` and `DOCUMENTATION.md`:
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\_ps_scripts\update-package-versions.ps1
  ```


