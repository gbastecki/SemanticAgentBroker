using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Core.Constants;
using SemanticAgentBroker.Core.Features.TokenCounting;
using SemanticAgentBroker.Core.Services;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.Api.Features.ReAct.Services;

public partial class SubagentExecutor : ISubagentExecutor
{
    private readonly LlmCallService _llmCallService;
    private readonly AiProviderOptions _aiOptions;
    private readonly ITokenCounter _tokenCounter;
    private readonly AgentTaskStore _taskStore;
    private readonly ILogger<SubagentExecutor> _logger;

    /// <summary>
    /// The instruction text appended to the state injection in Phase 1 (reasoning).
    /// Exposed so the orchestrator can accurately compute token budget overhead.
    /// </summary>
    public static string ReasoningFramingText { get; } =
        "Based on the state above, analyze the situation and reason about your next action.\n\n" +
        $"You MUST begin with `{ReactFormatTokens.Thought}` explaining:\n" +
        "- What you observe in the current state\n" +
        "- What has been done so far and what remains\n" +
        "- What you will do next and why\n\n" +
        $"Then, if the task is complete, write `{ReactFormatTokens.FinalAnswer}` followed by your final result.\n" +
        $"If you need to create/update an implementation plan, write `{ReactFormatTokens.Plan}` followed by the plan.\n\n" +
        "IMPORTANT: When calling tools, you MUST end your response with EXACTLY these lines (on separate lines):\n" +
        $"`{ReactFormatTokens.Action}` followed by the EXACT tool name (e.g. `Action: PythonSandbox-ExecutePythonScript`)\n" +
        $"`{ReactFormatTokens.ActionInput}` followed by the JSON arguments with DOUBLE QUOTES for keys (e.g. `ActionInput: {{\"script\": \"print('hello')\"}}`).\n\n" +
        "If you want to execute MULTIPLE tools in one step, repeat the Action:/ActionInput: pair for each tool. " +
        "Multiple tools in a single step are executed concurrently in parallel, so ensure they are independent.\n\n" +
        "Do NOT include Action: lines if you are providing a FinalAnswer:.\n" +
        "Do NOT actually invoke tools \u2014 just write Action: and ActionInput: as text so the system knows what to execute.";

    public SubagentExecutor(
        LlmCallService llmCallService,
        AiProviderOptions aiOptions,
        ITokenCounter tokenCounter,
        AgentTaskStore taskStore,
        ILogger<SubagentExecutor> logger)
    {
        _llmCallService = llmCallService;
        _aiOptions = aiOptions;
        _tokenCounter = tokenCounter;
        _taskStore = taskStore;
        _logger = logger;
    }

