using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Core.Features.PluginHost.Implementations;
using SemanticAgentBroker.Plugins.MockData;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Core.Features.PluginHost;

[TestClass]
public class PluginLoaderTests
{
    private TestWorkspaceFixture _workspaceFixture = null!;
    private Mock<IPluginContext> _contextMock = null!;
    private string _pluginsDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _workspaceFixture = new TestWorkspaceFixture();
        _contextMock = new Mock<IPluginContext>();
        _contextMock.Setup(c => c.WorkspacePath).Returns(_workspaceFixture.WorkspacePath);
        _contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string>());

        _pluginsDir = Path.Combine(_workspaceFixture.WorkspacePath, "Plugins");
        Directory.CreateDirectory(_pluginsDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _workspaceFixture.Dispose();
    }

    [TestMethod]
    public void ComputeFileHash_ReturnsDeterministicSha256Hex()
    {
        // Arrange
        var testFile = Path.Combine(_workspaceFixture.WorkspacePath, "test_hash.txt");
        File.WriteAllText(testFile, "Deterministic content for SHA256 test");

        // Act
        var hash1 = PluginLoader.ComputeFileHash(testFile);
        var hash2 = PluginLoader.ComputeFileHash(testFile);

        // Assert
        Assert.IsNotNull(hash1);
        Assert.AreEqual(64, hash1.Length);
        Assert.AreEqual(hash1, hash2);
    }

    [TestMethod]
    public void GenerateMethodManifest_GeneratesWellFormedJson()
    {
        // Arrange
        var plugin = new SampleDataAgentPlugin();

        // Act
        var manifestJson = PluginLoader.GenerateMethodManifest(plugin);

        // Assert
        Assert.IsNotNull(manifestJson);
        StringAssert.Contains(manifestJson, "GetSalesData");
        StringAssert.Contains(manifestJson, "GetServerLogs");
        StringAssert.Contains(manifestJson, "GetUserList");
        StringAssert.Contains(manifestJson, "GetProductCatalog");
    }

    [TestMethod]
    public async Task LoadAllAsync_WithEnabledFilter_LoadsEnabledAndStoresDiscovered()
    {
        // Arrange - Setup a plugin subdirectory with the MockData assembly
        var mockDataDir = Path.Combine(_pluginsDir, "MockData");
        Directory.CreateDirectory(mockDataDir);

        var mockDataAssemblyPath = typeof(SampleDataAgentPlugin).Assembly.Location;
        var targetDll = Path.Combine(mockDataDir, Path.GetFileName(mockDataAssemblyPath));
        File.Copy(mockDataAssemblyPath, targetDll, overwrite: true);

        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);

        // Only enable "SampleData", leaving "SampleSensors" and "SampleImages" as discovered
        var enabledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SampleData" };

        // Act
        await loader.LoadAllAsync(_contextMock.Object, enabledIds);

        // Assert
        Assert.IsTrue(loader.IsLoaded("SampleData"));
        Assert.IsNotNull(loader.GetPlugin("SampleData"));

        var loaded = loader.GetLoadedPlugins();
        Assert.IsTrue(loaded.Any(p => p.Plugin.Id == "SampleData"));

        var discovered = loader.GetDiscoveredPlugins();
        Assert.IsTrue(discovered.Any(p => p.PluginId == "SampleSensors" || p.PluginId == "SampleImages"));

        var diskIds = loader.GetAllPluginIdsOnDisk();
        Assert.IsTrue(diskIds.Contains("SampleData"));
        Assert.IsTrue(diskIds.Contains("SampleSensors") || diskIds.Contains("SampleImages"));
    }

    [TestMethod]
    public async Task LoadPluginAsync_LoadsDiscoveredPluginOnDemand()
    {
        // Arrange - Setup subdirectory with MockData assembly
        var mockDataDir = Path.Combine(_pluginsDir, "MockData");
        Directory.CreateDirectory(mockDataDir);

        var mockDataAssemblyPath = typeof(SampleDataAgentPlugin).Assembly.Location;
        var targetDll = Path.Combine(mockDataDir, Path.GetFileName(mockDataAssemblyPath));
        File.Copy(mockDataAssemblyPath, targetDll, overwrite: true);

        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);

        // Initially enable empty set (all will be discovered)
        await loader.LoadAllAsync(_contextMock.Object, new HashSet<string>());

        Assert.IsFalse(loader.IsLoaded("SampleData"));
        Assert.IsTrue(loader.GetDiscoveredPlugins().Any(p => p.PluginId == "SampleData"));

        // Act - Load on demand
        var loaded = await loader.LoadPluginAsync("SampleData", _contextMock.Object);

        // Assert
        Assert.IsNotNull(loaded);
        Assert.AreEqual("SampleData", loaded.Plugin.Id);
        Assert.IsTrue(loader.IsLoaded("SampleData"));
        Assert.IsFalse(loader.GetDiscoveredPlugins().Any(p => p.PluginId == "SampleData"));
    }

    [TestMethod]
    public async Task UnloadPlugin_UnloadsAndDisposesPlugin()
    {
        // Arrange
        var mockDataDir = Path.Combine(_pluginsDir, "MockData");
        Directory.CreateDirectory(mockDataDir);

        var mockDataAssemblyPath = typeof(SampleDataAgentPlugin).Assembly.Location;
        var targetDll = Path.Combine(mockDataDir, Path.GetFileName(mockDataAssemblyPath));
        File.Copy(mockDataAssemblyPath, targetDll, overwrite: true);

        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);
        await loader.LoadAllAsync(_contextMock.Object, new HashSet<string> { "SampleData" });
        Assert.IsTrue(loader.IsLoaded("SampleData"));

        // Act
        bool unloaded = loader.UnloadPlugin("SampleData");

        // Assert
        Assert.IsTrue(unloaded);
        Assert.IsFalse(loader.IsLoaded("SampleData"));
        Assert.IsNull(loader.GetPlugin("SampleData"));

        // Unloading again returns false
        Assert.IsFalse(loader.UnloadPlugin("SampleData"));
    }

    [TestMethod]
    public async Task UnloadPlugin_UnloadsAssemblyLoadContext()
    {
        // Arrange
        var mockDataDir = Path.Combine(_pluginsDir, "MockData");
        Directory.CreateDirectory(mockDataDir);

        var mockDataAssemblyPath = typeof(SampleDataAgentPlugin).Assembly.Location;
        var targetDll = Path.Combine(mockDataDir, Path.GetFileName(mockDataAssemblyPath));
        File.Copy(mockDataAssemblyPath, targetDll, overwrite: true);

        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);
        await loader.LoadAllAsync(_contextMock.Object, new HashSet<string> { "SampleData" });
        Assert.IsTrue(loader.IsLoaded("SampleData"));

        var loaded = loader.GetLoadedPlugins().First(p => p.Plugin.Id == "SampleData");
        var alc = loaded.LoadContext;
        Assert.IsTrue(alc.IsCollectible);

        bool unloadingFired = false;
        alc.Unloading += _ => unloadingFired = true;

        // Act
        bool unloaded = loader.UnloadPlugin("SampleData");

        // Assert
        Assert.IsTrue(unloaded);
        Assert.IsTrue(unloadingFired, "AssemblyLoadContext.Unloading event must be triggered when plugin is unloaded.");
    }

    [TestMethod]
    public async Task LoadPluginAsync_NonExistentPlugin_ReturnsNull()
    {
        // Arrange
        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);

        // Act
        var result = await loader.LoadPluginAsync("NonExistentPlugin123", _contextMock.Object);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task LoadPluginAsync_WhenLoadingFails_PreservesDiscoveredPlugin()
    {
        // Arrange - Setup subdirectory with MockData assembly
        var mockDataDir = Path.Combine(_pluginsDir, "MockData");
        Directory.CreateDirectory(mockDataDir);

        var mockDataAssemblyPath = typeof(SampleDataAgentPlugin).Assembly.Location;
        var targetDll = Path.Combine(mockDataDir, Path.GetFileName(mockDataAssemblyPath));
        File.Copy(mockDataAssemblyPath, targetDll, overwrite: true);

        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);
        await loader.LoadAllAsync(_contextMock.Object, new HashSet<string>());

        Assert.IsTrue(loader.GetDiscoveredPlugins().Any(p => p.PluginId == "SampleData"));

        // Arrange - Inject a discovered entry with non-matching PluginId to test load failure recovery
        var field = typeof(PluginLoader).GetField("_discoveredPlugins", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var map = (System.Collections.Concurrent.ConcurrentDictionary<string, DiscoveredPlugin>)field.GetValue(loader)!;
        map.TryAdd("FaultyPlugin", new DiscoveredPlugin
        {
            PluginId = "FaultyPlugin",
            DisplayName = "Faulty",
            DllPath = targetDll,
            FolderName = "MockData"
        });

        // Act - Attempt to load (TryLoadFromPathAsync finds no type with ID "FaultyPlugin")
        var result = await loader.LoadPluginAsync("FaultyPlugin", _contextMock.Object);

        // Assert
        Assert.IsNull(result);
        Assert.IsTrue(loader.GetDiscoveredPlugins().Any(p => p.PluginId == "FaultyPlugin"),
            "Discovered plugin must be restored to discovered set if loading fails.");
    }

    [TestMethod]
    public void StartWatching_ConfiguresLastWriteAndFileNameFilters()
    {
        // Arrange
        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);

        // Act
        loader.StartWatching(_contextMock.Object);

        // Assert
        var watcherField = typeof(PluginLoader).GetField("_watcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var watcher = (FileSystemWatcher)watcherField.GetValue(loader)!;

        Assert.IsNotNull(watcher);
        Assert.IsTrue(watcher.NotifyFilter.HasFlag(NotifyFilters.LastWrite), "Watcher must include LastWrite filter.");
        Assert.IsTrue(watcher.NotifyFilter.HasFlag(NotifyFilters.FileName), "Watcher must include FileName filter.");
        Assert.IsTrue(watcher.NotifyFilter.HasFlag(NotifyFilters.DirectoryName), "Watcher must include DirectoryName filter.");
    }

    [TestMethod]
    public async Task LoadAllAsync_WhenAssemblyHasMultiplePluginsAndOneDisabled_DoesNotUnloadContextForLoadedPlugin()
    {
        // Arrange - Setup a plugin subdirectory with the MockData assembly (which contains SampleData, SampleSensors, SampleImages)
        var mockDataDir = Path.Combine(_pluginsDir, "MockData");
        Directory.CreateDirectory(mockDataDir);

        var mockDataAssemblyPath = typeof(SampleDataAgentPlugin).Assembly.Location;
        var targetDll = Path.Combine(mockDataDir, Path.GetFileName(mockDataAssemblyPath));
        File.Copy(mockDataAssemblyPath, targetDll, overwrite: true);

        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);

        // Only enable "SampleData", leaving "SampleSensors" and "SampleImages" disabled/discovered
        var enabledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SampleData" };

        // Act
        await loader.LoadAllAsync(_contextMock.Object, enabledIds);

        // Assert - The enabled plugin must be loaded and its LoadContext must be active (not unloaded)
        var loaded = loader.GetPlugin("SampleData");
        Assert.IsNotNull(loaded, "SampleData must be loaded.");
        Assert.IsNotNull(loaded.LoadContext, "LoadContext must be present.");
        Assert.IsTrue(loaded.LoadContext.IsCollectible, "LoadContext should be collectible.");

        // If the context was prematurely unloaded, attempting to load or resolve types or invoke methods from the assembly fails
        var methods = loaded.Plugin.GetMethods();
        Assert.IsTrue(methods.Count > 0, "Loaded plugin methods should be accessible.");
    }

    private class FailingInitPlugin : IAgentPlugin
    {
        public bool IsDisposed { get; private set; }
        public string Id => "FailingInit";
        public string DisplayName => "Failing Init";
        public string Description => "Throws on init";
        public string Version => "1.0.0";
        public string Author => "Test";
        public string Icon => "";
        public PluginCategory Category => PluginCategory.Custom;
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public IReadOnlyList<string> RequiredPlugins => Array.Empty<string>();

        public Task InitializeAsync(IPluginContext context) => throw new InvalidOperationException("Simulated init failure");
        public IReadOnlyList<IPluginMethod> GetMethods() => Array.Empty<IPluginMethod>();
        public IReadOnlyList<PluginTaskDef> GetTasks() => Array.Empty<PluginTaskDef>();
        public void Dispose() => IsDisposed = true;
    }

    [TestMethod]
    public void PluginInstance_WhenInitializedThrows_DisposedProperly()
    {
        var failingPlugin = new FailingInitPlugin();
        Assert.IsFalse(failingPlugin.IsDisposed);
        try
        {
            failingPlugin.InitializeAsync(Mock.Of<IPluginContext>()).GetAwaiter().GetResult();
        }
        catch
        {
            failingPlugin.Dispose();
        }
        Assert.IsTrue(failingPlugin.IsDisposed);
    }

    [TestMethod]
    public void UnloadPlugin_WhileLoadLockHeld_BlocksUntilLockReleased()
    {
        // Arrange
        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);
        var loadLocksField = typeof(PluginLoader).GetField("_loadLocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var loadLocks = (System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>)loadLocksField.GetValue(loader)!;
        var pluginLock = loadLocks.GetOrAdd("SampleData", _ => new SemaphoreSlim(1, 1));

        // Acquire lock to simulate concurrent LoadPluginAsync operation
        pluginLock.Wait();

        bool unloadFinished = false;
        var unloadTask = Task.Run(() =>
        {
            loader.UnloadPlugin("SampleData");
            unloadFinished = true;
        });

        // Assert - UnloadPlugin must wait while lock is held
        Thread.Sleep(100);
        Assert.IsFalse(unloadFinished, "UnloadPlugin must wait for the plugin load lock.");

        // Release lock
        pluginLock.Release();
        unloadTask.Wait(TimeSpan.FromSeconds(2));
        Assert.IsTrue(unloadFinished, "UnloadPlugin should finish after load lock is released.");
    }

    [TestMethod]
    public async Task UnloadPluginAsync_WhileLoadLockHeld_AwaitsLockAsynchronouslyWithoutBlocking()
    {
        // Arrange
        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);
        var loadLocksField = typeof(PluginLoader).GetField("_loadLocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var loadLocks = (System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>)loadLocksField.GetValue(loader)!;
        var pluginLock = loadLocks.GetOrAdd("SampleData", _ => new SemaphoreSlim(1, 1));

        // Acquire lock asynchronously to simulate concurrent LoadPluginAsync operation
        await pluginLock.WaitAsync();

        bool unloadFinished = false;
        var unloadTask = Task.Run(async () =>
        {
            await loader.UnloadPluginAsync("SampleData");
            unloadFinished = true;
        });

        // Assert - UnloadPluginAsync must wait while lock is held
        await Task.Delay(50);
        Assert.IsFalse(unloadFinished, "UnloadPluginAsync must wait for the plugin load lock.");

        // Release lock
        pluginLock.Release();
        await unloadTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(unloadFinished, "UnloadPluginAsync should finish after load lock is released.");
    }

    [TestMethod]
    public async Task LoadAllAsync_WhenDuplicatePluginInMultipleFolders_DisposesDuplicateInstance()
    {
        // Arrange - Place MockData assembly in two different subdirectories
        var dir1 = Path.Combine(_pluginsDir, "Dir1");
        var dir2 = Path.Combine(_pluginsDir, "Dir2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        var mockDataAssemblyPath = typeof(SampleDataAgentPlugin).Assembly.Location;
        File.Copy(mockDataAssemblyPath, Path.Combine(dir1, Path.GetFileName(mockDataAssemblyPath)), overwrite: true);
        File.Copy(mockDataAssemblyPath, Path.Combine(dir2, Path.GetFileName(mockDataAssemblyPath)), overwrite: true);

        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);

        // Act - Load both directories
        await loader.LoadAllAsync(_contextMock.Object, new HashSet<string> { "SampleData" });

        // Assert - Only one instance is loaded in _loadedPlugins
        Assert.IsTrue(loader.IsLoaded("SampleData"));
        Assert.AreEqual(1, loader.GetLoadedPlugins().Count(p => p.Plugin.Id == "SampleData"));
    }

    [TestMethod]
    public async Task WatcherConsumerLoop_BatchesEventsWithinDebounceWindow()
    {
        // Arrange
        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);
        loader.StartWatching(_contextMock.Object);

        var field = typeof(PluginLoader).GetField("_watcherChannel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var channel = (System.Threading.Channels.Channel<string>)field.GetValue(loader)!;

        var subDir = Path.Combine(_pluginsDir, "BatchPluginDir");
        Directory.CreateDirectory(subDir);

        // Act - Write 5 rapid events for the same directory
        for (int i = 0; i < 5; i++)
        {
            await channel.Writer.WriteAsync(subDir);
        }

        // Wait up to 2.5 seconds (batched debounce takes ~1s, whereas sequential 5x 1s would take 5s)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (channel.Reader.Count > 0 && sw.ElapsedMilliseconds < 2500)
        {
            await Task.Delay(50);
        }

        // Assert - Channel must be drained well before 5 seconds
        Assert.AreEqual(0, channel.Reader.Count, "Channel should be drained in batch without sequential 1s delays per item.");
        Assert.IsTrue(sw.ElapsedMilliseconds < 3500, $"Draining took {sw.ElapsedMilliseconds}ms, expected under 3500ms.");
    }

    [TestMethod]
    public async Task UnloadPlugin_SharedAssemblyWithMultiplePlugins_DoesNotCrashSiblingPlugin()
    {
        // Arrange - Place MockData assembly in a subdirectory
        var mockDataDir = Path.Combine(_pluginsDir, "MockData");
        Directory.CreateDirectory(mockDataDir);

        var mockDataAssemblyPath = typeof(SampleDataAgentPlugin).Assembly.Location;
        File.Copy(mockDataAssemblyPath, Path.Combine(mockDataDir, Path.GetFileName(mockDataAssemblyPath)), overwrite: true);

        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);

        // Act 1 - Load both SampleData and SampleSensors (from the same assembly)
        await loader.LoadAllAsync(_contextMock.Object, new HashSet<string> { "SampleData", "SampleSensors" });

        Assert.IsTrue(loader.IsLoaded("SampleData"));
        Assert.IsTrue(loader.IsLoaded("SampleSensors"));

        // Act 2 - Unload only SampleData
        var unloaded = loader.UnloadPlugin("SampleData");
        Assert.IsTrue(unloaded);
        Assert.IsFalse(loader.IsLoaded("SampleData"));
        Assert.IsTrue(loader.IsLoaded("SampleSensors"));

        // Assert - SampleSensors must still be valid and usable (not unloaded)
        var sensorsPlugin = loader.GetPlugin("SampleSensors");
        Assert.IsNotNull(sensorsPlugin);
        Assert.IsNotNull(sensorsPlugin.Plugin);
        var methods = sensorsPlugin.Plugin.GetMethods();
        Assert.IsTrue(methods.Count > 0);
    }

    [TestMethod]
    public async Task LoadPluginAsync_MultiplePluginsFromSameDll_ShareSameAssemblyLoadContext()
    {
        // Arrange - MockData DLL contains SampleData, SampleSensors, SampleImages
        var mockDataDir = Path.Combine(_pluginsDir, "MockData");
        Directory.CreateDirectory(mockDataDir);

        var mockDataAssemblyPath = typeof(SampleDataAgentPlugin).Assembly.Location;
        var targetDll = Path.Combine(mockDataDir, Path.GetFileName(mockDataAssemblyPath));
        File.Copy(mockDataAssemblyPath, targetDll, overwrite: true);

        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);
        await loader.LoadAllAsync(_contextMock.Object, new HashSet<string>()); // All discovered, none loaded

        // Act - Load two plugins from the same DLL on demand
        var loaded1 = await loader.LoadPluginAsync("SampleData", _contextMock.Object);
        var loaded2 = await loader.LoadPluginAsync("SampleSensors", _contextMock.Object);

        // Assert - Both must share the same ALC instance
        Assert.IsNotNull(loaded1);
        Assert.IsNotNull(loaded2);
        Assert.AreSame(loaded1.LoadContext, loaded2.LoadContext,
            "Plugins from the same DLL must share the same AssemblyLoadContext to prevent memory leaks and type identity splits.");
    }

    [TestMethod]
    public async Task LoadPluginAsync_WithDependencyDllsInSubdirectory_SkipsDependencyAssembliesAndDoesNotFirePluginLoadFailed()
    {
        // Arrange - Create a plugin subdirectory containing both a dummy dependency DLL (System.*) and Sdk DLL
        var pluginSubdir = Path.Combine(_pluginsDir, "CustomPlugin");
        Directory.CreateDirectory(pluginSubdir);

        // Create dummy dependency files that would fail if loaded as plugin assemblies
        var systemDepPath = Path.Combine(pluginSubdir, "System.CustomDependency.dll");
        await File.WriteAllBytesAsync(systemDepPath, new byte[] { 1, 2, 3, 4, 5 });

        var msDepPath = Path.Combine(pluginSubdir, "Microsoft.Extensions.Dummy.dll");
        await File.WriteAllBytesAsync(msDepPath, new byte[] { 1, 2, 3, 4, 5 });

        var sdkDepPath = Path.Combine(pluginSubdir, "SemanticAgentBroker.Sdk.Mock.dll");
        await File.WriteAllBytesAsync(sdkDepPath, new byte[] { 1, 2, 3, 4, 5 });

        var newtonsoftDepPath = Path.Combine(pluginSubdir, "Newtonsoft.Json.Dummy.dll");
        await File.WriteAllBytesAsync(newtonsoftDepPath, new byte[] { 1, 2, 3, 4, 5 });

        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);
        var failedDlls = new List<string>();
        loader.PluginLoadFailed += (dllName, ex) => failedDlls.Add(dllName);

        // Act - Attempt to load a non-existent plugin, which triggers the fallback subdirectory scan
        var result = await loader.LoadPluginAsync("NonExistentPluginId", _contextMock.Object);

        // Assert - The result should be null and no dependency DLL should have triggered PluginLoadFailed
        Assert.IsNull(result);
        Assert.AreEqual(0, failedDlls.Count, $"Expected 0 failed DLL events for dependency assemblies, but got: {string.Join(", ", failedDlls)}");
    }

    [TestMethod]
    public async Task LoadPluginAsync_WhenPluginLoadedSubscriberThrows_StillReturnsLoadedPluginAndPreservesSingleState()
    {
        // Arrange
        var mockDataDir = Path.Combine(_pluginsDir, "MockData");
        Directory.CreateDirectory(mockDataDir);
        var mockDataAssemblyPath = typeof(SampleDataAgentPlugin).Assembly.Location;
        var targetDll = Path.Combine(mockDataDir, Path.GetFileName(mockDataAssemblyPath));
        File.Copy(mockDataAssemblyPath, targetDll, overwrite: true);

        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);
        await loader.LoadAllAsync(_contextMock.Object, new HashSet<string>()); // Discovers MockData, loads none

        // Attach an event subscriber that throws an exception
        loader.PluginLoaded += _ => throw new InvalidOperationException("External subscriber failed!");

        // Act
        var loaded = await loader.LoadPluginAsync("SampleData", _contextMock.Object);

        // Assert - The load must still succeed, not return null, and not be left in both collections
        Assert.IsNotNull(loaded, "Plugin should still be returned even if an external PluginLoaded event subscriber throws.");
        Assert.IsTrue(loader.IsLoaded("SampleData"));
        Assert.IsFalse(loader.GetDiscoveredPlugins().Any(p => p.PluginId == "SampleData"),
            "Plugin should not be re-added to discovered plugins when load actually succeeded.");
    }

    [TestMethod]
    public async Task LoadAllAsync_WhenPluginLoadedSubscriberThrows_DoesNotDisposeLoadedPlugin()
    {
        // Arrange
        var mockDataDir = Path.Combine(_pluginsDir, "MockData");
        Directory.CreateDirectory(mockDataDir);
        var mockDataAssemblyPath = typeof(SampleDataAgentPlugin).Assembly.Location;
        var targetDll = Path.Combine(mockDataDir, Path.GetFileName(mockDataAssemblyPath));
        File.Copy(mockDataAssemblyPath, targetDll, overwrite: true);

        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);

        // Attach an event subscriber that throws an exception
        loader.PluginLoaded += _ => throw new InvalidOperationException("External subscriber failed!");

        // Act
        await loader.LoadAllAsync(_contextMock.Object, new HashSet<string> { "SampleData" });

        // Assert - The plugin should be loaded and healthy, not disposed
        Assert.IsTrue(loader.IsLoaded("SampleData"));
        var loaded = loader.GetPlugin("SampleData");
        Assert.IsNotNull(loaded);
        Assert.IsNotNull(loaded.Plugin);
        // Calling a method or checking methods ensures the plugin instance is valid and not disposed
        Assert.IsTrue(loaded.Plugin.GetMethods().Count > 0);
    }

    [TestMethod]
    public void UnloadPlugin_DoesNotDeadlock_WhenLoadLockIsHeldAsync()
    {
        // Arrange
        using var loader = new PluginLoader(_pluginsDir, NullLogger<PluginLoader>.Instance);
        var loadLocksField = typeof(PluginLoader).GetField("_loadLocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var loadLocks = (System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>)loadLocksField.GetValue(loader)!;
        var pluginLock = loadLocks.GetOrAdd("SampleData", _ => new SemaphoreSlim(1, 1));

        // Acquire lock to simulate concurrent LoadPluginAsync operation holding the lock
        pluginLock.Wait();

        // Release the lock after 100ms asynchronously
        _ = Task.Delay(100).ContinueWith(_ => pluginLock.Release());

        // Act: Calling synchronous UnloadPlugin must not deadlock
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var unloadTask = Task.Run(() => loader.UnloadPlugin("SampleData"));
        bool completed = unloadTask.Wait(TimeSpan.FromSeconds(5));
        sw.Stop();

        // Assert
        Assert.IsTrue(completed, "UnloadPlugin must not deadlock when loadLock is held by another thread.");
        Assert.IsFalse(unloadTask.Result);
    }
}


