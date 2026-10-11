using SemanticAgentBroker.Shared.Configuration;
using System.Text;
using System.Text.Json.Nodes;

namespace SemanticAgentBroker.Core.Infrastructure.Http;

/// <summary>
/// Intercepts responses from OpenAI-compatible local endpoints (like Ollama)
/// and normalizes non-standard "finish_reason" values to prevent Microsoft.SemanticKernel's
/// OpenAI SDK wrapper from throwing ArgumentOutOfRangeException.
/// Also intercepts outgoing request payloads to ensure Ollama-specific options
/// (like "num_ctx") are nested inside an "options" object as required by Ollama's API.
/// </summary>
public class OllamaCompatibilityDelegatingHandler : DelegatingHandler
{
    private readonly ProviderConnection? _connection;

    /// <summary>
    /// Initializes a new instance of <see cref="OllamaCompatibilityDelegatingHandler"/>.
    /// </summary>
    /// <param name="connection">Optional provider connection configuration.</param>
    public OllamaCompatibilityDelegatingHandler(ProviderConnection? connection = null)
    {
        _connection = connection;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.Content != null)
        {
            var mediaType = request.Content.Headers.ContentType?.MediaType;
            if (mediaType == "application/json")
            {
                var bodyText = await request.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    try
                    {
                        var jsonNode = JsonNode.Parse(bodyText);
                        if (jsonNode is JsonObject jsonObject)
                        {
                            bool modified = false;

                            // Resolve target context size from connection model config or default to 32768
                            string? requestedModel = jsonObject["model"]?.ToString();
                            int targetMaxContext = 32768;

                            if (_connection != null && _connection.Models.Count > 0)
                            {
                                var modelConfig = _connection.Models.FirstOrDefault(m =>
                                    !string.IsNullOrEmpty(requestedModel) && m.Id.Equals(requestedModel, StringComparison.OrdinalIgnoreCase))
                                    ?? _connection.Models.FirstOrDefault();

                                if (modelConfig != null && modelConfig.MaxContextTokens > 0)
                                {
                                    targetMaxContext = modelConfig.MaxContextTokens;
                                }
                            }

                            // 1. Ensure top-level num_ctx is present
                            if (!jsonObject.ContainsKey("num_ctx") || jsonObject["num_ctx"] == null)
                            {
                                jsonObject["num_ctx"] = targetMaxContext;
                                modified = true;
                            }

                            // 2. Ensure options object exists with num_ctx
                            if (!jsonObject.TryGetPropertyValue("options", out var optionsNode) || optionsNode is not JsonObject)
                            {
                                optionsNode = new JsonObject();
                                jsonObject["options"] = optionsNode;
                                modified = true;
                            }

                            var optionsObj = optionsNode.AsObject();
                            if (!optionsObj.ContainsKey("num_ctx") || optionsObj["num_ctx"] == null)
                            {
                                optionsObj["num_ctx"] = targetMaxContext;
                                modified = true;
                            }

                            if (modified)
                            {
                                var oldContent = request.Content;
                                var newBody = jsonObject.ToJsonString();
                                var newContent = new StringContent(newBody, Encoding.UTF8, "application/json");
                                request.Content = newContent;
                                oldContent?.Dispose();
                            }
                        }
                    }
                    catch
                    {
                        // Ignore parsing failures for non-standard payloads
                    }
                }
            }
        }

        var response = await base.SendAsync(request, cancellationToken);
        
        if (response.Content != null)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType == "application/json")
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                
                try
                {
                    var jsonNode = JsonNode.Parse(content);
                    if (jsonNode is JsonObject jsonObj && jsonObj["choices"] is JsonArray choices)
                    {
                        bool modified = false;
                        foreach (var choiceNode in choices)
                        {
                            if (choiceNode is JsonObject choice && choice.ContainsKey("finish_reason"))
                            {
                                var rawValue = choice["finish_reason"]?.GetValue<string?>()?.ToLowerInvariant();
                                if (rawValue != null && rawValue is not ("stop" or "length" or "content_filter" or "tool_calls"))
                                {
                                    if (rawValue is "tool_call" or "function_call")
                                    {
                                        choice["finish_reason"] = "tool_calls";
                                    }
                                    else
                                    {
                                        choice["finish_reason"] = "stop";
                                    }
                                    modified = true;
                                }
                            }
                        }

                        if (modified)
                        {
                            var oldContent = response.Content;
                            var modifiedContent = jsonNode.ToJsonString();
                            var newContent = new StringContent(modifiedContent, Encoding.UTF8, "application/json");

                            foreach (var header in response.Content.Headers)
                            {
                                if (!header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                                    !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                                {
                                    newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                                }
                            }
                            response.Content = newContent;
                            oldContent?.Dispose();
                        }
                    }
                }
                catch
                {
                    // Fallback: keep original response content if not JSON parseable
                }
            }
        }

        
        return response;
    }
}
