using Microsoft.Extensions.Logging;
using SemanticAgentBroker.Core.Features.TokenCounting;
using SemanticAgentBroker.Shared.Configuration;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SemanticAgentBroker.Core.Features.TaskState.Implementations;

/// <inheritdoc />
public partial class TaskStateManager : ITaskStateManager
{
    private readonly AgentSystemOptions _sysOptions;
    private readonly AiProviderOptions _aiOptions;
    private readonly ITokenCounter _tokenCounter;
    private readonly ILogger<TaskStateManager> _logger;
    private const string JsonStateFileName = "_task_state.json";
    private const string MdStateFileName = "_task_state.md";
    private const string WalkthroughFileName = "_walkthrough.md";

    private sealed class SessionLockEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int ActiveCount;
        public bool CleanupRequested;
    }

    private static readonly ConcurrentDictionary<Guid, SessionLockEntry> _sessionLocks = new();
    private static readonly object _sessionLocksSync = new();

    private sealed class SessionLockScope : IDisposable
    {
        private readonly Guid _sessionId;
        private readonly SessionLockEntry _entry;
        private int _disposed;

        public SessionLockScope(Guid sessionId, SessionLockEntry entry)
        {
            _sessionId = sessionId;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    _entry.Semaphore.Release();
                }
                catch (ObjectDisposedException) { }

                lock (_sessionLocksSync)
                {
                    _entry.ActiveCount--;
                    if (_entry.ActiveCount <= 0 && _entry.CleanupRequested)
                    {
                        if (_sessionLocks.TryRemove(_sessionId, out var removed))
                        {
                            removed.Semaphore.Dispose();
                        }
                    }
                }
            }
        }
    }

    private async Task<IDisposable> AcquireSessionLockAsync(Guid sessionId, CancellationToken ct)
    {
        SessionLockEntry entry;
        lock (_sessionLocksSync)
        {
            entry = _sessionLocks.GetOrAdd(sessionId, _ => new SessionLockEntry());
            entry.ActiveCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
            return new SessionLockScope(sessionId, entry);
        }
        catch
        {
            lock (_sessionLocksSync)
            {
                entry.ActiveCount--;
                if (entry.ActiveCount <= 0 && entry.CleanupRequested)
                {
                    if (_sessionLocks.TryRemove(sessionId, out var removed))
                    {
                        removed.Semaphore.Dispose();
                    }
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Initializes a new instance of <see cref="TaskStateManager"/>.
    /// </summary>
    /// <param name="sysOptions">The agent system options.</param>
    /// <param name="aiOptions">The AI provider options.</param>
    /// <param name="tokenCounter">The token counter service.</param>
    /// <param name="logger">Logger instance.</param>
    public TaskStateManager(AgentSystemOptions sysOptions, AiProviderOptions aiOptions, ITokenCounter tokenCounter, ILogger<TaskStateManager> logger)
    {
        _sysOptions = sysOptions;
        _aiOptions = aiOptions;
        _tokenCounter = tokenCounter;
        _logger = logger;
    }

    private string GetWorkspacePath(Guid sessionId) =>
        Agents.Implementations.AgentTaskContext.GetWorkspacePath(sessionId, _sysOptions);

    /// <inheritdoc />
    public async Task InitializeStateAsync(Guid sessionId, string objective, CancellationToken ct)
    {
        using (await AcquireSessionLockAsync(sessionId, ct))
        {
            var state = await ReadStateUnlockedAsync(sessionId, ct);
            if (string.IsNullOrWhiteSpace(state.Objective))
            {
                state = state with { Objective = objective };
                var workspacePath = GetWorkspacePath(sessionId);
                Directory.CreateDirectory(workspacePath);
                await WriteStateAsync(workspacePath, state, ct);
            }
        }
    }

    /// <inheritdoc />
    public async Task AppendObjectiveAsync(Guid sessionId, string newObjective, CancellationToken ct)
    {
        using (await AcquireSessionLockAsync(sessionId, ct))
        {
            var state = await ReadStateUnlockedAsync(sessionId, ct);
            if (!string.IsNullOrWhiteSpace(state.Objective))
            {
                state = state with { Objective = $"{state.Objective}\n\n[Follow-up]: {newObjective}" };
                var workspacePath = GetWorkspacePath(sessionId);
                await WriteStateAsync(workspacePath, state, ct);
            }
        }
    }

    /// <inheritdoc />
    public async Task<TaskStateSnapshot> ReadStateAsync(Guid sessionId, CancellationToken ct)
    {
        // Lock for external callers to prevent TOCTOU races with concurrent writes
        using (await AcquireSessionLockAsync(sessionId, ct))
        {
            return await ReadStateUnlockedAsync(sessionId, ct);
        }
    }

    /// <summary>Internal read without locking. Caller MUST hold the session lock.</summary>
    private async Task<TaskStateSnapshot> ReadStateUnlockedAsync(Guid sessionId, CancellationToken ct)
    {
        var filePath = Path.Combine(GetWorkspacePath(sessionId), JsonStateFileName);
        if (!File.Exists(filePath))
        {
            return new TaskStateSnapshot("", [], new(), [], 0, null);
        }

        try
        {
            var content = await File.ReadAllTextAsync(filePath, ct);
            var snapshot = JsonSerializer.Deserialize<TaskStateSnapshot>(content);
            if (snapshot != null)
            {
                return snapshot with
                {
                    KeyFacts = snapshot.KeyFacts ?? [],
                    CompletedSteps = snapshot.CompletedSteps ?? [],
                    FilesManifest = snapshot.FilesManifest ?? new(),
                    RecentObservations = snapshot.RecentObservations ?? [],
                    UserDirectives = snapshot.UserDirectives ?? []
                };
            }
            return new TaskStateSnapshot("", [], new(), [], 0, null, [], []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read task state for {SessionId}", sessionId);
            return new TaskStateSnapshot("", [], new(), [], 0, null);
        }
    }

    /// <inheritdoc />
    public async Task<int> GetMaxStepNumberAsync(Guid sessionId, CancellationToken ct = default)
    {
        var state = await ReadStateAsync(sessionId, ct);
        return state.CompletedSteps.Count > 0 ? state.CompletedSteps.Max(s => s.StepNumber) : 0;
    }

    /// <inheritdoc />
    public async Task RecordStepAsync(Guid sessionId, int step, string action, string actionInput, string resultSummary, CancellationToken ct)
    {
        using (await AcquireSessionLockAsync(sessionId, ct))
        {
            var workspacePath = GetWorkspacePath(sessionId);
            Directory.CreateDirectory(workspacePath);

            var state = await ReadStateUnlockedAsync(sessionId, ct);
            
            actionInput = CleanTextForPrompt(actionInput);
            resultSummary = CleanTextForPrompt(resultSummary);

            // Don't add exact duplicate steps (same step, same action, AND same action input)
            if (state.CompletedSteps.Any(s => s.StepNumber == step && s.Action == action && string.Equals(s.ActionInput?.Trim(), actionInput?.Trim(), StringComparison.Ordinal)))
                return;

            // Record user interruptions in persistent UserDirectives
            if (string.Equals(action, "UserInterruption", StringComparison.OrdinalIgnoreCase))
            {
                var directives = state.UserDirectives?.ToList() ?? new List<string>();
                if (!string.IsNullOrWhiteSpace(resultSummary) && !directives.Contains(resultSummary))
                {
                    directives.Add(resultSummary);
                }
                state = state with { UserDirectives = directives };
            }

            // Detect error results for the IsError flag (UserInterruption is never an error)
            bool isError = !string.Equals(action, "UserInterruption", StringComparison.OrdinalIgnoreCase) && (
                resultSummary.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                || resultSummary.StartsWith("Error ", StringComparison.OrdinalIgnoreCase)
                || resultSummary.StartsWith("Warning/Error output:", StringComparison.OrdinalIgnoreCase)
                || resultSummary.StartsWith("Tool execution failed:", StringComparison.OrdinalIgnoreCase)
                || resultSummary.StartsWith("Rejected:", StringComparison.OrdinalIgnoreCase)
                || resultSummary.Contains("Traceback (most recent call last):", StringComparison.OrdinalIgnoreCase)
                || resultSummary.Contains("SyntaxError:", StringComparison.OrdinalIgnoreCase)
                || resultSummary.Contains("IndentationError:", StringComparison.OrdinalIgnoreCase)
                || resultSummary.Contains("Invalid action format", StringComparison.OrdinalIgnoreCase)
                || resultSummary.Contains("Invalid JSON format", StringComparison.OrdinalIgnoreCase));

            // Store the full raw observation for the latest step (configurable limit)
            int rawObsLimit = _aiOptions.ContextCompaction.RecentStepFullObservationChars;
            string? rawObservation = null;
            if (rawObsLimit > 0 && resultSummary.Length > 0)
            {
                rawObservation = resultSummary.Length > rawObsLimit
                    ? SafeTruncate(resultSummary, rawObsLimit)
                    : resultSummary;
            }

            // Truncate the step summary for the compact step log
            if (resultSummary.Length > 1000)
                resultSummary = SafeTruncate(resultSummary, 1000);

            state.CompletedSteps.Add(new CompletedStep(step, action, actionInput, resultSummary, isError));

            // Update RecentObservations
            var recentObs = state.RecentObservations?.ToList() ?? new List<RecentObservation>();
            if (rawObservation != null)
            {
                recentObs.Add(new RecentObservation(step, action, actionInput, rawObservation));
                
                // Trim to configured window size
                int windowSize = _aiOptions.ContextCompaction.RecentObservationWindowSize;
                if (windowSize > 0 && recentObs.Count > windowSize)
                {
                    recentObs = recentObs.Skip(recentObs.Count - windowSize).ToList();
                }
            }

            state = state with { RecentObservations = recentObs };

            // Extract key facts from the result (file paths, numbers)
            ExtractKeyFacts(resultSummary, state.KeyFacts);

            await WriteStateAsync(workspacePath, state, ct);
        }
    }

    /// <inheritdoc />
    public async Task UpdateFilesManifestAsync(Guid sessionId, CancellationToken ct)
    {
        using (await AcquireSessionLockAsync(sessionId, ct))
        {
            await UpdateFilesManifestCoreAsync(sessionId, ct);
        }
    }

    /// <summary>
    /// Core manifest-update logic without locking. Caller must hold the session lock.
    /// </summary>
    private async Task UpdateFilesManifestCoreAsync(Guid sessionId, CancellationToken ct)
    {
        var workspacePath = GetWorkspacePath(sessionId);
        if (!Directory.Exists(workspacePath))
            return;

        var state = await ReadStateUnlockedAsync(sessionId, ct);
        state.FilesManifest.Clear();

        foreach (var file in new DirectoryInfo(workspacePath).GetFiles("*", SearchOption.TopDirectoryOnly))
        {
            if (file.Name.StartsWith("_")) continue; // skip internal files
            
            try
            {
                if (!file.Exists) continue;

                var length = file.Length;
                var sizeStr = length switch
                {
                    > 1_048_576 => $"{length / 1_048_576.0:F1}MB",
                    > 1024 => $"{length / 1024.0:F0}KB",
                    _ => $"{length}B"
                };
                state.FilesManifest[file.Name] = sizeStr;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
            {
                // File deleted or locked during directory scan; skip safely
            }
        }

        await WriteStateAsync(workspacePath, state, ct);
    }

    /// <inheritdoc />
    public async Task IncrementCompactionCountAsync(Guid sessionId, CancellationToken ct)
    {
        using (await AcquireSessionLockAsync(sessionId, ct))
        {
            var state = await ReadStateUnlockedAsync(sessionId, ct);
            var newState = state with { CompactionCount = state.CompactionCount + 1 };
            var workspacePath = GetWorkspacePath(sessionId);
            await WriteStateAsync(workspacePath, newState, ct);
        }
    }

    private async Task<string> BuildContextString(
        TaskStateSnapshot state,
        List<RecentObservation>? overriddenObservations,
        int currentStep, int tokensUsed, int maxTokens, int compactionCount,
        string? planPath, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"[TASK PROGRESS - Step {currentStep}");
        sb.Append($" | Tokens: ~{tokensUsed / 1000}K/{maxTokens / 1000}K");
        if (compactionCount > 0)
            sb.Append($" | Compacted: {compactionCount}x");
        sb.AppendLine("]");
        sb.AppendLine("WORKSPACE_PATH=/app/workdir/");

        if (!string.IsNullOrWhiteSpace(state.Objective))
            sb.AppendLine($"Objective: {state.Objective}");

        // ── Active User Directives (Never collapsed) ─────────────────────────
        if (state.UserDirectives != null && state.UserDirectives.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 🚨 Active User Directives");
            sb.AppendLine("(User instructions provided mid-task that take precedence over previous instructions):");
            foreach (var directive in state.UserDirectives)
            {
                sb.AppendLine($"- [USER DIRECTIVE]: {directive}");
            }
        }

        // ── Completed Steps - Tiered Detail ──────────────────────────────────
        if (state.CompletedSteps.Count > 0)
        {
            sb.AppendLine($"Completed: {state.CompletedSteps.Count} steps");

            var steps = state.CompletedSteps.OrderBy(s => s.StepNumber).ToList();
            int totalSteps = steps.Count;

            int fullDetailCount = Math.Min(3, totalSteps);
            int mediumDetailCount = Math.Min(7, totalSteps - fullDetailCount);
            int collapsedCount = totalSteps - fullDetailCount - mediumDetailCount;

            if (collapsedCount > 0)
            {
                var collapsed = steps.Take(collapsedCount);
                var collapseRange = $"{collapsed.First().StepNumber}-{collapsed.Last().StepNumber}";
                var errorCount = collapsed.Count(s => s.IsError);
                var suffix = errorCount > 0 ? $", {errorCount} failed" : "";
                sb.AppendLine($"✅ Steps {collapseRange}: (earlier work - {collapsedCount} steps completed{suffix})");
            }

            var mediumSteps = steps.Skip(collapsedCount).Take(mediumDetailCount);
            foreach (var s in mediumSteps)
            {
                var icon = s.IsError ? "❌" : "✅";
                var summary = s.ResultSummary.Length > 500
                    ? SafeTruncate(s.ResultSummary, 500)
                    : s.ResultSummary;
                sb.AppendLine($"{icon} {s.StepNumber}: {s.Action} → {summary}");
            }

            var fullSteps = steps.Skip(collapsedCount + mediumDetailCount);
            foreach (var s in fullSteps)
            {
                var icon = s.IsError ? "❌" : "✅";
                var summary = s.ResultSummary.Length > 2000
                    ? SafeTruncate(s.ResultSummary, 2000)
                    : s.ResultSummary;

                sb.AppendLine($"{icon} {s.StepNumber}: {s.Action}");
                if (!string.IsNullOrWhiteSpace(s.ActionInput))
                    sb.AppendLine($"  Input: {s.ActionInput.Replace("\n", "\n  ")}");
                sb.AppendLine($"  Result: {summary}");
            }
        }

        // ── Recent Observations ───────────────────────────────────────────────
        var observations = overriddenObservations ?? state.RecentObservations?.ToList();
        if (observations != null && observations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"📋 Recent Observations:");
            foreach (var obs in observations)
            {
                sb.AppendLine();
                var stepRef = state.CompletedSteps.FirstOrDefault(s => s.StepNumber == obs.StepNumber && s.Action == obs.Action && string.Equals(s.ActionInput?.Trim(), obs.ActionInput?.Trim(), StringComparison.Ordinal))
                    ?? state.CompletedSteps.FirstOrDefault(s => s.StepNumber == obs.StepNumber && s.Action == obs.Action)
                    ?? state.CompletedSteps.FirstOrDefault(s => s.StepNumber == obs.StepNumber);
                var suffix = stepRef != null && stepRef.IsError ? " (Failed)" : " (Succeeded)";
                sb.AppendLine($"Step {obs.StepNumber} ({obs.Action}){suffix}:");
                if (!string.IsNullOrWhiteSpace(obs.ActionInput))
                    sb.AppendLine($"[Input]:\n{obs.ActionInput}");
                sb.AppendLine($"[Result]:\n{obs.Content}");
            }
        }

        // ── Files Manifest ────────────────────────────────────────────────────
        if (state.FilesManifest.Count > 0)
        {
            sb.Append("Files in /app/workdir/: ");
            sb.AppendLine(string.Join(", ", state.FilesManifest.Select(f => $"/app/workdir/{f.Key} ({f.Value})")));
        }

        // ── Key Facts ─────────────────────────────────────────────────────────
        if (state.KeyFacts.Count > 0)
        {
            var facts = state.KeyFacts.TakeLast(5);
            sb.AppendLine($"Key Data: {string.Join("; ", facts)}");
        }

        // ── Implementation Plan ───────────────────────────────────────────────
        if (planPath != null && File.Exists(planPath))
        {
            var planContent = await File.ReadAllTextAsync(planPath, ct);
            if (!string.IsNullOrWhiteSpace(planContent))
            {
                sb.AppendLine();
                sb.AppendLine(planPath.EndsWith("_runtime_plan.md") ? "## Active Runtime Plan" : "## Approved Implementation Plan");
                sb.AppendLine(planPath.EndsWith("_runtime_plan.md")
                    ? "(This is YOUR current active plan, superseding the baseline plan. Execute it step by step.)"
                    : "(This is YOUR current plan. Execute it step by step.)");
                sb.AppendLine(planContent.Trim());
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <inheritdoc />
    public async Task<string> BuildContextInjectionAsync(
        Guid sessionId, int currentStep,
        int tokensUsed, int maxTokens, int compactionCount,
        CancellationToken ct)
    {
        var state = await ReadStateAsync(sessionId, ct);
        var compactionOptions = _aiOptions.ContextCompaction;
        int maxStateTokens = compactionOptions.MaxStateInjectionTokens > 0 && maxTokens > 0
            ? Math.Min(compactionOptions.MaxStateInjectionTokens, maxTokens)
            : (maxTokens > 0 ? maxTokens : compactionOptions.MaxStateInjectionTokens);

        // Resolve plan path
        var runtimePlanPath = Path.Combine(GetWorkspacePath(sessionId), "_runtime_plan.md");
        var planPath = File.Exists(runtimePlanPath) ? runtimePlanPath : Path.Combine(GetWorkspacePath(sessionId), "_implementation_plan.md");
        if (!File.Exists(planPath)) planPath = null;

        // Build initial context string
        var observations = state.RecentObservations?.ToList();
        var result = await BuildContextString(state, observations, currentStep, tokensUsed, maxTokens, compactionCount, planPath, ct);

        // ── Token Budget Enforcement ─────────────────────────────────────
        if (maxStateTokens > 0)
        {
            string modelId = _aiOptions.ModelRouting.TryGetValue(
                SemanticAgentBroker.Shared.Configuration.TaskModelRole.PrimaryTextModel, out var route)
                ? route.ModelId : "default";
            int currentTokens = _tokenCounter.CountTokens(result, modelId);

            if (currentTokens > maxStateTokens)
            {
                _logger.LogInformation(
                    "State injection for {SessionId} step {Step} is {Tokens} tokens (limit {Limit}). Trimming.",
                    sessionId, currentStep, currentTokens, maxStateTokens);

                // Strategy 1: Progressively drop oldest observations, keeping at least the most recent one
                if (observations != null && observations.Count > 0)
                {
                    while (currentTokens > maxStateTokens && observations.Count > 1)
                    {
                        observations.RemoveAt(0);
                        result = await BuildContextString(state, observations, currentStep, tokensUsed, maxTokens, compactionCount, planPath, ct);
                        currentTokens = _tokenCounter.CountTokens(result, modelId);
                    }

                    // Truncate the last remaining observation if still over budget
                    if (currentTokens > maxStateTokens && observations.Count == 1 && observations[0].Content.Length > 1000)
                    {
                        observations[0] = observations[0] with { Content = SafeTruncate(observations[0].Content, 1000, "\n…[Observation truncated to fit token budget]") };
                        result = await BuildContextString(state, observations, currentStep, tokensUsed, maxTokens, compactionCount, planPath, ct);
                        currentTokens = _tokenCounter.CountTokens(result, modelId);
                    }
                }

                // Strategy 2: Trim plan to first 600 chars
                if (currentTokens > maxStateTokens && planPath != null)
                {
                    var planIdx = result.IndexOf("## Active Runtime Plan");
                    if (planIdx < 0)
                        planIdx = result.IndexOf("## Approved Implementation Plan");
                    if (planIdx >= 0)
                    {
                        var planSection = result[planIdx..];
                        var trimmedPlan = planSection.Length > 600
                            ? SafeTruncate(planSection, 600, "\n…[plan trimmed for token budget]")
                            : planSection;
                        result = result[..planIdx] + trimmedPlan;
                        currentTokens = _tokenCounter.CountTokens(result, modelId);
                    }
                }

                // Strategy 3: Collapse medium detail steps
                if (currentTokens > maxStateTokens && state.CompletedSteps.Count > 3)
                {
                    var lines = result.Split('\n');
                    var newLines = new List<string>();
                    bool skipSubLines = false;
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("✅ ") || line.StartsWith("❌ "))
                        {
                            var match = Regex.Match(line, @"^[✅❌] (\d+):");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out int stepNum))
                            {
                                int maxStep = state.CompletedSteps.Max(s => s.StepNumber);
                                skipSubLines = (stepNum <= maxStep - 3);
                            }
                            else
                            {
                                skipSubLines = false;
                            }
                        }
                        else if (skipSubLines && (line.StartsWith("  Input:") || line.StartsWith("  Result:")))
                        {
                            continue;  // Drop orphaned sub-lines from collapsed steps
                        }
                        else
                        {
                            skipSubLines = false;
                        }
                        newLines.Add(line);
                    }
                    result = string.Join("\n", newLines);
                    currentTokens = _tokenCounter.CountTokens(result, modelId);
                }

                // Strategy 4: Final safety fallback if still exceeding maxStateTokens
                if (currentTokens > maxStateTokens)
                {
                    int targetChars = (int)(result.Length * ((double)maxStateTokens / currentTokens) * 0.95);
                    if (targetChars > 500 && targetChars < result.Length)
                    {
                        result = SafeTruncate(result, targetChars, "\n…[Context truncated to fit token budget]");
                    }
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task FinalizeStateAsync(Guid sessionId, string finalResult, CancellationToken ct)
    {
        using (await AcquireSessionLockAsync(sessionId, ct))
        {
            try
            {
                // Update files manifest one last time
                await UpdateFilesManifestCoreAsync(sessionId, ct);

                var state = await ReadStateUnlockedAsync(sessionId, ct);
                var newState = state with { CurrentStatus = "Completed" };
                var workspacePath = GetWorkspacePath(sessionId);
                await WriteStateAsync(workspacePath, newState, ct);

                // Generate enhanced walkthrough
                var walkthroughPath = Path.Combine(workspacePath, WalkthroughFileName);
                var walkthroughSb = new StringBuilder();
                walkthroughSb.AppendLine("# Task Walkthrough");
                walkthroughSb.AppendLine();
                walkthroughSb.AppendLine($"## Objective");
                walkthroughSb.AppendLine(state.Objective);
                walkthroughSb.AppendLine();
                walkthroughSb.AppendLine("## Completed Steps");
                foreach (var step in state.CompletedSteps.OrderBy(s => s.StepNumber))
                {
                    walkthroughSb.AppendLine($"- **Step {step.StepNumber}**: {step.Action} → {step.ResultSummary}");
                }
                walkthroughSb.AppendLine();
                walkthroughSb.AppendLine("## Workspace Files");
                foreach (var (name, size) in state.FilesManifest)
                {
                    walkthroughSb.AppendLine($"- `{name}` ({size})");
                }
                walkthroughSb.AppendLine();
                walkthroughSb.AppendLine("## Final Result");
                var resultPreview = finalResult.Length > 2000 ? SafeTruncate(finalResult, 2000) : finalResult;
                walkthroughSb.AppendLine(resultPreview);

                await File.WriteAllTextAsync(walkthroughPath, walkthroughSb.ToString(), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to finalize task state for {SessionId}", sessionId);
            }
        }
    }

    /// <inheritdoc />
    public void CleanupSession(Guid sessionId)
    {
        lock (_sessionLocksSync)
        {
            if (_sessionLocks.TryGetValue(sessionId, out var entry))
            {
                if (entry.ActiveCount <= 0)
                {
                    if (_sessionLocks.TryRemove(sessionId, out var removed))
                    {
                        removed.Semaphore.Dispose();
                    }
                }
                else
                {
                    entry.CleanupRequested = true;
                }
            }
        }
    }

    // ── Serialization ──────────────────────────────────────────────────────

    private async Task WriteStateAsync(string workspacePath, TaskStateSnapshot state, CancellationToken ct)
    {
        Directory.CreateDirectory(workspacePath);

        // 1. Write JSON file as primary state store
        var jsonFilePath = Path.Combine(workspacePath, JsonStateFileName);
        var jsonContent = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await AtomicWriteAllTextAsync(jsonFilePath, jsonContent, ct);

        // 2. Write Markdown file as read-only view for LLM context / user visibility
        var sb = new StringBuilder();
        sb.AppendLine("<!-- This file is read-only. Do NOT write to it. -->");
        sb.AppendLine("# Task State");
        sb.AppendLine();
        sb.AppendLine("## Objective");
        sb.AppendLine(state.Objective);
        sb.AppendLine();
        if (state.UserDirectives != null && state.UserDirectives.Count > 0)
        {
            sb.AppendLine("## User Directives");
            foreach (var directive in state.UserDirectives)
            {
                sb.AppendLine($"- {directive}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("## Completed Steps");
        foreach (var step in state.CompletedSteps.OrderBy(s => s.StepNumber))
        {
            sb.AppendLine($"- [x] {step.StepNumber}: {step.Action} → {step.ResultSummary}");
        }
        sb.AppendLine();
        sb.AppendLine("## Files");
        foreach (var (name, size) in state.FilesManifest)
        {
            sb.AppendLine($"- {name} ({size})");
        }
        sb.AppendLine();
        sb.AppendLine("## Key Facts");
        foreach (var fact in state.KeyFacts)
        {
            sb.AppendLine($"- {fact}");
        }
        sb.AppendLine();
        sb.AppendLine($"## Meta");
        sb.AppendLine($"- CompactionCount: {state.CompactionCount}");
        if (state.RecentObservations != null && state.RecentObservations.Count > 0)
        {
            sb.AppendLine($"- RecentObservationsWindowSize: {state.RecentObservations.Count}");
            sb.AppendLine($"- LastRawObservationLength: {state.RecentObservations.Last().Content.Length}");
        }
        if (state.CurrentStatus != null)
            sb.AppendLine($"- Status: {state.CurrentStatus}");

        var mdFilePath = Path.Combine(workspacePath, MdStateFileName);
        await AtomicWriteAllTextAsync(mdFilePath, sb.ToString(), ct);
    }

    private static async Task AtomicWriteAllTextAsync(string targetFilePath, string content, CancellationToken ct)
    {
        var tempFilePath = targetFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempFilePath, content, ct);
            File.Move(tempFilePath, targetFilePath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch { }
            throw;
        }
    }

    private static void ExtractKeyFacts(string text, List<string>? facts)
    {
        if (facts is null) return;

        // Extract file paths from observations
        var fileMatches = FilePathRegex().Matches(text);
        foreach (Match m in fileMatches)
        {
            var rawPath = m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '"', '\'');
            if (rawPath.Length > "/app/workdir/".Length)
            {
                var fact = $"File: {rawPath}";
                if (!facts.Contains(fact))
                    facts.Add(fact);
            }
        }

        // Keep list bounded
        while (facts.Count > 20)
            facts.RemoveAt(0);
    }

    [GeneratedRegex(@"/app/workdir/[\w\-\. ()\[\]]+")]
    private static partial Regex FilePathRegex();

    /// <summary>
    /// Adjusts a truncation index to avoid splitting a UTF-16 surrogate pair.
    /// </summary>
    public static int SafeTruncateIndex(string s, int index)
    {
        if (index <= 0) return 0;
        if (index >= s.Length) return s.Length;
        if (char.IsHighSurrogate(s[index - 1])) return index - 1;
        if (char.IsLowSurrogate(s[index])) return Math.Max(0, index - 1);
        return index;
    }

    /// <summary>
    /// Truncates a string to at most maxLength characters without breaking surrogate pairs.
    /// </summary>
    public static string SafeTruncate(string? value, int maxLength, string suffix = "…")
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        if (value.Length <= maxLength) return value;
        int safeIndex = SafeTruncateIndex(value, maxLength);
        return value[..safeIndex] + suffix;
    }

    /// <summary>
    /// Cleans non-breaking spaces, zero-width characters, and byte-fallback tokens before
    /// recording steps or injecting state into LLM prompts.
    /// </summary>
    public static string CleanTextForPrompt(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        // Normalize /app/<0xXX>dir paths
        text = Regex.Replace(text, @"(?i)/app/<0x[0-9a-f]{2}>dir", "/app/workdir");
        text = Regex.Replace(text, @"(?i)/app/\u00C2dir", "/app/workdir");

        // Normalize literal JSON unicode escape sequences for non-breaking and unusual spaces
        text = Regex.Replace(text, @"(?i)\\u00a0|\\u202f|\\u200[0-9ab]|\\u3000|\\u1680", " ");
        text = Regex.Replace(text, @"(?i)\\u200[cd]|\\ufeff|\\u2060|\\u00ad", "");

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            switch (ch)
            {
                case '\u00A0': // NBSP
                case '\u202F': // Narrow NBSP
                case '\u2007':
                case '\u2008':
                case '\u2009':
                case '\u200A':
                case '\u3000':
                case '\u1680':
                case '\u2000':
                case '\u2001':
                case '\u2002':
                case '\u2003':
                case '\u2004':
                case '\u2005':
                case '\u2006':
                case '\u200B': // Zero-width space
                    sb.Append(' ');
                    break;

                case '\u200C':
                case '\u200D':
                case '\uFEFF':
                case '\u2060':
                case '\u00AD':
                    break;

                case '\u00C2' when i + 1 < text.Length && (text[i + 1] == '\u00A0' || text[i + 1] == ' '):
                    break;

                default:
                    sb.Append(ch);
                    break;
            }
        }

        var result = sb.ToString();
        if (result.Contains("<0x"))
        {
            result = Regex.Replace(result, @"<0x[0-9a-fA-F]{2}>", " ");
        }

        return result;
    }
}
