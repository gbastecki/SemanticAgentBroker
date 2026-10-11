using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.Core.LoadBalancing;

/// <summary>
/// Selects the best available provider connection for a given model,
/// respecting IsEnabled state and endpoint health.
/// </summary>
public interface IConnectionLoadBalancer
{
    /// <summary>
    /// Selects the best enabled, healthy connection that supports the given model.
    /// Increments the active request count on the selected connection.
    /// Caller MUST call <see cref="ReleaseConnection"/> when the request completes.
    /// </summary>
    /// <param name="modelId">The model identifier to route to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The selected provider connection.</returns>
    /// <exception cref="InvalidOperationException">No healthy, enabled connections found for the model.</exception>
    Task<ProviderConnection> SelectConnectionAsync(string modelId, CancellationToken ct = default);

    /// <summary>
    /// Reports that a request completed on a connection (decrements active request count).
    /// </summary>
    /// <param name="connectionId">The connection ID returned by <see cref="SelectConnectionAsync"/>.</param>
    void ReleaseConnection(string connectionId);

    /// <summary>
    /// Gets the current active request count for a connection.
    /// </summary>
    int GetActiveRequestCount(string connectionId);
}
