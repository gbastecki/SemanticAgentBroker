using Microsoft.Extensions.Logging;
using SemanticAgentBroker.Sdk;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
namespace SemanticAgentBroker.Core.Features.PluginHost.Implementations;

/// <summary>
/// Scans plugin subdirectories, loads DLLs in isolated <see cref="AssemblyLoadContext"/>s,
/// discovers <see cref="IAgentPlugin"/> implementations, and initializes them.
/// 
/// Each plugin lives in its own subdirectory under the plugins root:
/// <code>
/// Plugins/
/// ├── BuiltIn.Required/
/// │   ├── SemanticAgentBroker.Plugins.BuiltIn.Required.dll
/// │   └── Docker.DotNet.dll
/// ├── BuiltIn.Optional/
/// │   └── SemanticAgentBroker.Plugins.BuiltIn.Optional.dll
/// ├── MockData/
/// │   └── SemanticAgentBroker.Plugins.MockData.dll
/// └── MyCustomPlugin/
///     └── MyCustomPlugin.dll
/// </code>
/// 
/// Supports:
/// - Subdirectory-based plugin isolation (each plugin gets its own deps)
/// - Loading only enabled plugins (new plugins are discovered but not loaded)
/// - <see cref="FileSystemWatcher"/>-based detection of new plugin folders
/// - Unloading plugins at runtime
/// </summary>
public class PluginLoader : IPluginLoader
{
    private readonly string _pluginsDirectory;
    private readonly ILogger<PluginLoader> _logger;
    private readonly ConcurrentDictionary<string, LoadedPlugin> _loadedPlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DiscoveredPlugin> _discoveredPlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadLocks = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Channel<string>? _watcherChannel;
    private CancellationTokenSource? _watcherCts;

    /// <summary>Fires when a new plugin is successfully loaded.</summary>
    public event Action<LoadedPlugin>? PluginLoaded;

    /// <summary>Fires when a new plugin folder is discovered (not yet loaded).</summary>
    public event Action<DiscoveredPlugin>? PluginDiscovered;

    /// <summary>Fires when a plugin is unloaded.</summary>
    public event Action<string>? PluginUnloaded;

    /// <summary>Fires when a plugin DLL fails to load.</summary>
    public event Action<string, Exception>? PluginLoadFailed;

    /// <summary>
    /// Initializes a new instance of <see cref="PluginLoader"/>.
    /// </summary>
    /// <param name="pluginsDirectory">Directory path where plugin subdirectories reside.</param>
    /// <param name="logger">Logger instance.</param>
    public PluginLoader(string pluginsDirectory, ILogger<PluginLoader> logger)
    {
        _pluginsDirectory = Path.GetFullPath(pluginsDirectory);
        _logger = logger;
    }

    /// <summary>
    /// Scans plugin subdirectories and discovers all plugins.
    /// Only loads plugins whose IDs are in the <paramref name="enabledPluginIds"/> set.
    /// Others are stored as <see cref="DiscoveredPlugin"/> for later activation.
    /// </summary>
    public async Task LoadAllAsync(IPluginContext initContext, IReadOnlySet<string>? enabledPluginIds = null)
    {
        Directory.CreateDirectory(_pluginsDirectory);

        var subdirectories = Directory.GetDirectories(_pluginsDirectory);
        _logger.LogInformation("Plugin loader scanning {Directory}: found {Count} subdirectory(ies)", _pluginsDirectory, subdirectories.Length);

        foreach (var subdir in subdirectories)
        {
            await ScanSubdirectoryAsync(subdir, initContext, enabledPluginIds);
        }
    }

