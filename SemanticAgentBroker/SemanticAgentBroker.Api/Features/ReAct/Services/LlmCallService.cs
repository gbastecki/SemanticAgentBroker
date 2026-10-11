using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Core.Constants;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.Api.Features.ReAct.Services;

/// <summary>
/// Handles LLM call execution with retry, streaming, stop-marker detection,
/// and output truncation heuristics.
/// </summary>
public class LlmCallService
{
    private readonly AgentTaskStore _taskStore;
    private readonly AiProviderOptions _aiOptions;
    private readonly AgentSystemOptions _sysOptions;
    private readonly ILogger<LlmCallService> _logger;

    /// <summary>
    /// Default fallback per-step timeout if DefaultTimeoutMinutes is not configured.
    /// Prevents a single streaming response from consuming the full HttpClient timeout.
    /// </summary>
    private static readonly TimeSpan FallbackStepTimeout = TimeSpan.FromMinutes(15);

    public LlmCallService(
        AgentTaskStore taskStore,
        AiProviderOptions aiOptions,
        AgentSystemOptions sysOptions,
        ILogger<LlmCallService> logger)
    {
        _taskStore = taskStore;
        _aiOptions = aiOptions;
        _sysOptions = sysOptions;
        _logger = logger;
    }

    public async Task<(string Content, IReadOnlyList<ToolAction> ToolCalls, bool WasTruncated)> CallLlmWithRetryAsync(
        IChatCompletionService chatService,
        ChatHistory chatHistory,
        PromptExecutionSettings executionSettings,
        Kernel kernel,
        Guid sessionId,
        int step,
        string? tracePath,
        bool traceMode,
        bool debugMode,
        CancellationToken ct)
    {
        int maxRetries = _aiOptions.Inference.MaxLlmRetries; // -1 for infinite retries
        int baseDelaySeconds = _aiOptions.Inference.LlmRetryDelaySeconds > 0 ? _aiOptions.Inference.LlmRetryDelaySeconds : 5;
        int maxDelaySeconds = _aiOptions.Inference.MaxLlmRetryDelaySeconds > 0 ? _aiOptions.Inference.MaxLlmRetryDelaySeconds : 30;
        Exception? lastException = null;

        int attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            if (attempt > 1)
            {
                int delaySeconds = Math.Min(maxDelaySeconds, (int)(baseDelaySeconds * Math.Pow(1.5, attempt - 2)));
                string reason = lastException != null ? DescribeHttpException(lastException) : "Transient failure";

                string retryText = maxRetries < 0
                    ? $"[RETRY] {reason}. Waiting {delaySeconds}s before retry (Attempt {attempt})..."
                    : $"[RETRY] {reason}. Waiting {delaySeconds}s before retry (Attempt {attempt}/{maxRetries + 1})...";

                _logger.LogWarning("[Session {SessionId} Step {Step}] {RetryText}", sessionId, step, retryText);
                await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System, retryText);
                await _taskStore.BroadcastActivityAsync(sessionId, $"LLM provider offline/failing ({reason}). Retrying in {delaySeconds}s...");

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
            }

