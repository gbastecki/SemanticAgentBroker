using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Moq;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Core.Features.PluginHost.Implementations;
using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.UnitTests.Core.Features.PluginHost;

[TestClass]
public class PluginHostTests
{
    private class DummyPlugin : PluginBase
    {
        private readonly List<IPluginMethod> _methods;

        public DummyPlugin(string id, params IPluginMethod[] methods)
        {
            Id = id;
            DisplayName = id;
            Description = $"{id} description";
            Version = "1.0.0";
            Author = "TestAuthor";
            _methods = methods.ToList();
        }

        public override string Id { get; }
        public override string DisplayName { get; }
        public override string Description { get; }
        public override string Version { get; }
        public override string Author { get; }
        public override IReadOnlyList<IPluginMethod> GetMethods() => _methods;
        public override IReadOnlyList<PluginConfigEntry> GetConfigurationSchema() => [];
    }

    private class DummyMethod : PluginMethodBase
    {
        private readonly Func<IDictionary<string, object?>, IPluginContext, CancellationToken, Task<string>> _execute;

        public DummyMethod(
            string id,
            bool isDangerous = false,
            IReadOnlyList<PluginParameter>? parameters = null,
            Func<IDictionary<string, object?>, IPluginContext, CancellationToken, Task<string>>? execute = null)
        {
            Id = id;
            DisplayName = id;
            Description = $"{id} method description";
            ReturnDescription = "Result string";
            IsDangerous = isDangerous;
            Parameters = parameters ?? [new PluginParameter { Name = "input", ParameterType = typeof(string), IsRequired = true }];
            _execute = execute ?? new Func<IDictionary<string, object?>, IPluginContext, CancellationToken, Task<string>>(
                (args, ctx, ct) => Task.FromResult($"Executed {id} with input {(args.TryGetValue("input", out var v) ? v : null)}"));
        }

        public override string Id { get; }
        public override string DisplayName { get; }
        public override string Description { get; }
        public override string ReturnDescription { get; }
        public override bool IsDangerous { get; }
        public override IReadOnlyList<PluginParameter> Parameters { get; }

