using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Core.Constants;
using SemanticAgentBroker.Core.Features.Agents.Implementations;
using SemanticAgentBroker.Core.Services;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Api.Features.ReAct.Services;

/// <summary>
/// Handles the pre-execution planning phase for PlanThen* execution modes.
/// Generates a structured implementation plan, presents it for user review,
/// and supports approve/modify/reject/revise cycles before passing
/// the approved plan to the ReAct execution engine.
/// </summary>
public class PlanningEngine
{
    private readonly AgentTaskStore _taskStore;
    private readonly TaskOperationsService _taskOps;
    private readonly AiProviderOptions _aiOptions;
    private readonly AgentSystemOptions _sysOptions;
    private readonly LlmCallService _llmCallService;
    private readonly ILogger<PlanningEngine> _logger;
    private readonly SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager? _lockManager;

    private const int MaxRevisionAttempts = 5;

    public PlanningEngine(
        AgentTaskStore taskStore,
        TaskOperationsService taskOps,
        AiProviderOptions aiOptions,
        AgentSystemOptions sysOptions,
        LlmCallService llmCallService,
        ILogger<PlanningEngine> logger,
        SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager? lockManager = null)
    {
        _taskStore = taskStore;
        _taskOps = taskOps;
        _aiOptions = aiOptions;
        _sysOptions = sysOptions;
        _llmCallService = llmCallService;
        _logger = logger;
        _lockManager = lockManager;
    }

    /// <summary>
    /// Generates an implementation plan, waits for user review, and returns the approved plan.
    /// Returns null if the plan is rejected or the task is cancelled.
    /// </summary>
    public virtual async Task<string?> GeneratePlanAsync(
        Guid sessionId,
        Kernel kernel,
        string systemPrompt,
        string userPrompt,
        bool debugMode,
        bool autoApprove,
        SemaphoreSlim executionSemaphore,
        SemaphoreOwnershipState semaphoreState,
        CancellationToken ct)
    {
        await _taskStore.UpdateStateAsync(sessionId, TaskState.Planning, "Generating implementation plan...");
        await _taskStore.BroadcastActivityAsync(sessionId, "Analyzing request and generating plan...");
        _taskOps.ClearPlanDecisions(sessionId);

        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        // ── Resolve context window for planning ────────────────────────────
        int maxContextTokens = ResolveMaxContextTokens();

        // ── Phase 1: Generate the plan ─────────────────────────────────────
        var planningPrompt = BuildPlanningPrompt(systemPrompt, userPrompt, kernel);

        var planHistory = new ChatHistory(planningPrompt);
        var previousPlan = await _taskStore.GetLatestPlanAsync(sessionId);
        
        if (previousPlan != null)
        {
            planHistory.AddUserMessage("Previous Plan:\n" + previousPlan.PlanMarkdown);
            planHistory.AddAssistantMessage("Understood. I will use the previous plan as a base.");
            planHistory.AddUserMessage($"Please generate an updated plan based on the following follow-up request:\n{userPrompt}");
        }
        else
        {
            planHistory.AddUserMessage(userPrompt);
        }

        var planSettings = PromptSettingsHelper.BuildSettings(
            _aiOptions.Inference.PlanningPromptSettings,
            new Dictionary<string, object>
            {
                { "num_predict", _aiOptions.MaxTokensPerRequest },
                { "max_tokens", _aiOptions.MaxTokensPerRequest },
                { "num_ctx", maxContextTokens }
            },
            ProviderResolutionHelper.ResolvePrimaryProviderType(_aiOptions));

        if (debugMode)
            await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                "[DEBUG] Planning phase started. Sending planning prompt to LLM...");

        var planContent = await GeneratePlanContentAsync(chatService, planHistory, planSettings, sessionId, kernel, ct);

        if (string.IsNullOrWhiteSpace(planContent))
        {
            await _taskStore.LogEventAsync(sessionId, EventTypes.PlanError, LogLevelNames.System,
                "Failed to generate a valid implementation plan.");
            return null;
        }

        // ── Phase 2: Review loop ───────────────────────────────────────────
        int version = previousPlan != null ? previousPlan.Version + 1 : 1;
        string currentPlan = planContent;
        string? openQuestions = ExtractOpenQuestions(planContent);

