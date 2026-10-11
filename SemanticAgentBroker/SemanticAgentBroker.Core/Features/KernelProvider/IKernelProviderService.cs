using Microsoft.SemanticKernel;

namespace SemanticAgentBroker.Core.Features.KernelProvider;

/// <summary>
/// Service that creates and configures <see cref="IKernelBuilder"/> instances for AI providers.
/// </summary>
public interface IKernelProviderService
{
    /// <summary>
    /// Creates a kernel builder for a specific provider and model.
    /// Use this overload when you need to bypass the load balancer.
    /// </summary>
    /// <param name="providerId">The AI provider identifier.</param>
    /// <param name="modelId">The target model identifier.</param>
    /// <returns>A configured <see cref="IKernelBuilder"/>.</returns>
    IKernelBuilder CreateKernelBuilder(string providerId, string modelId);

    /// <summary>
    /// Creates a kernel builder using the load balancer to select the best connection for the given model.
    /// Returns the builder and the connection ID (must be released via load balancer after use).
    /// </summary>
    /// <param name="modelId">The target model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple containing the configured <see cref="IKernelBuilder"/> and connection ID.</returns>
    Task<(IKernelBuilder Builder, string ConnectionId)> CreateKernelBuilderBalancedAsync(
        string modelId, CancellationToken ct = default);
}

