using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SemanticAgentBroker.Shared.Configuration;
using System.Collections.Concurrent;

namespace SemanticAgentBroker.Core.LoadBalancing;

/// <summary>
/// Background service that periodically probes each enabled AI provider endpoint
/// to determine if it is healthy and responding to requests.
/// Health status is cached in-memory and queried by the load balancer.
/// </summary>
public class EndpointHealthCheckService : BackgroundService, IEndpointHealthMonitor
{
    private readonly AiProviderOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EndpointHealthCheckService> _logger;
    private readonly ConcurrentDictionary<string, bool> _healthStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _initializedConnections = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>
    /// Initializes a new instance of <see cref="EndpointHealthCheckService"/>.
    /// </summary>
    /// <param name="options">The AI provider options.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">Logger instance.</param>
    public EndpointHealthCheckService(
        AiProviderOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<EndpointHealthCheckService> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        if (_options.HealthCheck.Enabled)
        {
            // Assume unhealthy until first probe completes (pessimistic).
            // Prevents routing to dead endpoints during the startup window.
            foreach (var conn in options.Connections.Where(c => c.IsEnabled))
            {
                _healthStatus[conn.Id] = false;
            }
        }
    }

    /// <inheritdoc />
    public bool IsHealthy(string connectionId)
    {
        if (!_options.HealthCheck.Enabled)
        {
            return true;
        }

        return _healthStatus.GetValueOrDefault(connectionId, true);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.HealthCheck.Enabled)
        {
            _logger.LogInformation("Endpoint health checking is disabled via configuration");
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.HealthCheck.IntervalSeconds);
        _logger.LogInformation(
            "Endpoint health check service started. Interval: {Interval}s, Timeout: {Timeout}s",
            _options.HealthCheck.IntervalSeconds, _options.HealthCheck.TimeoutSeconds);

