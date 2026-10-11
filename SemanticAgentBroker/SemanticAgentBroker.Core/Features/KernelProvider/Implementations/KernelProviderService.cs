using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using SemanticAgentBroker.Core.LoadBalancing;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.Core.Features.KernelProvider.Implementations;

/// <inheritdoc />
public class KernelProviderService : IKernelProviderService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiProviderOptions _options;
    private readonly IConnectionLoadBalancer _loadBalancer;
    private readonly ILogger<KernelProviderService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="KernelProviderService"/>.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="options">The AI provider options.</param>
    /// <param name="loadBalancer">The connection load balancer.</param>
    /// <param name="logger">Logger instance.</param>
    public KernelProviderService(
        IHttpClientFactory httpClientFactory,
        AiProviderOptions options,
        IConnectionLoadBalancer loadBalancer,
        ILogger<KernelProviderService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _loadBalancer = loadBalancer;
        _logger = logger;
    }

    /// <inheritdoc />
    public IKernelBuilder CreateKernelBuilder(string providerId, string modelId)
    {
        var connection = _options.Connections.FirstOrDefault(c => c.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Provider with ID '{providerId}' not found.", nameof(providerId));

        var model = connection.Models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Model with ID '{modelId}' not found in provider '{providerId}'.", nameof(modelId));

        _logger.LogDebug(
            "[KernelProvider] Creating kernel for model '{ModelId}' on endpoint '{EndpointName}' ({ConnectionId}) - direct override",
            modelId, connection.EndpointName, connection.Id);

        return BuildKernelForConnection(connection, model);
    }

    /// <inheritdoc />
    public async Task<(IKernelBuilder Builder, string ConnectionId)> CreateKernelBuilderBalancedAsync(
        string modelId, CancellationToken ct = default)
    {
        var connection = await _loadBalancer.SelectConnectionAsync(modelId, ct);

        try
        {
            var model = connection.Models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Load balancer selected connection '{connection.Id}' but model '{modelId}' was not found in its model list.");

            _logger.LogDebug(
                "[KernelProvider] Creating kernel for model '{ModelId}' on endpoint '{EndpointName}' ({ConnectionId}) - load balanced",
                modelId, connection.EndpointName, connection.Id);

            return (BuildKernelForConnection(connection, model), connection.Id);
        }
        catch
        {
            // Release the connection safely if kernel building fails (e.g., UriFormatException)
            _loadBalancer.ReleaseConnection(connection.Id);
            throw;
        }
    }

    private IKernelBuilder BuildKernelForConnection(ProviderConnection connection, ModelConfig model)
    {
        var builder = Kernel.CreateBuilder();

        if (connection.ProviderType.Equals("OpenAiCompatible", StringComparison.OrdinalIgnoreCase) ||
            connection.ProviderType.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ||
            connection.ProviderType.Equals("OpenAi", StringComparison.OrdinalIgnoreCase))
        {
            var httpClient = _httpClientFactory.CreateClient(connection.Id);
            
            Uri? endpoint = null;
            if (!connection.ProviderType.Equals("OpenAi", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(connection.Endpoint) ||
                    !Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out endpoint) ||
                    (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException(
                        $"Connection '{connection.Id}' has an invalid endpoint URL: '{connection.Endpoint}'. " +
                        "Ensure the URL includes a valid HTTP or HTTPS scheme (e.g., http://).");
                }
            }

            if (endpoint != null)
            {
                builder.AddOpenAIChatCompletion(
                    modelId: model.Id ?? "default",
                    endpoint: endpoint,
                    apiKey: string.IsNullOrEmpty(connection.ApiKey) ? "dummy-key" : connection.ApiKey,
                    httpClient: httpClient);
            }
            else
            {
                builder.AddOpenAIChatCompletion(
                    modelId: model.Id ?? "default",
                    apiKey: string.IsNullOrEmpty(connection.ApiKey) ? "dummy-key" : connection.ApiKey,
                    httpClient: httpClient);
            }
        }
        else
        {
            throw new NotSupportedException($"ProviderType '{connection.ProviderType}' is not supported.");
        }

        return builder;
    }
}
