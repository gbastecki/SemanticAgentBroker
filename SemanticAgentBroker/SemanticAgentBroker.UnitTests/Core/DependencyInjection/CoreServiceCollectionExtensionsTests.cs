using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SemanticAgentBroker.Core.DependencyInjection;
using SemanticAgentBroker.Core.Features.Agents;
using SemanticAgentBroker.Core.Features.KernelProvider;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Core.Features.TaskState;
using SemanticAgentBroker.Core.Features.TokenCounting;
using SemanticAgentBroker.Core.Filters;
using SemanticAgentBroker.Core.LoadBalancing;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Core.DependencyInjection;

[TestClass]
public class CoreServiceCollectionExtensionsTests
{
    [TestMethod]
    public void AddSemanticAgentBrokerCore_RegistersAndResolvesAllServices()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["AiProviders:DefaultTimeoutMinutes"] = "5",
            ["AiProviders:Connections:0:Id"] = "ollama-local",
            ["AiProviders:Connections:0:Endpoint"] = "http://localhost:11434/",
            ["AiProviders:Connections:0:ProviderType"] = "Ollama",
            ["AiProviders:Connections:0:IsEnabled"] = "true",
            ["AgentSystem:TaskWorkspacesPath"] = Path.GetTempPath(),
            ["AgentSystem:TraceLogsDirectory"] = Path.GetTempPath(),
            ["AgentSystem:PluginsDirectory"] = Path.GetTempPath()
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);

        // Register mock task store required by TaskStateManager and Filters
        var taskStoreMock = new Mock<IAgentTaskStore>();
        services.AddScoped(_ => taskStoreMock.Object);

        // Act
        var builder = services.AddSemanticAgentBrokerCore(config);

        // Assert Builder
        Assert.IsNotNull(builder);
        Assert.IsNotNull(builder.AiOptions);
        Assert.IsNotNull(builder.SystemOptions);
        Assert.AreEqual(1, builder.AiOptions.Connections.Count);

        // Build container and test service resolutions
        using var sp = services.BuildServiceProvider();

        // Singletons
        Assert.IsNotNull(sp.GetRequiredService<AiProviderOptions>());
        Assert.IsNotNull(sp.GetRequiredService<AgentSystemOptions>());
        Assert.IsNotNull(sp.GetRequiredService<PythonSandboxOptions>());
        Assert.IsNotNull(sp.GetRequiredService<InferenceOptions>());
        Assert.IsNotNull(sp.GetRequiredService<HealthCheckOptions>());
        Assert.IsNotNull(sp.GetRequiredService<IKernelProviderService>());
        Assert.IsNotNull(sp.GetRequiredService<IWorkspaceLockManager>());
        Assert.IsNotNull(sp.GetRequiredService<SemanticAgentBroker.Sdk.IWorkspaceLockManager>());
        Assert.IsNotNull(sp.GetRequiredService<ITokenCounter>());
        Assert.IsNotNull(sp.GetRequiredService<IAgentRegistry>());
        Assert.IsNotNull(sp.GetRequiredService<IPluginRegistry>());
        Assert.IsNotNull(sp.GetRequiredService<IPluginLoader>());
        Assert.IsNotNull(sp.GetRequiredService<IConnectionLoadBalancer>());
        Assert.IsNotNull(sp.GetRequiredService<IEndpointHealthMonitor>());

        // Scoped
        using var scope = sp.CreateScope();
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IAgentTaskContext>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<ITaskStateManager>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IPluginContextFactory>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<Microsoft.SemanticKernel.IFunctionInvocationFilter>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<Microsoft.SemanticKernel.IPromptRenderFilter>());
    }

    [TestMethod]
    public void AddSemanticAgentBrokerCore_RegistersSubOptionsAndScopedFilters()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["AiProviders:PythonSandbox:MemoryLimitMb"] = "1024",
            ["AiProviders:Inference:MaxFinalAnswerInlineChars"] = "5000",
            ["AiProviders:HealthCheck:IntervalSeconds"] = "45"
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        var taskStoreMock = new Mock<IAgentTaskStore>();
        services.AddScoped(_ => taskStoreMock.Object);

        // Act
        services.AddSemanticAgentBrokerCore(config);
        using var sp = services.BuildServiceProvider();

        // Assert
        var sandbox = sp.GetRequiredService<PythonSandboxOptions>();
        var inference = sp.GetRequiredService<InferenceOptions>();
        var health = sp.GetRequiredService<HealthCheckOptions>();

        Assert.AreEqual(1024, sandbox.MemoryLimitMb);
        Assert.AreEqual(5000, inference.MaxFinalAnswerInlineChars);
        Assert.AreEqual(45, health.IntervalSeconds);
    }

    [TestMethod]
    public void AddSemanticAgentBrokerCore_WithRelativePathsAndConnectionStrings_ResolvesAbsolutePaths()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["AiProviders:Connections:0:Id"] = "ollama-local",
            ["AiProviders:Connections:0:Endpoint"] = "http://localhost:11434/",
            ["AiProviders:Connections:0:ProviderType"] = "Ollama",
            ["AiProviders:Connections:0:Models:0:Id"] = "qwen2.5-coder:32b",
            ["AiProviders:Connections:0:Models:0:TokenCounting:TokenizerJsonPath"] = "%TEMP%/models/tokenizer.json",
            ["AgentSystem:TaskWorkspacesPath"] = "~/workspaces",
            ["AgentSystem:TraceLogsDirectory"] = "%TEMP%/traces",
            ["AgentSystem:PluginsDirectory"] = "%USERPROFILE%/plugins",
            ["AgentSystem:DatabaseConnectionString"] = "Data Source=%TEMP%/data/agents.db",
            ["AgentSystem:AuthDatabaseConnectionString"] = "Data Source=data/auth.db",
            ["AgentSystem:TokensDatabaseConnectionString"] = "Data Source=data/tokens.db"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        var taskStoreMock = new Mock<IAgentTaskStore>();
        services.AddScoped(_ => taskStoreMock.Object);

        // Act
        var builder = services.AddSemanticAgentBrokerCore(config);

        // Assert - Paths must be rooted / absolute
        Assert.IsTrue(Path.IsPathRooted(builder.SystemOptions.TaskWorkspacesPath));
        Assert.IsTrue(Path.IsPathRooted(builder.SystemOptions.TraceLogsDirectory));
        Assert.IsTrue(Path.IsPathRooted(builder.SystemOptions.PluginsDirectory));
        StringAssert.Contains(builder.SystemOptions.DatabaseConnectionString, "Data Source=");
        Assert.IsTrue(Path.IsPathRooted(builder.AiOptions.Connections[0].Models[0].TokenCounting!.TokenizerJsonPath));
    }

    [TestMethod]
    public async Task AddSemanticAgentBrokerCore_HttpClientAgentPlugin_DirectPrivateIp_BlockedBySsrfPolicyWithoutDnsException()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["AiProviders:Connections:0:Id"] = "ollama-local",
            ["AiProviders:Connections:0:Endpoint"] = "http://localhost:11434/",
            ["AiProviders:Connections:0:ProviderType"] = "Ollama",
            ["AgentSystem:TaskWorkspacesPath"] = Path.GetTempPath(),
            ["AgentSystem:TraceLogsDirectory"] = Path.GetTempPath(),
            ["AgentSystem:PluginsDirectory"] = Path.GetTempPath()
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        var taskStoreMock = new Mock<IAgentTaskStore>();
        services.AddScoped(_ => taskStoreMock.Object);

        services.AddSemanticAgentBrokerCore(config);
        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("HttpClientAgentPlugin");

        // Act & Assert
        // 10.254.254.254 is a private IP with no reverse PTR DNS record.
        // It must be blocked by SSRF policy with HttpRequestException (Forbidden), NOT throw SocketException ("No such host is known").
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await client.GetAsync("http://10.254.254.254:8080/test");
        });

        StringAssert.Contains(ex.Message, "HTTP request blocked by SSRF security policy");
        var statusCode = ex.StatusCode ?? (ex.InnerException as HttpRequestException)?.StatusCode;
        Assert.AreEqual(System.Net.HttpStatusCode.Forbidden, statusCode);
    }
}