        // Initial check
        try
        {
            await RunHealthChecksAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during initial health checks run");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await RunHealthChecksAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in health check loop");
            }
        }
    }

    private async Task RunHealthChecksAsync(CancellationToken ct)
    {
        var enabledConnections = _options.Connections.Where(c => c.IsEnabled).ToList();
        var tasks = enabledConnections.Select(conn => CheckEndpointAsync(conn, ct));
        await Task.WhenAll(tasks);
    }

    private async Task CheckEndpointAsync(ProviderConnection connection, CancellationToken ct)
    {
        var previousStatus = _healthStatus.GetValueOrDefault(connection.Id, true);

        try
        {
            using var httpClient = _httpClientFactory.CreateClient(connection.Id);
            httpClient.Timeout = TimeSpan.FromSeconds(_options.HealthCheck.TimeoutSeconds);

            var healthPath = _options.HealthCheck.EndpointPaths
                .GetValueOrDefault(connection.ProviderType, "/");

            using var response = await httpClient.GetAsync(healthPath, ct);

            var isHealthy = response.IsSuccessStatusCode;
            _healthStatus[connection.Id] = isHealthy;

            if (isHealthy)
            {
                if (!_initializedConnections.ContainsKey(connection.Id))
                {
                    var initialized = await EnsureOllamaModelContextAsync(connection, httpClient, ct);
                    if (initialized)
                    {
                        _initializedConnections.TryAdd(connection.Id, 0);
                    }
                }
            }
            else
            {
                _initializedConnections.TryRemove(connection.Id, out _);
            }

            // Log transitions only
            if (isHealthy != previousStatus)
            {
                if (isHealthy)
                {
                    _logger.LogInformation(
                        "Endpoint '{EndpointName}' ({ConnectionId}) is now HEALTHY at {Endpoint}",
                        connection.EndpointName, connection.Id, connection.Endpoint);
                }
                else
                {
                    _logger.LogWarning(
                        "Endpoint '{EndpointName}' ({ConnectionId}) is now UNHEALTHY at {Endpoint}. Status: {StatusCode}",
                        connection.EndpointName, connection.Id, connection.Endpoint, response.StatusCode);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _healthStatus[connection.Id] = false;
            _initializedConnections.TryRemove(connection.Id, out _);

            // Log transitions only
            if (previousStatus)
            {
                _logger.LogWarning(
                    "Endpoint '{EndpointName}' ({ConnectionId}) is now UNHEALTHY at {Endpoint}. Error: {Error}",
                    connection.EndpointName, connection.Id, connection.Endpoint, ex.Message);
            }
        }
        catch (Exception ex)
        {
            _healthStatus[connection.Id] = false;
            _initializedConnections.TryRemove(connection.Id, out _);

            _logger.LogError(
                ex,
                "Endpoint '{EndpointName}' ({ConnectionId}) health check encountered an unexpected error at {Endpoint}. Marked UNHEALTHY.",
                connection.EndpointName, connection.Id, connection.Endpoint);
        }
    }

    private async Task<bool> EnsureOllamaModelContextAsync(ProviderConnection connection, HttpClient httpClient, CancellationToken ct)
    {
        if (!connection.ProviderType.Equals("Ollama", StringComparison.OrdinalIgnoreCase) &&
            !connection.Id.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var baseUri = new Uri(connection.Endpoint);
            var hostUri = new Uri(baseUri.GetLeftPart(UriPartial.Authority));
            bool allSucceeded = true;

            foreach (var model in connection.Models)
            {
                if (string.IsNullOrEmpty(model.Id) || model.MaxContextTokens <= 0) continue;

                // 1. Inspect existing model parameters via /api/show
                var showUri = new Uri(hostUri, "/api/show");
                var showPayload = new { model = model.Id };
                using var showContent = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(showPayload),
                    System.Text.Encoding.UTF8,
                    "application/json");

                using var showResponse = await httpClient.PostAsync(showUri, showContent, ct);
                if (showResponse.IsSuccessStatusCode)
                {
                    var showJson = await showResponse.Content.ReadAsStringAsync(ct);
                    // Check if num_ctx parameter is already matching MaxContextTokens
                    if (showJson.Contains($"num_ctx\\t{model.MaxContextTokens}") ||
                        showJson.Contains($"\"num_ctx\":{model.MaxContextTokens}") ||
                        showJson.Contains($"num_ctx {model.MaxContextTokens}"))
                    {
                        _logger.LogDebug(
                            "[OllamaInitializer] Model '{ModelId}' on '{ConnectionId}' is already registered with num_ctx = {MaxContextTokens}. Skipping configuration & unload.",
                            model.Id, connection.Id, model.MaxContextTokens);
                        continue;
                    }
                }
                else if (showResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    // Non-404 error inspecting model (e.g. 500 or timeout) - retry next time
                    allSucceeded = false;
                    continue;
                }

                // 2. Mismatch detected - update model parameter via /api/create
                var createPayload = new
                {
                    model = model.Id,
                    from = model.Id,
                    parameters = new
                    {
                        num_ctx = model.MaxContextTokens
                    },
                    stream = false
                };

                using var createContent = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(createPayload),
                    System.Text.Encoding.UTF8,
                    "application/json");

                var createRequestUri = new Uri(hostUri, "/api/create");
                using var response = await httpClient.PostAsync(createRequestUri, createContent, ct);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "[OllamaInitializer] Updated Ollama model '{ModelId}' with num_ctx = {MaxContextTokens} via API",
                        model.Id, model.MaxContextTokens);

                    // 3. Unload old mismatched runner so fresh runner initializes with new context limit
                    var unloadPayload = new { model = model.Id, keep_alive = 0 };
                    using var unloadContent = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(unloadPayload),
                        System.Text.Encoding.UTF8,
                        "application/json");
                    var unloadRequestUri = new Uri(hostUri, "/api/generate");
                    using var unloadResponse = await httpClient.PostAsync(unloadRequestUri, unloadContent, ct);
                }
                else
                {
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OllamaInitializer] Failed to configure Ollama model context via API for connection '{ConnectionId}'", connection.Id);
            return false;
        }
    }
}
