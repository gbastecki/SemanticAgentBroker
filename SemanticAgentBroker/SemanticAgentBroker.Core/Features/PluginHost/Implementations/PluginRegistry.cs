using Microsoft.Extensions.Logging;
using SemanticAgentBroker.Sdk;
using System.Collections.Concurrent;
namespace SemanticAgentBroker.Core.Features.PluginHost.Implementations;

/// <summary>
/// Central registry of all loaded plugins and their methods.
/// Provides lookup, enumeration, and Semantic Kernel integration.
/// Thread-safe for concurrent access during agent execution.
/// </summary>
public class PluginRegistry : IPluginRegistry
{
    private readonly ConcurrentDictionary<string, LoadedPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<PluginRegistry> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PluginRegistry"/>.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public PluginRegistry(ILogger<PluginRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>Register a loaded plugin into the registry.</summary>
    public void Register(LoadedPlugin loadedPlugin)
    {
        var plugin = loadedPlugin.Plugin;
        if (_plugins.TryAdd(plugin.Id, loadedPlugin))
        {
            _logger.LogInformation("Registered plugin '{PluginId}' with {MethodCount} method(s)",
                plugin.Id, plugin.GetMethods().Count);
        }
        else
        {
            _logger.LogWarning("Plugin '{PluginId}' is already registered", plugin.Id);
        }
    }

    /// <summary>Unregister a plugin by ID. Does NOT dispose - disposal is handled by PluginLoader.</summary>
    public bool Unregister(string pluginId)
    {
        if (_plugins.TryRemove(pluginId, out _))
        {
            _logger.LogInformation("Unregistered plugin '{PluginId}' from registry", pluginId);
            return true;
        }
        return false;
    }

    /// <summary>Get a loaded plugin by its ID.</summary>
    public LoadedPlugin? GetPlugin(string pluginId) =>
        _plugins.TryGetValue(pluginId, out var lp) ? lp : null;

    /// <summary>Get all registered plugins.</summary>
    public IReadOnlyCollection<LoadedPlugin> GetAllPlugins() => _plugins.Values.ToList();

    /// <summary>Get all plugin IDs.</summary>
    public IReadOnlyCollection<string> GetAllPluginIds() => _plugins.Keys.ToList();

    /// <summary>Whether a plugin with the given ID is registered.</summary>
    public bool Contains(string pluginId) => _plugins.ContainsKey(pluginId);

    /// <summary>
    /// Get a specific method from a specific plugin.
    /// </summary>
    public IPluginMethod? GetMethod(string pluginId, string methodId)
    {
        if (!_plugins.TryGetValue(pluginId, out var loaded))
            return null;

        return loaded.Plugin.GetMethods().FirstOrDefault(m => m.Id.Equals(methodId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Get all methods across all plugins, optionally filtered to specific plugin+method pairs.
    /// </summary>
    /// <param name="bindings">If provided, returns only the specified plugin+method combinations.</param>
    public IReadOnlyList<(string PluginId, IPluginMethod Method)> GetMethods(
        IEnumerable<(string PluginId, string MethodId)>? bindings = null)
    {
        if (bindings != null)
        {
            var result = new List<(string, IPluginMethod)>();
            foreach (var (pluginId, methodId) in bindings)
            {
                var method = GetMethod(pluginId, methodId);
                if (method != null)
                    result.Add((pluginId, method));
                else
                    _logger.LogWarning("Method '{PluginId}.{MethodId}' not found in registry", pluginId, methodId);
            }
            return result;
        }

        // Return all methods from all plugins
        return _plugins.Values
            .SelectMany(lp => lp.Plugin.GetMethods().Select(m => (lp.Plugin.Id, m)))
            .ToList();
    }
}
