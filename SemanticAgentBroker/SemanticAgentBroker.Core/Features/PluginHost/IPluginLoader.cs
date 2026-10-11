using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.Core.Features.PluginHost;

/// <summary>
/// Service responsible for scanning, loading, and managing plugin lifecycles in isolated load contexts.
/// </summary>
public interface IPluginLoader : IDisposable
{
    /// <summary>Fires when a plugin is successfully loaded.</summary>
    event Action<LoadedPlugin>? PluginLoaded;

    /// <summary>Fires when a plugin is discovered in the plugins directory.</summary>
    event Action<DiscoveredPlugin>? PluginDiscovered;

    /// <summary>Fires when a plugin is unloaded.</summary>
    event Action<string>? PluginUnloaded;

    /// <summary>Fires when loading a plugin DLL fails.</summary>
    event Action<string, Exception>? PluginLoadFailed;

    /// <summary>
    /// Scans the plugins directory and loads enabled plugins.
    /// </summary>
    /// <param name="initContext">The plugin initialization context.</param>
    /// <param name="enabledPluginIds">Optional set of enabled plugin IDs to load.</param>
    Task LoadAllAsync(IPluginContext initContext, IReadOnlySet<string>? enabledPluginIds = null);

    /// <summary>
    /// Loads a specific plugin by ID on demand.
    /// </summary>
    /// <param name="pluginId">The identifier of the plugin to load.</param>
    /// <param name="initContext">The initialization context.</param>
    /// <returns>The loaded plugin instance, or null if not found.</returns>
    Task<LoadedPlugin?> LoadPluginAsync(string pluginId, IPluginContext initContext);

    /// <summary>
    /// Unloads a loaded plugin and disposes its resources.
    /// </summary>
    /// <param name="pluginId">The identifier of the plugin to unload.</param>
    /// <returns>True if unloaded; otherwise false.</returns>
    bool UnloadPlugin(string pluginId);

    /// <summary>
    /// Asynchronously unloads a loaded plugin and disposes its resources.
    /// </summary>
    /// <param name="pluginId">The identifier of the plugin to unload.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>True if unloaded; otherwise false.</returns>
    Task<bool> UnloadPluginAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts watching the plugins directory for newly added plugins.
    /// </summary>
    /// <param name="initContext">The initialization context.</param>
    void StartWatching(IPluginContext initContext);

    /// <summary>
    /// Returns the set of all plugin IDs found on disk.
    /// </summary>
    /// <returns>A read-only set of plugin IDs.</returns>
    IReadOnlySet<string> GetAllPluginIdsOnDisk();

    /// <summary>
    /// Returns all currently loaded plugins.
    /// </summary>
    /// <returns>A read-only collection of loaded plugins.</returns>
    IReadOnlyCollection<LoadedPlugin> GetLoadedPlugins();

    /// <summary>
    /// Returns all discovered (unloaded) plugins.
    /// </summary>
    /// <returns>A read-only collection of discovered plugins.</returns>
    IReadOnlyCollection<DiscoveredPlugin> GetDiscoveredPlugins();

    /// <summary>
    /// Gets a loaded plugin by its ID.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>The loaded plugin or null if not loaded.</returns>
    LoadedPlugin? GetPlugin(string pluginId);

    /// <summary>
    /// Checks whether a plugin is currently loaded.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>True if loaded, otherwise false.</returns>
    bool IsLoaded(string pluginId);
}