        for (int attempt = 0; attempt < MaxRevisionAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Persist the plan
            await _taskStore.SavePlanAsync(sessionId, version, currentPlan, openQuestions);
            
            try
            {
                var workspacePath = AgentTaskContext.GetWorkspacePath(sessionId, _sysOptions);
                Directory.CreateDirectory(workspacePath);
                var planFile = "_implementation_plan.md";
                if (_lockManager != null)
                {
                    using (await _lockManager.AcquireLockAsync(sessionId, planFile, ct))
                    {
                        await File.WriteAllTextAsync(Path.Combine(workspacePath, planFile), currentPlan, ct);
                    }
                }
                else
                {
                    await File.WriteAllTextAsync(Path.Combine(workspacePath, planFile), currentPlan, ct);
                }
                await _taskStore.BroadcastWorkspaceFileChangedAsync(sessionId, planFile, "Created");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist implementation plan to workspace file.");
            }

            await _taskStore.LogEventAsync(sessionId, EventTypes.PlanGenerated, LogLevelNames.System,
                $"Implementation plan v{version} generated ({currentPlan.Length} chars)");

            // Auto-approve mode: skip user review entirely
            if (autoApprove)
            {
                await _taskStore.ApprovePlanAsync(sessionId, version);
                await _taskStore.LogEventAsync(sessionId, EventTypes.PlanAutoApproved, LogLevelNames.System,
                    $"Plan v{version} auto-approved (AutoPlanned mode).");
                await _taskStore.BroadcastPlanAsync(sessionId, currentPlan, requiresApproval: false);
                await _taskStore.BroadcastActivityAsync(sessionId, "Plan auto-approved. Starting execution...");
                return currentPlan;
            }

            // Broadcast for UI
            await _taskStore.BroadcastPlanReviewAsync(sessionId, currentPlan, openQuestions, version);
            await _taskStore.UpdateStateAsync(sessionId, TaskState.PlanReview,
                "Implementation plan ready for review. Approve, modify, or reject.");
            await _taskStore.BroadcastActivityAsync(sessionId, "Waiting for plan review...");

            // ── Release semaphore while waiting for user decision ───────
            bool semaphoreReleased = false;
            if (executionSemaphore != null && (semaphoreState?.TryRelease() ?? true))
            {
                try
                {
                    executionSemaphore.Release();
                    semaphoreReleased = true;
                }
                catch (ObjectDisposedException)
                {
                    // Host shutdown may have already disposed the semaphore
                }
            }
            PlanDecision decision;
            bool decisionProcessed = false;
            try
            {
                decision = await _taskOps.WaitForPlanDecisionAsync(sessionId, ct);

                // Immediately acknowledge user action before re-acquiring slot
                await _taskStore.UpdateStateAsync(sessionId, TaskState.Queued,
                    "Plan decision received. Waiting for execution slot...");
                await _taskStore.BroadcastActivityAsync(sessionId, "Waiting for execution slot...");
                decisionProcessed = true;
            }
            finally
            {
                // Re-acquire before continuing (LLM work or next review cycle)
                if (executionSemaphore != null && semaphoreReleased)
                {
                    try
                    {
                        await executionSemaphore.WaitAsync(ct);
                        semaphoreState?.TryAcquire();
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutdown or cancellation requested - semaphoreState.IsHeld stays false
                        // so the outer finally in AgentTaskQueueService won't double-release.
                        if (semaphoreState != null) semaphoreState.IsHeld = false;
                        if (decisionProcessed)
                        {
                            ct.ThrowIfCancellationRequested();
                        }
                    }
                    catch (ObjectDisposedException ex)
                    {
                        // Semaphore was disposed (app shutdown). Abort execution loop cleanly.
                        if (semaphoreState != null) semaphoreState.IsHeld = false;
                        _logger.LogWarning(ex, "Execution semaphore was disposed during plan review for {SessionId}", sessionId);
                        throw new OperationCanceledException("Execution semaphore was disposed during application shutdown.", ex, ct);
                    }
                }
            }
            await _taskStore.UpdateStateAsync(sessionId, TaskState.Planning, "Processing plan decision...");

            switch (decision.Action)
            {
                case PlanAction.Approve:
                    await _taskStore.ApprovePlanAsync(sessionId, version);
                    await _taskStore.LogEventAsync(sessionId, EventTypes.PlanApproved, LogLevelNames.User,
                        $"Plan v{version} approved by user.");
                    await _taskStore.BroadcastActivityAsync(sessionId, "Plan approved. Starting execution...");
                    return currentPlan;

                case PlanAction.Modify:
                    currentPlan = decision.ModifiedPlan ?? currentPlan;
                    version++;
                    await _taskStore.LogEventAsync(sessionId, EventTypes.PlanModified, LogLevelNames.User,
                        $"Plan modified by user. New version: v{version}");
                    await _taskStore.BroadcastPlanRevisionAsync(sessionId, currentPlan, version);
                    continue;

                case PlanAction.Reject:
                    var feedback = decision.Feedback ?? "User rejected the plan.";
                    await _taskStore.LogEventAsync(sessionId, EventTypes.PlanRejected, LogLevelNames.User,
                        $"Plan v{version} rejected. Feedback: {feedback}");

                    await _taskStore.UpdateStateAsync(sessionId, TaskState.Planning,
                        "Revising plan based on feedback...");
                    await _taskStore.BroadcastActivityAsync(sessionId, "Revising plan based on your feedback...");

                    planHistory.AddAssistantMessage(currentPlan);
                    planHistory.AddUserMessage(
                        $"The user reviewed your plan and rejected it with the following feedback:\n\n" +
                        $"{feedback}\n\n" +
                        $"Please revise the plan to address this feedback. " +
                        $"Output the complete revised plan.");

                    currentPlan = await GeneratePlanContentAsync(chatService, planHistory, planSettings, sessionId, kernel, ct);
                    if (string.IsNullOrWhiteSpace(currentPlan))
                    {
                        await _taskStore.LogEventAsync(sessionId, EventTypes.PlanError, LogLevelNames.System,
                            "Failed to generate revised plan.");
                        return null;
                    }

                    openQuestions = ExtractOpenQuestions(currentPlan);
                    version++;
                    await _taskStore.BroadcastPlanRevisionAsync(sessionId, currentPlan, version);
                    continue;

                case PlanAction.AskQuestion:
                    var question = decision.Feedback ?? "";
                    await _taskStore.LogEventAsync(sessionId, EventTypes.PlanQuestion, LogLevelNames.User,
                        $"User question: {question}");

                    planHistory.AddAssistantMessage(currentPlan);
                    planHistory.AddUserMessage(
                        $"The user has a question about the plan:\n\n{question}\n\n" +
                        $"Answer the question and then output the complete plan again " +
                        $"(updated if the question changes anything).");

                    currentPlan = await GeneratePlanContentAsync(chatService, planHistory, planSettings, sessionId, kernel, ct);
                    if (string.IsNullOrWhiteSpace(currentPlan))
                    {
                        currentPlan = "(Plan regeneration failed - previous plan still applies)";
                    }

                    openQuestions = ExtractOpenQuestions(currentPlan);
                    version++;
                    await _taskStore.BroadcastPlanRevisionAsync(sessionId, currentPlan, version);
                    continue;
            }
        }

