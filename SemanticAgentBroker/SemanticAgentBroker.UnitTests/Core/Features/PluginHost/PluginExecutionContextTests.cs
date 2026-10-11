using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticAgentBroker.Core.Features.PluginHost.Implementations;

namespace SemanticAgentBroker.UnitTests.Core.Features.PluginHost;

public interface ITestScopedService
{
    string GetName();
}

public class RootScopedService : ITestScopedService
{
    public string GetName() => "RootService";
}

public class KernelScopedService : ITestScopedService
{
    public string GetName() => "KernelService";
}

[TestClass]
public class PluginExecutionContextTests
{
    [TestMethod]
    public void PluginContextFactory_CreateExecutionContext_MergesConfigurationCorrectly()
    {
        // Arrange
        var inMemoryConfig = new Dictionary<string, string?>
        {
            ["AgentSystem:Plugins:SamplePlugin:Timeout"] = "30",
            ["AgentSystem:Plugins:SamplePlugin:Mode"] = "AppSettingsDefault"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemoryConfig)
            .Build();

        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new PluginContextFactory(services, NullLoggerFactory.Instance, configuration);

        var appSettingsOverride = new Dictionary<string, string>
        {
            ["Mode"] = "CodeOverride",
            ["ExtraKey"] = "ExtraVal"
        };

        var dbConfig = new Dictionary<string, string>
        {
            ["Mode"] = "DatabaseWinner",
            ["DbOnlyKey"] = "DbVal"
        };

        var sessionId = Guid.NewGuid();

        // Act
        var context = factory.CreateExecutionContext(
            "/workspace/path",
            sessionId,
            "SamplePlugin",
            dbConfig: dbConfig,
            appSettingsConfig: appSettingsOverride);

        // Assert
        Assert.AreEqual("/workspace/path", context.WorkspacePath);
        Assert.AreEqual(sessionId, context.SessionId);
        Assert.AreEqual("30", context.Configuration["Timeout"]); // From IConfiguration section
        Assert.AreEqual("DatabaseWinner", context.Configuration["Mode"]); // DB overrides Code & AppSettings
        Assert.AreEqual("ExtraVal", context.Configuration["ExtraKey"]);
        Assert.AreEqual("DbVal", context.Configuration["DbOnlyKey"]);
    }

    [TestMethod]
    public void PluginExecutionContext_GetService_PrefersKernelServicesOverRootServices()
    {
        // Arrange
        var rootServices = new ServiceCollection();
        rootServices.AddSingleton<ITestScopedService, RootScopedService>();
        var rootProvider = rootServices.BuildServiceProvider();

        var kernelServices = new ServiceCollection();
        kernelServices.AddSingleton<ITestScopedService, KernelScopedService>();
        var kernelProvider = kernelServices.BuildServiceProvider();

        var context = new PluginExecutionContext(
            workspacePath: "/ws",
            sessionId: Guid.NewGuid(),
            serviceProvider: rootProvider,
            logger: NullLogger.Instance,
            kernelServices: kernelProvider);

        // Act
        var service = context.GetService<ITestScopedService>();

        // Assert
        Assert.IsNotNull(service);
        Assert.AreEqual("KernelService", service.GetName());
    }

    [TestMethod]
    public void PluginExecutionContext_GetService_FallsBackToRootServicesWhenNotInKernel()
    {
        // Arrange
        var rootServices = new ServiceCollection();
        rootServices.AddSingleton<ITestScopedService, RootScopedService>();
        var rootProvider = rootServices.BuildServiceProvider();

        var kernelServices = new ServiceCollection(); // Empty kernel services
        var kernelProvider = kernelServices.BuildServiceProvider();

        var context = new PluginExecutionContext(
            workspacePath: "/ws",
            sessionId: Guid.NewGuid(),
            serviceProvider: rootProvider,
            logger: NullLogger.Instance,
            kernelServices: kernelProvider);

        // Act
        var service = context.GetService<ITestScopedService>();

        // Assert
        Assert.IsNotNull(service);
        Assert.AreEqual("RootService", service.GetName());
    }

    [TestMethod]
    public void PluginContextFactory_CreateInitContext_ReturnsInitContextWithEmptyWorkspace()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new PluginContextFactory(services, NullLoggerFactory.Instance, configuration);

        // Act
        var initContext = factory.CreateInitContext();

        // Assert
        Assert.AreEqual(string.Empty, initContext.WorkspacePath);
        Assert.AreEqual(Guid.Empty, initContext.SessionId);
        Assert.IsNotNull(initContext.Logger);
    }

    [TestMethod]
    public void PluginExecutionContext_GetService_WhenKernelServicesNull_ResolvesFromRootServices()
    {
        // Arrange
        var rootServices = new ServiceCollection();
        rootServices.AddSingleton<ITestScopedService, RootScopedService>();
        var rootProvider = rootServices.BuildServiceProvider();

        var context = new PluginExecutionContext(
            workspacePath: "/ws",
            sessionId: Guid.NewGuid(),
            serviceProvider: rootProvider,
            logger: NullLogger.Instance,
            kernelServices: null); // Explicitly null kernel services

        // Act
        var service = context.GetService<ITestScopedService>();

        // Assert
        Assert.IsNotNull(service);
        Assert.AreEqual("RootService", service.GetName());
    }

    [TestMethod]
    public void PluginExecutionContext_GetService_WhenServiceNotRegistered_ReturnsNull()
    {
        // Arrange
        var rootProvider = new ServiceCollection().BuildServiceProvider();
        var kernelProvider = new ServiceCollection().BuildServiceProvider();

        var context = new PluginExecutionContext(
            workspacePath: "/ws",
            sessionId: Guid.NewGuid(),
            serviceProvider: rootProvider,
            logger: NullLogger.Instance,
            kernelServices: kernelProvider);

        // Act
        var service = context.GetService<ITestScopedService>();

        // Assert
        Assert.IsNull(service);
    }
}