    /// <summary>
    /// Scans a single subdirectory for plugin DLLs.
    /// If the plugin's ID is in the enabled set (or enabledIds is null = first run), it's fully loaded.
    /// Otherwise it's stored as a discovered plugin.
    /// </summary>
    private async Task ScanSubdirectoryAsync(string subdir, IPluginContext initContext, IReadOnlySet<string>? enabledPluginIds)
    {
        var folderName = Path.GetFileName(subdir);
        string[] dllFiles;
        try
        {
            dllFiles = Directory.GetFiles(subdir, "*.dll", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not access or enumerate files in {Folder}", folderName);
            return;
        }

        foreach (var dllPath in dllFiles)
        {
            var fileName = Path.GetFileName(dllPath);

            // Skip known non-plugin DLLs (dependencies)
            if (fileName.StartsWith("System.") || fileName.StartsWith("Microsoft.") ||
                fileName.StartsWith("SemanticAgentBroker.Sdk") || fileName.StartsWith("Newtonsoft."))
                continue;

            try
            {
                var dllHash = ComputeFileHash(dllPath);

                var probeContext = new PluginAssemblyLoadContext(dllPath);
                Assembly probeAssembly;
                try
                {
                    probeAssembly = probeContext.LoadFromAssemblyPath(dllPath);
                }
                catch
                {
                    probeContext.Unload();
                    continue;
                }

                List<Type> pluginTypes;
                try
                {
                    pluginTypes = probeAssembly.GetTypes()
                        .Where(t => typeof(IAgentPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load types from assembly '{FileName}' in {Folder}/", fileName, folderName);
                    probeContext.Unload();
                    continue;
                }

                if (pluginTypes.Count == 0)
                {
                    probeContext.Unload();
                    continue;
                }

                bool anyLoadedFromDll = false;
                foreach (var pluginType in pluginTypes)
                {
                    IAgentPlugin? probeInstance = null;
                    try
                    {
                        probeInstance = (IAgentPlugin)Activator.CreateInstance(pluginType)!;
                        var pluginId = probeInstance.Id;

                        // Skip if already loaded
                        if (_loadedPlugins.ContainsKey(pluginId) || _discoveredPlugins.ContainsKey(pluginId))
                        {
                            probeInstance.Dispose();
                            probeInstance = null;
                            continue;
                        }

                        bool shouldLoad = enabledPluginIds == null || enabledPluginIds.Contains(pluginId)
                            || probeInstance.Category == PluginCategory.BuiltInRequired;

                        if (shouldLoad)
                        {
                            // Initialize and load fully
                            await probeInstance.InitializeAsync(initContext);

                            var loaded = new LoadedPlugin(probeInstance, probeAssembly, dllPath, dllHash, folderName, probeContext);
                            if (_loadedPlugins.TryAdd(pluginId, loaded))
                            {
                                anyLoadedFromDll = true;
                                _logger.LogInformation(
                                    "Loaded plugin '{PluginId}' v{Version} ({DisplayName}) from {Folder}/ — {MethodCount} method(s)",
                                    pluginId, probeInstance.Version, probeInstance.DisplayName,
                                    folderName, probeInstance.GetMethods().Count);

                                try
                                {
                                    PluginLoaded?.Invoke(loaded);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "PluginLoaded subscriber failed for '{PluginId}'", pluginId);
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Plugin '{PluginId}' is already loaded. Disposing duplicate instance.", pluginId);
                                loaded.UnloadContextOnDispose = false;
                                loaded.Dispose();
                            }
                        }
                        else
                        {
                            // Store as discovered (not loaded)
                            var discovered = new DiscoveredPlugin
                            {
                                DllPath = dllPath,
                                DllHash = dllHash,
                                FolderName = folderName,
                                PluginId = pluginId,
                                DisplayName = probeInstance.DisplayName,
                                Description = probeInstance.Description,
                                Version = probeInstance.Version,
                                Author = probeInstance.Author,
                                Icon = probeInstance.Icon,
                                Tags = probeInstance.Tags.ToList(),
                                Category = probeInstance.Category,
                                RequiredPlugins = probeInstance.RequiredPlugins.ToList(),
                                TasksJson = JsonSerializer.Serialize(probeInstance.GetTasks()),
                                MethodManifestJson = GenerateMethodManifest(probeInstance)
                            };

                            probeInstance.Dispose();
                            probeInstance = null;

                            if (_discoveredPlugins.TryAdd(pluginId, discovered))
                            {
                                _logger.LogInformation(
                                    "Discovered plugin '{PluginId}' v{Version} in {Folder}/ — not enabled, skipping load",
                                    pluginId, discovered.Version, folderName);

                                PluginDiscovered?.Invoke(discovered);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        probeInstance?.Dispose();
                        _logger.LogError(ex, "Failed to probe plugin type '{TypeName}' from {FileName}", pluginType.FullName, fileName);
                        PluginLoadFailed?.Invoke(fileName, ex);
                    }
                }

                if (!anyLoadedFromDll)
                {
                    probeContext.Unload();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to scan DLL '{FileName}' in {Folder}/", fileName, folderName);
            }
        }
    }

    /// <summary>
    /// Loads a specific plugin by ID from the discovered set.
    /// Called when user enables a previously-discovered plugin.
    /// </summary>
    public async Task<LoadedPlugin?> LoadPluginAsync(string pluginId, IPluginContext initContext)
    {
        if (_loadedPlugins.TryGetValue(pluginId, out var alreadyLoaded))
        {
            _logger.LogWarning("Plugin '{PluginId}' is already loaded", pluginId);
            return alreadyLoaded;
        }

        var loadLock = _loadLocks.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));
        await loadLock.WaitAsync();
        try
        {
            if (_loadedPlugins.TryGetValue(pluginId, out var existing))
            {
                return existing;
            }

            // Try from discovered set first
            if (_discoveredPlugins.TryRemove(pluginId, out var discovered))
            {
                var loaded = await TryLoadFromPathAsync(discovered.DllPath, discovered.FolderName, pluginId, initContext);
                if (loaded == null)
                {
                    _discoveredPlugins.TryAdd(pluginId, discovered);
                }
                return loaded;
            }

            // Otherwise scan all subdirectories to find it
            var subdirectories = Directory.GetDirectories(_pluginsDirectory);
            foreach (var subdir in subdirectories)
            {
                var dllFiles = Directory.GetFiles(subdir, "*.dll", SearchOption.TopDirectoryOnly);
                foreach (var dllPath in dllFiles)
                {
                    var fileName = Path.GetFileName(dllPath);

                    // Skip known non-plugin DLLs (dependencies)
                    if (fileName.StartsWith("System.") || fileName.StartsWith("Microsoft.") ||
                        fileName.StartsWith("SemanticAgentBroker.Sdk") || fileName.StartsWith("Newtonsoft."))
                        continue;

                    var result = await TryLoadFromPathAsync(dllPath, Path.GetFileName(subdir), pluginId, initContext);
                    if (result != null) return result;
                }
            }

            _logger.LogWarning("Plugin '{PluginId}' not found in any subdirectory", pluginId);
            return null;
        }
        finally
        {
            loadLock.Release();
        }
    }

    private async Task<LoadedPlugin?> TryLoadFromPathAsync(string dllPath, string folderName, string targetPluginId, IPluginContext initContext)
    {
        AssemblyLoadContext? loadContext = null;
        bool keepLoaded = false;
        bool isReusedContext = false;
        IAgentPlugin? instance = null;
        try
        {
            Assembly assembly;
            var existingPluginWithSameDll = _loadedPlugins.Values
                .FirstOrDefault(lp => string.Equals(lp.DllPath, dllPath, StringComparison.OrdinalIgnoreCase));

            if (existingPluginWithSameDll != null)
            {
                loadContext = existingPluginWithSameDll.LoadContext;
                assembly = existingPluginWithSameDll.Assembly;
                isReusedContext = true;
            }
            else
            {
                loadContext = new PluginAssemblyLoadContext(dllPath);
                assembly = loadContext.LoadFromAssemblyPath(dllPath);
            }

            var pluginTypes = assembly.GetTypes()
                .Where(t => typeof(IAgentPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                .ToList();

            foreach (var pluginType in pluginTypes)
            {
                instance = (IAgentPlugin)Activator.CreateInstance(pluginType)!;
                if (!instance.Id.Equals(targetPluginId, StringComparison.OrdinalIgnoreCase))
                {
                    instance.Dispose();
                    instance = null;
                    continue;
                }

                await instance.InitializeAsync(initContext);
                var dllHash = ComputeFileHash(dllPath);
                var loaded = new LoadedPlugin(instance, assembly, dllPath, dllHash, folderName, loadContext);

                if (_loadedPlugins.TryAdd(instance.Id, loaded))
                {
                    keepLoaded = true;
                    _logger.LogInformation("Loaded plugin '{PluginId}' v{Version} on demand", instance.Id, instance.Version);
                    try
                    {
                        PluginLoaded?.Invoke(loaded);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "PluginLoaded subscriber failed for '{PluginId}'", instance.Id);
                    }
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugin '{PluginId}' from {DllPath}", targetPluginId, dllPath);
            try
            {
                PluginLoadFailed?.Invoke(Path.GetFileName(dllPath), ex);
            }
            catch (Exception eventEx)
            {
                _logger.LogError(eventEx, "PluginLoadFailed subscriber failed for '{DllPath}'", dllPath);
            }
        }
        finally
        {
            if (!keepLoaded)
            {
                instance?.Dispose();
                if (loadContext != null && !isReusedContext)
                {
                    loadContext.Unload();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Asynchronously unloads a plugin by ID. Disposes the plugin and unloads the AssemblyLoadContext.
    /// </summary>
    public async Task<bool> UnloadPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var loadLock = _loadLocks.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));
        await loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_loadedPlugins.TryRemove(pluginId, out var loaded))
            {
                _logger.LogInformation("Unloading plugin '{PluginId}'", pluginId);

                // If any other currently loaded plugin still shares this LoadContext, do not unload the ALC
                bool isContextStillInUse = _loadedPlugins.Values.Any(p => ReferenceEquals(p.LoadContext, loaded.LoadContext));
                if (isContextStillInUse)
                {
                    loaded.UnloadContextOnDispose = false;
                }

                loaded.Dispose();
                try
                {
                    PluginUnloaded?.Invoke(pluginId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PluginUnloaded subscriber failed for '{PluginId}'", pluginId);
                }
                return true;
            }
            return false;
        }
        finally
        {
            loadLock.Release();
        }
    }

    /// <summary>
    /// Unloads a plugin by ID. Disposes the plugin and unloads the AssemblyLoadContext.
    /// </summary>
    public bool UnloadPlugin(string pluginId)
    {
        return Task.Run(() => UnloadPluginAsync(pluginId)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Starts watching the plugins directory for new subdirectories.
    /// New directories trigger a scan for undiscovered plugins.
    /// </summary>
    public void StartWatching(IPluginContext initContext)
    {
        if (_watcher != null) return;

        Directory.CreateDirectory(_pluginsDirectory);
        _watcherCts = new CancellationTokenSource();
        _watcherChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var token = _watcherCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var reader = _watcherChannel.Reader;
                while (await reader.WaitToReadAsync(token))
                {
                    var pathsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while (reader.TryRead(out var path))
                    {
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            pathsToScan.Add(path);
                        }
                    }

                    if (pathsToScan.Count == 0) continue;

                    // Single debounce delay for the batch of file system events
                    await Task.Delay(1000, token);

                    // Drain any additional events that arrived during the debounce delay
                    while (reader.TryRead(out var latePath))
                    {
                        if (!string.IsNullOrWhiteSpace(latePath))
                        {
                            pathsToScan.Add(latePath);
                        }
                    }

                    foreach (var path in pathsToScan)
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            if (Directory.Exists(path))
                            {
                                await ScanSubdirectoryAsync(path, initContext, new HashSet<string>());
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to scan subdirectory {Path} from FileSystemWatcher", path);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watcher consumer loop error");
            }
        }, token);

        _watcher = new FileSystemWatcher(_pluginsDirectory)
        {
            NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        _watcher.Created += (s, e) =>
        {
            var dirToScan = Directory.Exists(e.FullPath) ? e.FullPath : Path.GetDirectoryName(e.FullPath);
            if (!string.IsNullOrEmpty(dirToScan))
            {
                _logger.LogInformation("New plugin file or directory detected: {Path}", e.FullPath);
                _watcherChannel?.Writer.TryWrite(dirToScan);
            }
        };

        _watcher.Changed += (s, e) =>
        {
            var dirToScan = Directory.Exists(e.FullPath) ? e.FullPath : Path.GetDirectoryName(e.FullPath);
            if (!string.IsNullOrEmpty(dirToScan))
            {
                _logger.LogInformation("Plugin file or directory modification detected: {Path}", e.FullPath);
                _watcherChannel?.Writer.TryWrite(dirToScan);
            }
        };
    }

    /// <summary>
    /// Returns the set of plugin IDs that exist on disk (in any subdirectory).
    /// Used for DB lifecycle sync to detect missing plugins.
    /// </summary>
    public IReadOnlySet<string> GetAllPluginIdsOnDisk()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lp in _loadedPlugins.Values)
            ids.Add(lp.Plugin.Id);
        foreach (var dp in _discoveredPlugins.Values)
            ids.Add(dp.PluginId);
        return ids;
    }

    /// <summary>Returns all currently loaded plugins.</summary>
    public IReadOnlyCollection<LoadedPlugin> GetLoadedPlugins() => _loadedPlugins.Values.ToList();

    /// <summary>Returns all discovered (but not loaded) plugins.</summary>
    public IReadOnlyCollection<DiscoveredPlugin> GetDiscoveredPlugins() => _discoveredPlugins.Values.ToList();

    /// <summary>Returns a loaded plugin by its ID, or null.</summary>
    public LoadedPlugin? GetPlugin(string pluginId) =>
        _loadedPlugins.TryGetValue(pluginId, out var lp) ? lp : null;

    /// <summary>Whether a plugin with the given ID is currently loaded.</summary>
    public bool IsLoaded(string pluginId) => _loadedPlugins.ContainsKey(pluginId);

    /// <summary>
    /// Generates a JSON method manifest for a plugin (used for DB persistence / UI preview).
    /// </summary>
    public static string GenerateMethodManifest(IAgentPlugin plugin)
    {
        var methods = plugin.GetMethods().Select(m => new
        {
            m.Id,
            m.DisplayName,
            m.Description,
            m.IsDangerous,
            m.ReturnDescription,
            Parameters = m.Parameters.Select(p => new
            {
                p.Name,
                p.Description,
                Type = p.ParameterType.Name,
                p.IsRequired,
                DefaultValue = p.DefaultValue?.ToString()
            }).ToList()
        }).ToList();

        return JsonSerializer.Serialize(methods, new JsonSerializerOptions { WriteIndented = false });
    }

    internal static string ComputeFileHash(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Disposes file watchers and unloads all active plugins.
    /// </summary>
    public void Dispose()
    {
        _watcher?.Dispose();
        _watcherCts?.Cancel();
        _watcherChannel?.Writer.TryComplete();
        _watcherCts?.Dispose();

        foreach (var loaded in _loadedPlugins.Values)
        {
            loaded.Dispose();
        }
        _loadedPlugins.Clear();
        _discoveredPlugins.Clear();

        foreach (var lockSem in _loadLocks.Values)
        {
            lockSem.Dispose();
        }
        _loadLocks.Clear();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Isolated assembly load context that resolves plugin-specific dependencies from 
/// the plugin's directory. For shared assemblies (like the SDK), it returns null
/// which causes the runtime to fall back to the default context — ensuring
/// type identity (e.g., <c>typeof(IAgentPlugin)</c>) is consistent across host and plugin.
/// 
/// IMPORTANT: The SDK DLL must NOT be in the plugin's directory. If it is, this 
/// context will load a separate copy, creating a type identity split where 
/// <c>typeof(IAgentPlugin)</c> from the host != <c>typeof(IAgentPlugin)</c> from the plugin.
/// </summary>
internal class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "SemanticAgentBroker.Sdk",
        "SemanticAgentBroker.Shared",
        "Microsoft.SemanticKernel.Abstractions",
        "Microsoft.SemanticKernel.Core"
    };

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name != null && SharedAssemblies.Contains(assemblyName.Name))
        {
            // Force fallback to default context to prevent type identity split
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        // Return null for assemblies not in the plugin directory —
        // the runtime then probes the default context (host), keeping type identity.
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
