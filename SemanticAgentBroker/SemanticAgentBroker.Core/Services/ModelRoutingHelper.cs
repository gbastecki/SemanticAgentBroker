using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.Core.Services;

/// <summary>
/// Shared utility for resolving model routing configuration.
/// Eliminates duplicate ResolveMaxContextTokens across engines.
/// </summary>
public static class ModelRoutingHelper
{
    /// <summary>
    /// Resolves the maximum context window size in tokens for the PrimaryTextModel.
    /// Checks sticky ProviderId first, then scans enabled connections.
    /// Falls back to 16384 if not configured.
    /// </summary>
    public static int ResolveMaxContextTokens(AiProviderOptions aiOptions)
    {
        var modelRoute = aiOptions.ModelRouting.GetValueOrDefault(TaskModelRole.PrimaryTextModel);
        if (modelRoute == null) return 16384;

        // If sticky ProviderId is set, resolve from that specific connection
        if (!string.IsNullOrEmpty(modelRoute.ProviderId))
        {
            var connection = aiOptions.Connections.FirstOrDefault(c => c.Id.Equals(modelRoute.ProviderId, StringComparison.OrdinalIgnoreCase));
            var model = connection?.Models.FirstOrDefault(m => m.Id.Equals(modelRoute.ModelId, StringComparison.OrdinalIgnoreCase));
            return model?.MaxContextTokens ?? 16384;
        }

        // Otherwise find any enabled connection with this model
        foreach (var conn in aiOptions.Connections.Where(c => c.IsEnabled))
        {
            var model = conn.Models.FirstOrDefault(m => m.Id.Equals(modelRoute.ModelId, StringComparison.OrdinalIgnoreCase));
            if (model != null) return model.MaxContextTokens;
        }

        return 16384;
    }
}