        await _taskStore.LogEventAsync(sessionId, EventTypes.PlanError, LogLevelNames.System,
            $"Maximum revision attempts ({MaxRevisionAttempts}) reached.");
        return null;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private string BuildPlanningPrompt(string systemPrompt, string userPrompt, Kernel kernel)
    {
        var functions = kernel.Plugins.GetFunctionsMetadata();
        var toolsDesc = string.Join("\n", functions.Select(f =>
            $"- {f.PluginName}-{f.Name}: {f.Description}"));

        return $$"""
            You are in PLANNING MODE. You must NOT execute any tools yet.
            
            Your role: {{systemPrompt.Split('\n').FirstOrDefault() ?? "Agent"}}
            
            Available tools:
            {{toolsDesc}}
            
            Analyze the user's request and produce a structured implementation plan.
            Use the following format:
            
            ## Goal
            Brief description of what needs to be accomplished.
            
            ## Proposed Steps
            Numbered list of concrete steps you will take, including which tools you'll use at each step.
            Be specific about the data you'll retrieve, the analysis you'll perform, and the expected outputs.
            
            ## Expected Output
            Describe the format and content of the final deliverable.
            CRITICAL: Listing a file here does NOT magically create it. If your plan requires creating a separate report file, your "Proposed Steps" MUST include explicitly using a file-saving tool to create it. If no separate file was requested, include the analysis directly in your final response instead of promising imaginary files.

            
            ## Open Questions
            Any clarifications needed from the user before proceeding. If none, write "None".
            
            ## Risks & Considerations
            Any potential issues or edge cases to be aware of.
            
            Output your complete plan now.
            """;
    }

    private int ResolveMaxContextTokens() => ModelRoutingHelper.ResolveMaxContextTokens(_aiOptions);

    private async Task<string> GeneratePlanContentAsync(
        IChatCompletionService chatService,
        ChatHistory history,
        PromptExecutionSettings settings,
        Guid sessionId,
        Kernel kernel,
        CancellationToken ct)
    {
        try
        {
            var (content, _, _) = await _llmCallService.CallLlmWithRetryAsync(
                chatService, history, settings, kernel, sessionId, 0, null, false, false, ct);
            return content.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate plan content for session {SessionId}", sessionId);
            return "";
        }
    }

    private static string? ExtractOpenQuestions(string planContent)
    {
        var idx = planContent.IndexOf("## Open Questions", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var afterQuestions = planContent[(idx + "## Open Questions".Length)..].Trim();

        var nextHeading = afterQuestions.IndexOf("\n##", StringComparison.Ordinal);
        var questionsText = nextHeading >= 0
            ? afterQuestions[..nextHeading].Trim()
            : afterQuestions.Trim();

        if (string.IsNullOrWhiteSpace(questionsText) ||
            questionsText.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            questionsText.Equals("None.", StringComparison.OrdinalIgnoreCase) ||
            questionsText.StartsWith("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return questionsText;
    }



}
