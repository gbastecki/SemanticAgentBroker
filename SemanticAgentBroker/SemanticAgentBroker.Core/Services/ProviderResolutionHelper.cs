using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.Core.Services;

/// <summary>
/// Helper for resolving the primary AI provider type from configuration options.
/// </summary>
public static class ProviderResolutionHelper
{
    /// <summary>
    /// Resolves the provider type for the primary text model from the configured AI provider options.
    /// </summary>
    /// <param name="options">The AI provider options.</param>
    /// <returns>The provider type string (e.g., "Ollama", "OpenAiCompatible") or null if not resolved.</returns>
    public static string? ResolvePrimaryProviderType(AiProviderOptions options)
    {
        var modelRoute = options.ModelRouting.GetValueOrDefault(TaskModelRole.PrimaryTextModel);
        if (modelRoute == null) return null;

        if (!string.IsNullOrEmpty(modelRoute.ProviderId))
            return options.Connections.FirstOrDefault(c => c.Id.Equals(modelRoute.ProviderId, StringComparison.OrdinalIgnoreCase))?.ProviderType;

        return options.Connections
            .FirstOrDefault(c => c.IsEnabled && c.Models.Any(m => m.Id.Equals(modelRoute.ModelId, StringComparison.OrdinalIgnoreCase)))?.ProviderType;
    }
}
