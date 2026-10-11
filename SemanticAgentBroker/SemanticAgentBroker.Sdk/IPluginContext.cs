using Microsoft.Extensions.Logging;

namespace SemanticAgentBroker.Sdk;

/// <summary>
/// Provides plugins access to framework services without coupling to the full DI container.
/// A new context instance is created per task execution scope.
/// </summary>
public interface IPluginContext
{
    /// <summary>Absolute path to the current task's isolated workspace directory.</summary>
    string WorkspacePath { get; }

    /// <summary>Current task/session identifier.</summary>
    Guid SessionId { get; }

    /// <summary>Logger scoped to the current plugin.</summary>
    ILogger Logger { get; }

    /// <summary>
    /// Access to plugin-specific configuration (hybrid: DB values override appsettings).
    /// Keys correspond to the plugin's declared <see cref="PluginConfigEntry.Key"/> values.
    /// </summary>
    IReadOnlyDictionary<string, string> Configuration { get; }

    /// <summary>
    /// Resolve a framework-provided service (e.g., IHttpClientFactory).
    /// Returns null if the service is not available.
    /// </summary>
    T? GetService<T>() where T : class;
}