        public override Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct) =>
            _execute(arguments, context, ct);
    }

    [TestMethod]
    public void PluginRegistry_RegisterAndQuery_ReturnsExpectedPluginsAndMethods()
    {
        // Arrange
        var registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);
        var method1 = new DummyMethod("MethodA");
        var method2 = new DummyMethod("MethodB");
        var plugin1 = new DummyPlugin("Plugin1", method1, method2);

        var loadedPlugin = new LoadedPlugin(
            plugin1,
            typeof(DummyPlugin).Assembly,
            "Dummy.dll",
            "hash123",
            "dummy_folder",
            System.Runtime.Loader.AssemblyLoadContext.Default);

        // Act
        registry.Register(loadedPlugin);

        // Assert
        Assert.IsTrue(registry.Contains("Plugin1"));
        Assert.IsNotNull(registry.GetPlugin("Plugin1"));
        Assert.AreEqual(1, registry.GetAllPlugins().Count);
        CollectionAssert.Contains(registry.GetAllPluginIds().ToList(), "Plugin1");

        var retrievedMethod = registry.GetMethod("Plugin1", "MethodA");
        Assert.IsNotNull(retrievedMethod);
        Assert.AreEqual("MethodA", retrievedMethod.Id);

        var boundMethods = registry.GetMethods([("Plugin1", "MethodB")]);
        Assert.AreEqual(1, boundMethods.Count);
        Assert.AreEqual("MethodB", boundMethods[0].Method.Id);

        // Act - Unregister
        var unregistered = registry.Unregister("Plugin1");
        Assert.IsTrue(unregistered);
        Assert.IsFalse(registry.Contains("Plugin1"));
    }

    [TestMethod]
    public void PluginContextFactory_CreatesExecutionContextWithMergedConfiguration()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton("TestServiceInstance");
        var sp = services.BuildServiceProvider();

        var inMemoryConfig = new Dictionary<string, string?>
        {
            ["AgentSystem:Plugins:Plugin1:KeyFromAppSettings"] = "ValFromApp"
        };
        var appConfig = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();

        var factory = new PluginContextFactory(sp, NullLoggerFactory.Instance, appConfig);

        var dbConfig = new Dictionary<string, string>
        {
            ["KeyFromDb"] = "ValFromDb"
        };

        var sessionId = Guid.NewGuid();

        // Act
        var context = factory.CreateExecutionContext(
            workspacePath: "/app/workdir",
            sessionId: sessionId,
            pluginId: "Plugin1",
            dbConfig: dbConfig);

        // Assert
        Assert.AreEqual("/app/workdir", context.WorkspacePath);
        Assert.AreEqual(sessionId, context.SessionId);
        Assert.AreEqual("ValFromApp", context.Configuration.GetValueOrDefault("KeyFromAppSettings"));
        Assert.AreEqual("ValFromDb", context.Configuration.GetValueOrDefault("KeyFromDb"));
        Assert.AreEqual("TestServiceInstance", context.GetService<string>());
    }

    [TestMethod]
    public async Task SemanticKernelPluginAdapter_RegisterPluginMethods_CreatesAndExecutesKernelFunction()
    {
        // Arrange
        var kernel = new Kernel();
        var method = new DummyMethod("Calculate", isDangerous: false);

        var execContext = new PluginExecutionContext(
            "/app/workdir",
            Guid.NewGuid(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger.Instance);

        var contextFactoryMock = new Mock<IPluginContextFactory>();
        contextFactoryMock.Setup(f => f.CreateExecutionContext(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<IServiceProvider>()))
            .Returns(execContext);

        var methods = new List<(string PluginId, IPluginMethod Method)>
        {
            ("MathPlugin", method)
        };

        // Act
        SemanticKernelPluginAdapter.RegisterPluginMethods(
            kernel,
            methods,
            contextFactoryMock.Object,
            workspacePath: "/app/workdir",
            sessionId: Guid.NewGuid());

        // Assert
        Assert.IsTrue(kernel.Plugins.Contains("MathPlugin"));
        var plugin = kernel.Plugins["MathPlugin"];
        Assert.IsTrue(plugin.Contains("Calculate"));

        var function = plugin["Calculate"];
        var result = await function.InvokeAsync(kernel, new KernelArguments { ["input"] = "100" });

        Assert.IsNotNull(result);
        StringAssert.Contains(result.GetValue<string>(), "Executed Calculate with input 100");
    }

    [TestMethod]
    public void PluginRegistry_RegisterDuplicate_IgnoresAndLogsWarning()
    {
        // Arrange
        var registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);
        var plugin1 = new DummyPlugin("DuplicatePlugin", new DummyMethod("Method1"));
        var loaded1 = new LoadedPlugin(plugin1, typeof(DummyPlugin).Assembly, "Dummy1.dll", "hash1", "folder1", System.Runtime.Loader.AssemblyLoadContext.Default);
        var loaded2 = new LoadedPlugin(plugin1, typeof(DummyPlugin).Assembly, "Dummy2.dll", "hash2", "folder2", System.Runtime.Loader.AssemblyLoadContext.Default);

        // Act
        registry.Register(loaded1);
        registry.Register(loaded2); // Duplicate registration

        // Assert
        Assert.AreEqual(1, registry.GetAllPlugins().Count);
        Assert.AreEqual("hash1", registry.GetPlugin("DuplicatePlugin")?.DllHash);
    }

    [TestMethod]
    public void PluginRegistry_UnregisterNonExistent_ReturnsFalse()
    {
        // Arrange
        var registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);

        // Act
        var result = registry.Unregister("NonExistentPluginId");

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void PluginRegistry_GetMethods_WithMissingBinding_ReturnsOnlyFoundMethods()
    {
        // Arrange
        var registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);
        var method1 = new DummyMethod("RealMethod");
        var plugin1 = new DummyPlugin("RealPlugin", method1);
        var loaded = new LoadedPlugin(plugin1, typeof(DummyPlugin).Assembly, "Dummy.dll", "hash", "folder", System.Runtime.Loader.AssemblyLoadContext.Default);
        registry.Register(loaded);

        // Act
        var methods = registry.GetMethods([
            ("RealPlugin", "RealMethod"),
            ("RealPlugin", "GhostMethod"),
            ("GhostPlugin", "RealMethod")
        ]);

        // Assert
        Assert.AreEqual(1, methods.Count);
        Assert.AreEqual("RealPlugin", methods[0].PluginId);
        Assert.AreEqual("RealMethod", methods[0].Method.Id);
    }
}
