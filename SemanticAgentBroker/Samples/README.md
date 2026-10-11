# Semantic Agent Broker — Integration Samples Guide

This directory contains production-ready integration sample projects demonstrating how external services, backends, and client applications communicate with **Semantic Agent Broker**.

---

## 1. Quick Navigation Matrix

| Sample Project | Category | Primary Focus & Workflow | Transport |
| :--- | :--- | :--- | :--- |
| [`Samples.Common`](Samples.Common) | **Shared SDK Library** | Typed `SemanticAgentBrokerClient`, thread-safe `TokenService`, and automatic 401 retry `AuthHandler` | HTTP & SignalR |
| [`Samples.RestApiIntegrationExample`](Samples.RestApiIntegrationExample) | **Execution & Await** | Submit autonomous task, poll & await final result, audit execution events, download workspace reports, proceed continuation | REST API (JSON) |
| [`Samples.TaskManagementExample`](Samples.TaskManagementExample) | **Lifecycle Management** | Query system execution stats, paginated task listing, pause & resume, restart from scratch, single delete, bulk delete | REST API (JSON) |
| [`Samples.RealTimeStreamingExample`](Samples.RealTimeStreamingExample) | **Real-Time Telemetry** | SignalR WebSocket connection (`/loghub`), live thought streaming, tool execution telemetry, plan review approval, mid-flight directives | SignalR WebSockets |
| [`Samples.FileIngestionExample`](Samples.FileIngestionExample) | **Workspace & Files** | Atomic multipart file staging on creation (`POST /api/tasks`), mid-flight workspace upload, and downloading output artifacts | Multipart & REST |

---

## 2. Shared Client SDK: `Samples.Common`

All executable samples reference `Samples.Common`. Rather than writing raw HTTP calls, JSON serialization, and manual token refresh loops in every application, `Samples.Common` provides the `SemanticAgentBrokerClient`:

```csharp
using Samples.Common.Client;
using SemanticAgentBroker.Shared.Domain;

using var client = new SemanticAgentBrokerClient(
    baseUrl: "https://localhost:5100",
    username: "admin",
    password: "admin",
    browserId: "my-external-service"
);

// 1. Enqueue task
var task = await client.CreateTaskAsync(new AgentTaskRequest
{
    AgentName = "GeneralAssistant",
    Prompt = "Analyze log entries and summarize errors.",
    Mode = ExecutionMode.Auto
});

// 2. Await final result with polling
var completed = await client.AwaitTaskCompletionAsync(
    task.TaskId,
    timeout: TimeSpan.FromMinutes(3),
    pollInterval: TimeSpan.FromSeconds(2),
    onProgress: s => Console.WriteLine($"Status: {s.State}")
);

Console.WriteLine($"Final Result: {completed.FinalResult}");
```

### Key Capabilities of `SemanticAgentBrokerClient`:
- **Thread-Safe Token Caching**: `TokenService` uses `SemaphoreSlim` double-check locking to avoid redundant login requests.
- **Transparent 401 Interception**: `AuthHandler` automatically detects token expiration, requests a new session token, clones the request, and retries.
- **Cross-Platform WebSocket Support**: Configures SignalR `HubConnection` with access tokens, headers, and certificate validation bypass for local development certificates.

---

## 3. Sample Projects Overview

### A. `Samples.RestApiIntegrationExample` (Submit Task & Await Result)
Demonstrates the fundamental request-response integration pattern:
1. Submits an autonomous task (`Mode = ExecutionMode.Auto`).
2. Polls `GET /api/tasks/{taskId}` until reaching a terminal state (`Completed`, `Failed`, `Cancelled`).
3. Extracts and displays `FinalResult`.
4. Retrieves execution audit events via `GET /api/tasks/{taskId}/events`.
5. Lists and downloads generated markdown reports from `GET /api/tasks/{taskId}/workspace`.
6. Sends a follow-up continuation prompt (`POST /api/tasks/{taskId}/proceed`) to continue the session in the same workspace.

**Run Command:**
```powershell
# From SemanticAgentBroker\ subdirectory:
dotnet run --project Samples\Samples.RestApiIntegrationExample

# Or from repository root:
dotnet run --project SemanticAgentBroker\Samples\Samples.RestApiIntegrationExample
```

