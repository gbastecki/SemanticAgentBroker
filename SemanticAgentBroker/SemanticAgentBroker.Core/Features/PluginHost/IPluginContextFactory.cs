using SemanticAgentBroker.Core.Features.PluginHost.Implementations;

namespace SemanticAgentBroker.Core.Features.PluginHost;

/// <summary>
/// Factory interface for creating plugin execution contexts.
/// </summary>
public interface IPluginContextFactory
{
    /// <summary>
    /// Creates an initialization context for plugin discovery and startup.
    /// </summary>
    /// <returns>A new <see cref="PluginExecutionContext"/> for initialization.</returns>
    PluginExecutionContext CreateInitContext();

    /// <summary>
    /// Creates an execution context for running plugin methods within a specific task/session and workspace.
    /// </summary>
    /// <param name="workspacePath">The physical directory path assigned to the task.</param>
    /// <param name="sessionId">The unique task/session identifier.</param>
    /// <param name="pluginId">The identifier of the plugin being executed.</param>
    /// <param name="dbConfig">Optional configuration values stored in the database for this plugin.</param>
    /// <param name="appSettingsConfig">Optional configuration values from application settings.</param>
    /// <param name="kernelServices">Optional scoped service provider from the Semantic Kernel.</param>
    /// <returns>A configured <see cref="PluginExecutionContext"/>.</returns>
    PluginExecutionContext CreateExecutionContext(
        string workspacePath,
        Guid sessionId,
        string pluginId,
        IReadOnlyDictionary<string, string>? dbConfig = null,
        IReadOnlyDictionary<string, string>? appSettingsConfig = null,
        IServiceProvider? kernelServices = null);
}
