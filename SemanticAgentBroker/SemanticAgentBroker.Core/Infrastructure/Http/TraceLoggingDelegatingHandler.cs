using Microsoft.Extensions.Logging;

namespace SemanticAgentBroker.Core.Infrastructure.Http;

/// <summary>
/// Delegating handler that writes sanitized raw HTTP request and response payloads to disk when trace logging is enabled.
/// </summary>
public class TraceLoggingDelegatingHandler : DelegatingHandler
{
    private readonly string _logDirectory;
    private readonly ILogger<TraceLoggingDelegatingHandler> _logger;
    private readonly string _providerId;

    /// <summary>
    /// Initializes a new instance of <see cref="TraceLoggingDelegatingHandler"/>.
    /// </summary>
    /// <param name="logDirectory">Directory where trace logs will be saved.</param>
    /// <param name="providerId">AI provider identifier.</param>
    /// <param name="logger">Logger instance.</param>
    public TraceLoggingDelegatingHandler(string logDirectory, string providerId, ILogger<TraceLoggingDelegatingHandler> logger)
    {
        _logDirectory = logDirectory;
        _providerId = providerId;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!TraceLoggingContext.IsTraceEnabled)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var requestId = Guid.NewGuid().ToString("N")[..8];
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var taskIdPrefix = TraceLoggingContext.CurrentTaskId.HasValue ? $"{TraceLoggingContext.CurrentTaskId.Value}_" : "";
        var fileName = $"{timestamp}_{taskIdPrefix}{_providerId}_{requestId}.log";
        var filePath = Path.Combine(_logDirectory, fileName);

        try
        {
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }

            var requestContent = request.Content != null 
                ? await request.Content.ReadAsStringAsync(cancellationToken) 
                : string.Empty;

            var sanitizedHeaders = FormatSanitizedHeaders(request.Headers, request.Content?.Headers);

            var logEntry = $"=== REQUEST ===\n" +
                           $"Method: {request.Method}\n" +
                           $"Url: {request.RequestUri}\n" +
                           $"Headers:\n{sanitizedHeaders}\n" +
                           $"Content:\n{requestContent}\n\n";

            await File.AppendAllTextAsync(filePath, logEntry, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write trace log for request {RequestId}", requestId);
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            try
            {
                var logEntry = $"=== CANCELLED / TIMED OUT ===\n" +
                               $"Message: {ex.Message}\n";
                await File.AppendAllTextAsync(filePath, logEntry, CancellationToken.None);
            }
            catch (Exception writeEx)
            {
                _logger.LogWarning(writeEx, "Failed to write cancellation trace log for request {RequestId}", requestId);
            }

            throw;
        }
        catch (Exception ex)
        {
            try
            {
                var logEntry = $"=== EXCEPTION ===\n" +
                               $"Type: {ex.GetType().FullName}\n" +
                               $"Message: {ex.Message}\n";
                await File.AppendAllTextAsync(filePath, logEntry, CancellationToken.None);
            }
            catch (Exception writeEx)
            {
                _logger.LogWarning(writeEx, "Failed to write exception trace log for request {RequestId}", requestId);
            }

            throw;
        }

        try
        {
            var isEventStream = string.Equals(response.Content?.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
            var responseContent = isEventStream
                ? "[SSE stream - body not buffered to preserve real-time streaming]"
                : (response.Content != null 
                    ? await response.Content.ReadAsStringAsync(cancellationToken) 
                    : string.Empty);

            var sanitizedResponseHeaders = FormatSanitizedHeaders(response.Headers, response.Content?.Headers);

            var logEntry = $"=== RESPONSE ===\n" +
                           $"StatusCode: {response.StatusCode}\n" +
                           $"Headers:\n{sanitizedResponseHeaders}\n" +
                           $"Content:\n{responseContent}\n";

            await File.AppendAllTextAsync(filePath, logEntry, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write trace log for response {RequestId}", requestId);
        }

        return response;
    }

    private static readonly System.Collections.Generic.HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "X-Api-Key",
        "api-key",
        "Cookie",
        "Set-Cookie",
        "OpenAI-Organization",
        "X-OpenAI-Api-Key",
        "X-Anthropic-Api-Key",
        "X-Auth-Token"
    };

    private static string FormatSanitizedHeaders(System.Net.Http.Headers.HttpHeaders? headers, System.Net.Http.Headers.HttpContentHeaders? contentHeaders = null)
    {
        var sb = new System.Text.StringBuilder();
        if (headers != null)
        {
            foreach (var h in headers)
            {
                if (SensitiveHeaderNames.Contains(h.Key))
                {
                    sb.AppendLine($"{h.Key}: [REDACTED]");
                }
                else
                {
                    sb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
                }
            }
        }
        if (contentHeaders != null)
        {
            foreach (var h in contentHeaders)
            {
                if (SensitiveHeaderNames.Contains(h.Key))
                {
                    sb.AppendLine($"{h.Key}: [REDACTED]");
                }
                else
                {
                    sb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
                }
            }
        }
        return sb.ToString().TrimEnd();
    }
}
