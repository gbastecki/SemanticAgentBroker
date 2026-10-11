using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.Core.LoadBalancing;

/// <summary>
/// Load balancer that selects the connection with the fewest active requests
/// from the pool of enabled, healthy connections that support the requested model.
/// Thread-safe via atomic Interlocked operations.
/// </summary>
public class LeastUsedConnectionLoadBalancer : IConnectionLoadBalancer
{
    private readonly AiProviderOptions _options;
    private readonly IEndpointHealthMonitor _healthMonitor;
    private readonly ILogger<LeastUsedConnectionLoadBalancer> _logger;
    private readonly ConcurrentDictionary<string, int> _activeRequestCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _selectionLock = new();

    /// <summary>
    /// Initializes a new instance of <see cref="LeastUsedConnectionLoadBalancer"/>.
    /// </summary>
    /// <param name="options">The AI provider options.</param>
    /// <param name="healthMonitor">The endpoint health monitor.</param>
    /// <param name="logger">Logger instance.</param>
    public LeastUsedConnectionLoadBalancer(
        AiProviderOptions options,
        IEndpointHealthMonitor healthMonitor,
        ILogger<LeastUsedConnectionLoadBalancer> logger)
    {
        _options = options;
        _healthMonitor = healthMonitor;
        _logger = logger;

        // Pre-initialize counters for all connections
        foreach (var conn in options.Connections)
        {
            _activeRequestCounts.TryAdd(conn.Id, 0);
        }
    }

    /// <inheritdoc />
    public Task<ProviderConnection> SelectConnectionAsync(string modelId, CancellationToken ct = default)
    {
        // 1. Filter: enabled, has the model, is healthy
        var candidates = _options.Connections
            .Where(c => c.IsEnabled)
            .Where(c => c.Models.Any(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)))
            .Where(c => _healthMonitor.IsHealthy(c.Id))
            .ToList();

        if (candidates.Count == 0)
        {
            // Fallback: try enabled connections with the model regardless of health
            candidates = _options.Connections
                .Where(c => c.IsEnabled)
                .Where(c => c.Models.Any(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No enabled connections found that support model '{modelId}'. " +
                    $"Available connections: [{string.Join(", ", _options.Connections.Select(c => $"{c.Id}(enabled={c.IsEnabled})"))}]");
            }

            _logger.LogWarning(
                "No healthy connections found for model '{ModelId}'. Falling back to unhealthy candidates: [{Candidates}]",
                modelId, string.Join(", ", candidates.Select(c => c.EndpointName)));
        }

        // 2. Sort by active request count → pick least used and atomically increment under lock
        ProviderConnection selected;
        int activeCount;
        lock (_selectionLock)
        {
            selected = candidates
                .OrderBy(c => _activeRequestCounts.GetValueOrDefault(c.Id, 0))
                .First();

            _activeRequestCounts.AddOrUpdate(selected.Id, 1, (_, current) => current + 1);
            activeCount = _activeRequestCounts.GetValueOrDefault(selected.Id, 0);
        }

        _logger.LogDebug(
            "[LoadBalancer] Selected endpoint '{EndpointName}' (Id={ConnectionId}) for model '{ModelId}'. " +
            "Active requests on this endpoint: {ActiveCount}. Candidates evaluated: {CandidateCount}",
            selected.EndpointName, selected.Id, modelId, activeCount, candidates.Count);

        return Task.FromResult(selected);
    }

    /// <inheritdoc />
    public void ReleaseConnection(string connectionId)
    {
        int activeCount;
        lock (_selectionLock)
        {
            _activeRequestCounts.AddOrUpdate(connectionId, 0, (_, current) => Math.Max(0, current - 1));
            activeCount = _activeRequestCounts.GetValueOrDefault(connectionId, 0);
        }

        _logger.LogDebug(
            "[LoadBalancer] Released endpoint '{ConnectionId}'. Active requests: {ActiveCount}",
            connectionId, activeCount);
    }

    /// <inheritdoc />
    public int GetActiveRequestCount(string connectionId)
    {
        return _activeRequestCounts.GetValueOrDefault(connectionId, 0);
    }
}
