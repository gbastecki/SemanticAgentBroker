using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.Core.Infrastructure.Http;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Core.Infrastructure;

[TestClass]
public class HttpClientBuilderExtensionsTests
{
    private string _traceDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _traceDir = Path.Combine(Path.GetTempPath(), "sab_test_traces_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_traceDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_traceDir))
        {
            try { Directory.Delete(_traceDir, true); } catch { }
        }
    }

    [TestMethod]
    public void ConfigureAiProviderClient_WithApiKey_SetsBearerAuthAndBaseAddress()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var connection = new ProviderConnection
        {
            Id = "openai-main",
            Endpoint = "https://api.openai.com/v1/",
            ApiKey = "sk-testkey123",
            IsEnabled = true
        };

        // Act
        services.AddHttpClient("openai-client")
            .ConfigureAiProviderClient(connection, timeoutMinutes: 2, traceLogsDirectory: _traceDir);

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("openai-client");

        // Assert
        Assert.AreEqual(new Uri("https://api.openai.com/v1/"), client.BaseAddress);
        Assert.AreEqual(TimeSpan.FromMinutes(2), client.Timeout);
        Assert.IsNotNull(client.DefaultRequestHeaders.Authorization);
        Assert.AreEqual("Bearer", client.DefaultRequestHeaders.Authorization.Scheme);
        Assert.AreEqual("sk-testkey123", client.DefaultRequestHeaders.Authorization.Parameter);
    }

    [TestMethod]
    public void ConfigureAiProviderClient_WithBasicAuth_ConfiguresWithoutError()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var connection = new ProviderConnection
        {
            Id = "basic-auth-conn",
            Endpoint = "https://custom-ai.internal/",
            ApiKey = string.Empty,
            HttpBasicAuthUsername = "aiuser",
            HttpBasicAuthPassword = "secretpassword",
            IsEnabled = true
        };

        // Act
        services.AddHttpClient("basic-client")
            .ConfigureAiProviderClient(connection, timeoutMinutes: 5, traceLogsDirectory: _traceDir);

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("basic-client");

        // Assert
        Assert.IsNotNull(client);
        Assert.AreEqual(new Uri("https://custom-ai.internal/"), client.BaseAddress);
        Assert.AreEqual(TimeSpan.FromMinutes(5), client.Timeout);
    }

    [TestMethod]
    public void ConfigureAiProviderClient_WithOllamaProvider_BuildsClientSuccessfully()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var connection = new ProviderConnection
        {
            Id = "ollama-1",
            ProviderType = "Ollama",
            Endpoint = "http://localhost:11434/",
            IsEnabled = true
        };

        // Act
        services.AddHttpClient("ollama-test")
            .ConfigureAiProviderClient(connection, timeoutMinutes: 10, traceLogsDirectory: _traceDir);

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("ollama-test");

        // Assert
        Assert.IsNotNull(client);
        Assert.AreEqual(new Uri("http://localhost:11434/"), client.BaseAddress);
        Assert.AreEqual(TimeSpan.FromMinutes(10), client.Timeout);
    }

    [TestMethod]
    public void ConfigureAiProviderClient_WithTrustUntrustedCerts_BuildsWithoutError()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var connection = new ProviderConnection
        {
            Id = "self-signed-endpoint",
            Endpoint = "https://192.168.1.100:8443/",
            TrustUntrustedCertificates = true,
            IsEnabled = true
        };

        // Act
        services.AddHttpClient("self-signed-client")
            .ConfigureAiProviderClient(connection, timeoutMinutes: 3, traceLogsDirectory: _traceDir);

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("self-signed-client");

        // Assert
        Assert.IsNotNull(client);
        Assert.AreEqual(new Uri("https://192.168.1.100:8443/"), client.BaseAddress);
    }
}
