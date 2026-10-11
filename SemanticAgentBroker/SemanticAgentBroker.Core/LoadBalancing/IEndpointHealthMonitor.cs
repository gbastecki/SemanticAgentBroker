namespace SemanticAgentBroker.Core.LoadBalancing;

/// <summary>
/// Provides endpoint health status information.
/// Implementations should periodically probe endpoints and cache results.
/// </summary>
public interface IEndpointHealthMonitor
{
    /// <summary>Returns true if the given connection endpoint is considered healthy.</summary>
    bool IsHealthy(string connectionId);
}