            try
            {
                using var stepTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var stepTimeout = _aiOptions.DefaultTimeoutMinutes > 0
                    ? TimeSpan.FromMinutes(_aiOptions.DefaultTimeoutMinutes)
                    : FallbackStepTimeout;
                stepTimeoutCts.CancelAfter(stepTimeout);

                var response = await chatService.GetChatMessageContentAsync(
                    chatHistory, executionSettings: executionSettings, kernel: kernel, cancellationToken: stepTimeoutCts.Token);

                var content = response.Content ?? string.Empty;
                var toolCalls = new List<ToolAction>();

                // Extract Native Tool Calls
                var functionCalls = response.Items.OfType<FunctionCallContent>();
                foreach (var fc in functionCalls)
                {
                    string actionName = string.IsNullOrEmpty(fc.PluginName) ? fc.FunctionName : $"{fc.PluginName}-{fc.FunctionName}";
                    string argsJson = fc.Arguments != null ? System.Text.Json.JsonSerializer.Serialize(fc.Arguments) : "{}";
                    toolCalls.Add(new ToolAction(actionName, argsJson));
                }

                // Append trace
                if (traceMode && tracePath != null && !string.IsNullOrEmpty(content))
                {
                    try
                    {
                        await using var traceWriter = new StreamWriter(
                            new FileStream(tracePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        { AutoFlush = true };
                        await traceWriter.WriteAsync($"━━━ LLM RESPONSE: ━━━\n{content}\n\n");
                        
                        if (toolCalls.Count > 0)
                        {
                            await traceWriter.WriteAsync($"[NATIVE TOOL CALLS ({toolCalls.Count})]\n");
                            foreach (var tc in toolCalls)
                            {
                                await traceWriter.WriteAsync($"Action: {tc.ActionName}\nArguments: {tc.ActionInput}\n\n");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        _logger.LogWarning(ex, "Failed to write trace log to {TracePath}.", tracePath);
                    }
                }

                // Token Truncation Detection
                bool wasTruncated = false;
                if (response.Metadata?.ContainsKey("FinishReason") == true)
                {
                    var finishReason = response.Metadata["FinishReason"]?.ToString()?.ToLowerInvariant();
                    wasTruncated = finishReason is "length" or "max_tokens";
                }

                return (content, toolCalls, wasTruncated);
            }
            catch (Exception ex) when (
                ex is HttpRequestException or TimeoutException ||
                ex.GetType().Name == "HttpOperationException" ||
                (ex is TaskCanceledException tce && tce.InnerException is TimeoutException) ||
                (ex is OperationCanceledException oce && !ct.IsCancellationRequested))
            {
                lastException = ex;

                if (IsNonTransientException(ex))
                {
                    _logger.LogWarning("[Session {SessionId} Step {Step}] Non-transient HTTP error encountered: {Error}. Skipping retries.",
                        sessionId, step, DescribeHttpException(ex));
                    break;
                }

                if (maxRetries >= 0 && attempt >= (maxRetries + 1))
                {
                    break;
                }
            }
        }

        string finalError = DescribeHttpException(lastException!);
        _logger.LogError(lastException, "All retry attempts exhausted for session {SessionId} step {Step}. Reason: {Reason}", sessionId, step, finalError);
        throw new InvalidOperationException(
            $"LLM provider is offline or failing ({finalError}). Please ensure Ollama or vLLM is running at the configured endpoint.", lastException);
    }

    private static bool IsNonTransientException(Exception ex)
    {
        if (ex is HttpRequestException hre && hre.StatusCode != null)
        {
            int code = (int)hre.StatusCode;
            return code is 400 or 401 or 403 or 404 or 413 or 422;
        }

        var exTypeName = ex.GetType().Name;
        if (exTypeName == "HttpOperationException" || exTypeName.Contains("Http"))
        {
            var statusCodeProp = ex.GetType().GetProperty("StatusCode");
            if (statusCodeProp?.GetValue(ex) is object statusCodeObj &&
                statusCodeObj is System.Net.HttpStatusCode statusCode)
            {
                int code = (int)statusCode;
                return code is 400 or 401 or 403 or 404 or 413 or 422;
            }
        }

        return false;
    }

    private static string DescribeHttpException(Exception ex)
    {
        if (ex is HttpRequestException hre)
        {
            if (hre.StatusCode != null)
            {
                int code = (int)hre.StatusCode;
                return code switch
                {
                    503 => "HTTP 503 (Service Unavailable - LLM server starting up or overloaded)",
                    502 => "HTTP 502 (Bad Gateway - LLM endpoint unreachable)",
                    504 => "HTTP 504 (Gateway Timeout - LLM response timed out)",
                    429 => "HTTP 429 (Too Many Requests - Rate limit exceeded)",
                    500 => "HTTP 500 (Internal Server Error)",
                    _ => $"HTTP {code} ({hre.StatusCode})"
                };
            }

            if (hre.Message.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
                hre.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused })
            {
                return "Connection refused - LLM server is not running";
            }
        }

        var exTypeName = ex.GetType().Name;
        if (exTypeName == "HttpOperationException" || exTypeName.Contains("Http"))
        {
            var statusCodeProp = ex.GetType().GetProperty("StatusCode");
            if (statusCodeProp?.GetValue(ex) is object statusCodeObj)
            {
                return $"HTTP {statusCodeObj} (LLM server error)";
            }
        }

        if (ex.InnerException is System.Net.Sockets.SocketException se)
        {
            return $"Network error ({se.SocketErrorCode}) - LLM server offline";
        }

        if (ex is TimeoutException || (ex is TaskCanceledException tce && tce.InnerException is TimeoutException))
        {
            return "Request timed out";
        }

        return ex.Message;
    }
}