---

### B. `Samples.TaskManagementExample` (Lifecycle & Administration)
Demonstrates operational task and queue management:
1. Queries system-wide execution statistics (`GET /api/tasks/stats`).
2. Retrieves paginated task listings (`GET /api/tasks?page=1&pageSize=5`).
3. Demonstrates the **Pause & Resume** workflow: pauses a task mid-flight (`POST /api/tasks/{id}/pause`), confirms state, then resumes execution (`POST /api/tasks/{id}/resume`).
4. Demonstrates the **Restart** workflow: resets an existing task session (`POST /api/tasks/{id}/restart`), clearing events and re-queueing cleanly.
5. Demonstrates single cancellation & deletion (`DELETE /api/tasks/{id}`) and bulk deletion (`POST /api/tasks/bulk-delete`, up to 500 tasks per batch).

**Run Command:**
```powershell
# From SemanticAgentBroker\ subdirectory:
dotnet run --project Samples\Samples.TaskManagementExample

# Or from repository root:
dotnet run --project SemanticAgentBroker\Samples\Samples.TaskManagementExample
```

---

### C. `Samples.RealTimeStreamingExample` (SignalR WebSocket Streaming)
Demonstrates real-time telemetry and Human-in-the-Loop workflows:
1. Connects to `/loghub` using SignalR with automated token authentication.
2. Registers listeners for:
   - `TaskStateChanged`: Colored status transitions.
   - `AgentThought`: Streams live LLM reasoning thoughts as they are generated.
   - `ToolExecution`: Telemetry for tool invocations (`ToolName`, arguments, results).
   - `WorkspaceFileChanged`: Alerts when files are written in the workspace.
   - `PlanReviewRequested`: Plan ready for review in `ExecutionMode.Planned`.
3. Subscribes to the task: `hubConnection.InvokeAsync("SubscribeToTask", taskId)`.
4. Simulates Human-in-the-Loop approval: calls `hubConnection.InvokeAsync("ApprovePlan", taskId)`.
5. Sends mid-flight user interruptions: `hubConnection.InvokeAsync("SendInterruption", taskId, "...")`.

**Run Command:**
```powershell
# From SemanticAgentBroker\ subdirectory:
dotnet run --project Samples\Samples.RealTimeStreamingExample

# Or from repository root:
dotnet run --project SemanticAgentBroker\Samples\Samples.RealTimeStreamingExample
```

---

### D. `Samples.FileIngestionExample` (Workspace Files & Ingestion)
Demonstrates file processing pipelines and artifact retrieval:
1. Creates local sample datasets (`regional_sales.csv`).
2. Atomically stages files during task creation via multipart form data (`POST /api/tasks`).
3. Uploads supplementary files during execution (`POST /api/tasks/{id}/workspace/upload`).
4. Verifies staged files via `GET /api/tasks/{id}/workspace`.
5. Awaits task execution and downloads the generated output report (`regional_analysis.md`).

**Run Command:**
```powershell
# From SemanticAgentBroker\ subdirectory:
dotnet run --project Samples\Samples.FileIngestionExample

# Or from repository root:
dotnet run --project SemanticAgentBroker\Samples\Samples.FileIngestionExample
```

---

## 4. Prerequisites & Configuration

Before running any sample:
1. Start the API backend:
   ```powershell
   # From SemanticAgentBroker\ subdirectory:
   dotnet run --project SemanticAgentBroker.Api

   # Or from repository root:
   dotnet run --project SemanticAgentBroker\SemanticAgentBroker.Api
   ```
   By default, the API runs at `https://localhost:5100`.
2. Each sample accepts optional CLI arguments for custom endpoints and credentials:
   ```powershell
   # From SemanticAgentBroker\ subdirectory:
   dotnet run --project Samples\Samples.RestApiIntegrationExample -- "https://localhost:5100" "admin" "admin"

   # Or from repository root:
   dotnet run --project SemanticAgentBroker\Samples\Samples.RestApiIntegrationExample -- "https://localhost:5100" "admin" "admin"
   ```