    public async Task<SubagentResult> ExecuteStepAsync(
        Guid sessionId,
        Kernel kernel,
        string systemPrompt,
        string stateInjection,
        int step,
        int maxContextTokens,
        bool debugMode,
        CancellationToken ct)
    {
        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        string modelId = _aiOptions.ModelRouting.TryGetValue(TaskModelRole.PrimaryTextModel, out var route) ? route.ModelId : "default";

        // ======================================================================
        // PHASE 1: Reasoning call (no tools) - always produces visible thinking
        // ======================================================================
        var reasoningHistory = new ChatHistory(systemPrompt);
        reasoningHistory.AddUserMessage(
            $"[TASK STATE]\n{stateInjection}\n\n{ReasoningFramingText}");

        var reasoningSettings = PromptSettingsHelper.BuildSettings(
            _aiOptions.Inference.MainExecutionPromptSettings,
            new Dictionary<string, object>
            {
                { "num_predict", _aiOptions.MaxTokensPerRequest },
                { "max_tokens", _aiOptions.MaxTokensPerRequest },
                { "num_ctx", maxContextTokens }
            },
            ProviderResolutionHelper.ResolvePrimaryProviderType(_aiOptions),
            enableToolCalling: false);  // No tools in reasoning phase

        int reasoningTokens = _tokenCounter.CountTokens(reasoningHistory, modelId);
        await _taskStore.BroadcastTokenCountAsync(sessionId, reasoningTokens, maxContextTokens, $"Subagent {step} (Reasoning)");

        string reasoningContent;
        try
        {
            var (content, _, wasTruncated) = await _llmCallService.CallLlmWithRetryAsync(
                chatService, reasoningHistory, reasoningSettings, kernel,
                sessionId, step, null, false, debugMode, ct);

            if (wasTruncated || string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning(
                    "Phase 1 (reasoning) was truncated or empty for session {SessionId} step {Step}. Retrying with brevity nudge.",
                    sessionId, step);

                // Retry Phase 1 with an explicit brevity nudge to break out of reasoning loops
                var nudgedHistory = new ChatHistory(systemPrompt);
                nudgedHistory.AddUserMessage(
                    $"[TASK STATE]\n{stateInjection}\n\n{ReasoningFramingText}\n\n" +
                    "[SYSTEM ALERT]: Your previous attempt was cut short because reasoning exceeded the output token budget. " +
                    "Keep your Thought to 1-2 brief sentences and IMMEDIATELY output your Action and ActionInput (or FinalAnswer).");

                var nudgedSettings = PromptSettingsHelper.BuildSettings(
                    _aiOptions.Inference.NudgedExecutionPromptSettings ?? _aiOptions.Inference.MainExecutionPromptSettings,
                    new Dictionary<string, object>
                    {
                        { "temperature", 0.2 },
                        { "presence_penalty", 0.2 },
                        { "repeat_penalty", 1.15 },
                        { "num_predict", _aiOptions.MaxTokensPerRequest },
                        { "max_tokens", _aiOptions.MaxTokensPerRequest },
                        { "num_ctx", maxContextTokens }
                    },
                    ProviderResolutionHelper.ResolvePrimaryProviderType(_aiOptions),
                    enableToolCalling: false);

                var (nudgedContent, _, nudgedTruncated) = await _llmCallService.CallLlmWithRetryAsync(
                    chatService, nudgedHistory, nudgedSettings, kernel,
                    sessionId, step, null, false, debugMode, ct);

                if (nudgedTruncated || string.IsNullOrWhiteSpace(nudgedContent))
                {
                    _logger.LogWarning(
                        "Nudged Phase 1 retry also truncated/empty for session {SessionId} step {Step}. Falling back to single-call.",
                        sessionId, step);
                    return await ExecuteSingleCallFallbackAsync(sessionId, kernel, chatService, systemPrompt,
                        stateInjection, step, maxContextTokens, modelId, debugMode, ct);
                }

                reasoningContent = nudgedContent;
            }
            else
            {
                reasoningContent = content;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Phase 1 (reasoning) failed for session {SessionId} step {Step}. Falling back to single-call.", sessionId, step);
            return await ExecuteSingleCallFallbackAsync(sessionId, kernel, chatService, systemPrompt,
                stateInjection, step, maxContextTokens, modelId, debugMode, ct);
        }

        if (debugMode)
        {
            await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                $"[DEBUG] Phase 1 (reasoning) returned {reasoningContent.Length} chars");
        }

        // ── Parse reasoning output using SubagentResponseParser ───────────────
        var availableTools = kernel.Plugins
            .SelectMany(p => p.Select(f => $"{p.Name}-{f.Name}"))
            .ToList();

        var parsed = SubagentResponseParser.ParseResponse(reasoningContent, _aiOptions, modelId, availableTools);
        var thought = parsed.Thought;
        var plan = parsed.Plan;
        var finalAnswer = parsed.FinalAnswer;
        var toolCalls = parsed.ToolCalls;

        // ── Validate and repair JSON for each tool call ──
        for (int i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];
            if (string.IsNullOrWhiteSpace(tc.ActionInput) || tc.ActionInput == "{}")
                continue;

            var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(tc.ActionInput);

            if (JsonSanitizer.IsValidJson(sanitized))
            {
                if (wasSanitized)
                {
                    toolCalls[i] = tc with { ActionInput = sanitized };
                    if (debugMode)
                    {
                        await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.Warning,
                            $"[DEBUG] Repaired ActionInput JSON for {tc.ActionName} using JsonSanitizer: {warning}");
                    }
                }
            }
            else
            {
                // Try deterministic wrapping for raw code inputs (e.g., PythonSandbox-ExecutePythonScript)
                string? deterministicFix = SubagentResponseParser.TryWrapRawCodeInput(tc.ActionName, tc.ActionInput);
                if (deterministicFix != null && JsonSanitizer.IsValidJson(deterministicFix))
                {
                    toolCalls[i] = tc with { ActionInput = deterministicFix };
                    if (debugMode)
                    {
                        await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                            $"[DEBUG] Wrapped raw code input as JSON for {tc.ActionName} (deterministic fast-path).");
                    }
                }
                else
                {
                    if (debugMode)
                    {
                        await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                            $"[DEBUG] ActionInput for {tc.ActionName} is invalid after fast-path repair. Requesting LLM self-repair...");
                    }

