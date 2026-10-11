using SemanticAgentBroker.Sdk;
using System.Reflection;
using System.Runtime.Loader;

namespace SemanticAgentBroker.Core.Features.PluginHost;

/// <summary>
/// Represents a loaded plugin DLL with its assembly load context for isolation.
/// </summary>
public sealed class LoadedPlugin : IDisposable
{
    /// <summary>The plugin instance.</summary>
    public IAgentPlugin Plugin { get; }

    /// <summary>The loaded assembly.</summary>
    public Assembly Assembly { get; }

    /// <summary>The absolute path to the plugin DLL file.</summary>
    public string DllPath { get; }

    /// <summary>SHA-256 hash of the plugin DLL file.</summary>
    public string DllHash { get; }

    /// <summary>Folder name of the plugin subdirectory.</summary>
    public string FolderName { get; }

    /// <summary>The isolated assembly load context for this plugin.</summary>
    public AssemblyLoadContext LoadContext { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="LoadedPlugin"/>.
    /// </summary>
    /// <param name="plugin">The plugin instance.</param>
    /// <param name="assembly">The loaded assembly.</param>
    /// <param name="dllPath">The absolute path to the plugin DLL.</param>
    /// <param name="dllHash">The file hash.</param>
    /// <param name="folderName">The folder name.</param>
    /// <param name="loadContext">The assembly load context.</param>
    public LoadedPlugin(IAgentPlugin plugin, Assembly assembly, string dllPath, string dllHash, string folderName, AssemblyLoadContext loadContext)
    {
        Plugin = plugin;
        Assembly = assembly;
        DllPath = dllPath;
        DllHash = dllHash;
        FolderName = folderName;
        LoadContext = loadContext;
    }

    private bool _isDisposed;

    /// <summary>Whether to unload the AssemblyLoadContext when disposed. Defaults to true.</summary>
    public bool UnloadContextOnDispose { get; set; } = true;

    /// <summary>
    /// Disposes the underlying plugin instance and unloads the AssemblyLoadContext.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Plugin.Dispose();
        if (UnloadContextOnDispose && LoadContext.IsCollectible)
        {
            try
            {
                LoadContext.Unload();
            }
            catch (InvalidOperationException) { }
        }
    }
}

/// <summary>
/// Discovered but not-yet-loaded plugin placeholder.
/// Contains metadata extracted from a quick assembly scan without full initialization.
/// </summary>
public sealed class DiscoveredPlugin
{
    /// <summary>Path to the plugin DLL.</summary>
    public string DllPath { get; init; } = "";

    /// <summary>SHA-256 hash of the DLL file.</summary>
    public string DllHash { get; init; } = "";

    /// <summary>Plugin subdirectory name.</summary>
    public string FolderName { get; init; } = "";

    /// <summary>Unique plugin identifier.</summary>
    public string PluginId { get; init; } = "";

    /// <summary>Human-readable display name of the plugin.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Description of what the plugin does.</summary>
    public string Description { get; init; } = "";

    /// <summary>Semantic version string of the plugin.</summary>
    public string Version { get; init; } = "";

    /// <summary>Author or maintainer of the plugin.</summary>
    public string Author { get; init; } = "";

    /// <summary>UI icon identifier for the plugin.</summary>
    public string Icon { get; init; } = "extension";

    /// <summary>Search and categorization tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Plugin classification category.</summary>
    public PluginCategory Category { get; init; } = PluginCategory.Custom;

    /// <summary>Serialized JSON array containing method signatures and parameter descriptions.</summary>
    public string MethodManifestJson { get; init; } = "[]";

    /// <summary>List of required prerequisite plugin IDs.</summary>
    public IReadOnlyList<string> RequiredPlugins { get; init; } = [];

    /// <summary>Serialized JSON array containing predefined task definitions.</summary>
    public string TasksJson { get; init; } = "[]";
}
