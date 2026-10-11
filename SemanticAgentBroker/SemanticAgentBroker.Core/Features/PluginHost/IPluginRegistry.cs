using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.Core.Features.PluginHost;

/// <summary>
/// Central registry of all loaded plugins and their methods.
/// Provides lookup, enumeration, and Semantic Kernel integration.
/// </summary>
public interface IPluginRegistry
{
    /// <summary>Registers a loaded plugin.</summary>
    /// <param name="loadedPlugin">The loaded plugin instance.</param>
    void Register(LoadedPlugin loadedPlugin);

    /// <summary>Unregisters a plugin by its ID.</summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>True if successfully removed; otherwise false.</returns>
    bool Unregister(string pluginId);

    /// <summary>Gets a loaded plugin by its ID.</summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>The loaded plugin or null if not found.</returns>
    LoadedPlugin? GetPlugin(string pluginId);

    /// <summary>Returns all currently registered plugins.</summary>
    /// <returns>A read-only collection of loaded plugins.</returns>
    IReadOnlyCollection<LoadedPlugin> GetAllPlugins();

    /// <summary>Returns all registered plugin IDs.</summary>
    /// <returns>A read-only collection of plugin IDs.</returns>
    IReadOnlyCollection<string> GetAllPluginIds();

    /// <summary>Checks whether a plugin is registered.</summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>True if registered; otherwise false.</returns>
    bool Contains(string pluginId);

    /// <summary>Gets a specific method from a plugin.</summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <param name="methodId">The method ID.</param>
    /// <returns>The plugin method, or null if not found.</returns>
    IPluginMethod? GetMethod(string pluginId, string methodId);

    /// <summary>
    /// Resolves and returns a list of plugin methods based on bindings, or all methods if bindings is null.
    /// </summary>
    /// <param name="bindings">Optional list of (PluginId, MethodId) tuples.</param>
    /// <returns>A list of resolved (PluginId, Method) pairs.</returns>
    IReadOnlyList<(string PluginId, IPluginMethod Method)> GetMethods(IEnumerable<(string PluginId, string MethodId)>? bindings = null);
}