                    // Use a shorter timeout for repair calls - 30 seconds max
                    string repairedInput;
                    using var repairCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    repairCts.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        repairedInput = await LlmRepairJsonAsync(chatService, kernel, tc.ActionInput, sessionId, step, debugMode, repairCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        if (debugMode)
                        {
                            await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.Warning,
                                $"[DEBUG] LLM JSON self-repair timed out (30s) for {tc.ActionName}. Using raw input.");
                        }
                        repairedInput = tc.ActionInput;
                    }

                    if (JsonSanitizer.IsValidJson(repairedInput))
                    {
                        toolCalls[i] = tc with { ActionInput = repairedInput };
                        if (debugMode)
                        {
                            await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                                $"[DEBUG] LLM JSON self-repair succeeded for {tc.ActionName}.");
                        }
                    }
                    else if (debugMode)
                    {
                        await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.Warning,
                            $"[DEBUG] LLM JSON self-repair failed for {tc.ActionName}. Falling back to raw input.");
                    }
                }
            }
        }

        // Broadcast thinking immediately so the UI shows it
        if (!string.IsNullOrWhiteSpace(thought))
        {
            await _taskStore.BroadcastThoughtAsync(sessionId, thought, $"Subagent {step}", null);
        }

        // ── Check for Actions vs FinalAnswer ───────────────────────────────
        var validActions = toolCalls
            .Where(a => !a.ActionName.Equals(ReactFormatTokens.ActionNone, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // If the model produced valid actions, execute them first and defer FinalAnswer to the next step
        if (validActions.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(finalAnswer) && debugMode)
            {
                await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                    $"[DEBUG] Subagent {step} produced both Actions ({validActions.Count}) and FinalAnswer. Executing actions first; FinalAnswer deferred until observations are available.");
            }
            return new SubagentResult(thought, plan, toolCalls.AsReadOnly(), null, false, null);
        }

        // If no actions (or only Action: None) and FinalAnswer is present
        if (!string.IsNullOrWhiteSpace(finalAnswer))
        {
            return new SubagentResult(thought, plan, Array.Empty<ToolAction>(), finalAnswer, false, null);
        }

        return new SubagentResult(thought, plan, toolCalls.AsReadOnly(), null, false, null);
    }

    // ==========================================================================
    // Fallback: Single-call approach (used when Phase 1 fails)
    // ==========================================================================

    private async Task<SubagentResult> ExecuteSingleCallFallbackAsync(
        Guid sessionId,
        Kernel kernel,
        IChatCompletionService chatService,
        string systemPrompt,
        string stateInjection,
        int step,
        int maxContextTokens,
        string modelId,
        bool debugMode,
        CancellationToken ct)
    {
        _logger.LogInformation("Using single-call fallback for session {SessionId} step {Step}", sessionId, step);

        var chatHistory = new ChatHistory(systemPrompt);
        chatHistory.AddUserMessage($"[TASK STATE]\n{stateInjection}\n\nBased on the state above, please proceed with your next action or provide the final result.");

        var executionSettings = PromptSettingsHelper.BuildSettings(
            _aiOptions.Inference.MainExecutionPromptSettings,
            new Dictionary<string, object>
            {
                { "num_predict", _aiOptions.MaxTokensPerRequest },
                { "max_tokens", _aiOptions.MaxTokensPerRequest },
                { "num_ctx", maxContextTokens }
            },
            ProviderResolutionHelper.ResolvePrimaryProviderType(_aiOptions),
            enableToolCalling: true);

        int currentTokens = _tokenCounter.CountTokens(chatHistory, modelId);
        await _taskStore.BroadcastTokenCountAsync(sessionId, currentTokens, maxContextTokens, $"Subagent {step} (Fallback)");

        string content = "";
        IReadOnlyList<ToolAction> toolCalls = Array.Empty<ToolAction>();
        string? plan = null;
        string? finalAnswer = null;

        int retryCount = 0;
        while (retryCount < 3)
        {
            try
            {
                var (c, tc, wasTruncated) = await _llmCallService.CallLlmWithRetryAsync(
                    chatService, chatHistory, executionSettings, kernel,
                    sessionId, step, null, false, debugMode, ct);
                content = c;
                toolCalls = tc;

                if (wasTruncated)
                    return new SubagentResult(content, null, toolCalls, null, true, "Response was truncated. Context limit reached.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new SubagentResult(null, null, Array.Empty<ToolAction>(), null, true, $"LLM Call Failed: {ex.Message}");
            }

            plan = SubagentResponseParser.ExtractSection(content, ReactFormatTokens.Plan,
                new[] { ReactFormatTokens.Action, ReactFormatTokens.FinalAnswer });
            finalAnswer = SubagentResponseParser.ExtractSection(content, ReactFormatTokens.FinalAnswer, Array.Empty<string>());

            if (toolCalls.Count == 0 && string.IsNullOrWhiteSpace(finalAnswer))
            {
                // M2: Check for text-format Action: and ActionInput: lines for models without native tool calling
                var textActions = SubagentResponseParser.ExtractAllActionPairs(content);
                if (textActions.Count > 0)
                {
                    for (int i = 0; i < textActions.Count; i++)
                    {
                        var tc = textActions[i];
                        if (string.IsNullOrWhiteSpace(tc.ActionInput) || tc.ActionInput == "{}")
                            continue;

                        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(tc.ActionInput);
                        if (JsonSanitizer.IsValidJson(sanitized))
                        {
                            if (wasSanitized)
                            {
                                textActions[i] = tc with { ActionInput = sanitized };
                            }
                        }
                    }
                    toolCalls = textActions;
                }
            }

            if (toolCalls.Count == 0 && string.IsNullOrWhiteSpace(finalAnswer))
            {
                chatHistory.AddAssistantMessage(content);
                chatHistory.AddUserMessage("You did not invoke any tools, and you did not provide a 'FinalAnswer:'. You MUST either invoke a tool using the native tool calling capability to execute your plan, or provide a 'FinalAnswer:' to complete the task.");
                retryCount++;
                continue;
            }
            break;
        }

        var thought = SubagentResponseParser.ExtractSection(content, ReactFormatTokens.Thought,
            new[] { ReactFormatTokens.Action, ReactFormatTokens.Plan, ReactFormatTokens.FinalAnswer });

        if (!string.IsNullOrWhiteSpace(thought))
        {
            await _taskStore.BroadcastThoughtAsync(sessionId, thought, $"Subagent {step} (Fallback)", null);
        }

        return new SubagentResult(thought, plan, toolCalls, finalAnswer, false, null);
    }

    /// <summary>
    /// Instructs the LLM to self-repair a malformed JSON argument block.
    /// </summary>
    internal async Task<string> LlmRepairJsonAsync(
        IChatCompletionService chatService,
        Kernel kernel,
        string malformedJson,
        Guid sessionId,
        int step,
        bool debugMode,
        CancellationToken ct)
    {
        var repairHistory = new ChatHistory(
            "You are a JSON repair assistant. Your job is to take a malformed JSON string and output it as a valid, parseable JSON object. " +
            "Do NOT modify the values, keys, or code content (like Python scripts). Only fix quotes, escaping, missing braces, brackets, or commas. " +
            "Output ONLY the raw valid JSON. Do NOT wrap it in markdown blocks or include any other text.");
        
        repairHistory.AddUserMessage(malformedJson);

        var settings = PromptSettingsHelper.BuildSettings(
            _aiOptions.Inference.MainExecutionPromptSettings,
            new Dictionary<string, object>
            {
                { "temperature", 0.0 },
                { "num_predict", 2048 },
                { "max_tokens", 2048 }
            },
            ProviderResolutionHelper.ResolvePrimaryProviderType(_aiOptions),
            enableToolCalling: false);

        try
        {
            var (repairedContent, _, _) = await _llmCallService.CallLlmWithRetryAsync(
                chatService, repairHistory, settings, kernel,
                sessionId, step, "JsonRepair", false, debugMode, ct);

            var trimmed = repairedContent.Trim();
            
            if (trimmed.StartsWith("```"))
            {
                int firstNewline = trimmed.IndexOf('\n');
                if (firstNewline >= 0)
                {
                    int lastFence = trimmed.LastIndexOf("```");
                    trimmed = lastFence > firstNewline
                        ? trimmed[(firstNewline + 1)..lastFence].Trim()
                        : trimmed[(firstNewline + 1)..].Trim();
                }
            }

            return trimmed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM JSON repair failed. Returning original raw JSON.");
            return malformedJson;
        }
    }
}
