using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Moq;
using SemanticAgentBroker.Core.Features.KernelProvider.Implementations;
using SemanticAgentBroker.Core.LoadBalancing;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Core.KernelProvider;

[TestClass]
public class KernelProviderServiceTests
{
    private Mock<IHttpClientFactory> _httpClientFactoryMock = null!;
    private Mock<IConnectionLoadBalancer> _loadBalancerMock = null!;
    private AiProviderOptions _options = null!;

    [TestInitialize]
    public void Setup()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());

        _loadBalancerMock = new Mock<IConnectionLoadBalancer>();

        _options = new AiProviderOptions
        {
            Connections = new List<ProviderConnection>
            {
                new ProviderConnection
                {
                    Id = "ollama-primary",
                    EndpointName = "Ollama Primary",
                    ProviderType = "Ollama",
                    Endpoint = "http://localhost:11434/v1",
                    ApiKey = "",
                    Models = new List<ModelConfig>
                    {
                        new ModelConfig { Id = "qwen2.5-coder:32b" }
                    }
                },
                new ProviderConnection
                {
                    Id = "openai-cloud",
                    EndpointName = "OpenAI Cloud",
                    ProviderType = "OpenAi",
                    Endpoint = "https://api.openai.com/v1",
                    ApiKey = "sk-test-secret-key",
                    Models = new List<ModelConfig>
                    {
                        new ModelConfig { Id = "gpt-4o" }
                    }
                }
            }
        };
    }

    [TestMethod]
    public void CreateKernelBuilder_ValidProviderAndModel_BuildsKernelSuccessfully()
    {
        // Arrange
        var service = new KernelProviderService(
            _httpClientFactoryMock.Object,
            _options,
            _loadBalancerMock.Object,
            NullLogger<KernelProviderService>.Instance);

        // Act
        var builder = service.CreateKernelBuilder("ollama-primary", "qwen2.5-coder:32b");
        var kernel = builder.Build();

        // Assert
        Assert.IsNotNull(kernel);
    }

    [TestMethod]
    public void CreateKernelBuilder_ForOllamaProvider_ConfiguresEndpointUri()
    {
        // Arrange
        var service = new KernelProviderService(
            _httpClientFactoryMock.Object,
            _options,
            _loadBalancerMock.Object,
            NullLogger<KernelProviderService>.Instance);

        // Act
        var builder = service.CreateKernelBuilder("ollama-primary", "qwen2.5-coder:32b");
        var kernel = builder.Build();

        // Assert - Verify chat completion service has endpoint configured
        var chatService = kernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
        Assert.IsNotNull(chatService);

        // Reflect into chatService -> client -> endpoint to verify it points to localhost:11434 and not api.openai.com
        var clientField = chatService.GetType().GetField("_client", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? chatService.GetType().GetField("_openAIClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (clientField != null)
        {
            var client = clientField.GetValue(chatService);
            Assert.IsNotNull(client);
            var endpointProp = client.GetType().GetProperty("Endpoint", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (endpointProp != null)
            {
                var endpointValue = endpointProp.GetValue(client) as Uri;
                Assert.IsNotNull(endpointValue);
                Assert.AreEqual("localhost", endpointValue.Host);
                Assert.AreEqual(11434, endpointValue.Port);
            }
        }
        else
        {
            // Verify via Attributes or internal property
            var endpointProp = chatService.GetType().GetProperty("Endpoint", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (endpointProp != null)
            {
                var endpointValue = endpointProp.GetValue(chatService) as Uri;
                Assert.IsNotNull(endpointValue);
                Assert.AreEqual("localhost", endpointValue.Host);
            }
        }
    }

    [TestMethod]
    public void CreateKernelBuilder_ProviderNotFound_ThrowsArgumentException()
    {
        // Arrange
        var service = new KernelProviderService(
            _httpClientFactoryMock.Object,
            _options,
            _loadBalancerMock.Object,
            NullLogger<KernelProviderService>.Instance);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
        {
            service.CreateKernelBuilder("non-existent-provider", "qwen2.5-coder:32b");
        });
    }

    [TestMethod]
    public void CreateKernelBuilder_ModelNotFoundInProvider_ThrowsArgumentException()
    {
        // Arrange
        var service = new KernelProviderService(
            _httpClientFactoryMock.Object,
            _options,
            _loadBalancerMock.Object,
            NullLogger<KernelProviderService>.Instance);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
        {
            service.CreateKernelBuilder("ollama-primary", "non-existent-model");
        });
    }

    [TestMethod]
    public void CreateKernelBuilder_UnsupportedProviderType_ThrowsNotSupportedException()
    {
        // Arrange
        _options.Connections.Add(new ProviderConnection
        {
            Id = "unsupported-provider",
            ProviderType = "CustomUnknownType",
            Models = new List<ModelConfig> { new ModelConfig { Id = "custom-m1" } }
        });

        var service = new KernelProviderService(
            _httpClientFactoryMock.Object,
            _options,
            _loadBalancerMock.Object,
            NullLogger<KernelProviderService>.Instance);

        // Act & Assert
        Assert.Throws<NotSupportedException>(() =>
        {
            service.CreateKernelBuilder("unsupported-provider", "custom-m1");
        });
    }

    [TestMethod]
    public async Task CreateKernelBuilderBalancedAsync_ValidModel_SelectsFromLoadBalancer()
    {
        // Arrange
        var connection = _options.Connections[0];
        _loadBalancerMock.Setup(l => l.SelectConnectionAsync("qwen2.5-coder:32b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);

        var service = new KernelProviderService(
            _httpClientFactoryMock.Object,
            _options,
            _loadBalancerMock.Object,
            NullLogger<KernelProviderService>.Instance);

        // Act
        var (builder, connectionId) = await service.CreateKernelBuilderBalancedAsync("qwen2.5-coder:32b");
        var kernel = builder.Build();

        // Assert
        Assert.IsNotNull(kernel);
        Assert.AreEqual("ollama-primary", connectionId);
        _loadBalancerMock.Verify(l => l.SelectConnectionAsync("qwen2.5-coder:32b", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CreateKernelBuilderBalancedAsync_WhenBuildFails_ReleasesLoadBalancerConnection()
    {
        // Arrange - Connection with unsupported provider type causing BuildKernelForConnection to throw
        var unsupportedConn = new ProviderConnection
        {
            Id = "failing-conn",
            ProviderType = "UnsupportedType",
            Models = new List<ModelConfig> { new ModelConfig { Id = "test-model" } }
        };

        _loadBalancerMock.Setup(l => l.SelectConnectionAsync("test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(unsupportedConn);

        var service = new KernelProviderService(
            _httpClientFactoryMock.Object,
            _options,
            _loadBalancerMock.Object,
            NullLogger<KernelProviderService>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await service.CreateKernelBuilderBalancedAsync("test-model");
        });

        // Verify connection was released
        _loadBalancerMock.Verify(l => l.ReleaseConnection("failing-conn"), Times.Once);
    }

    [TestMethod]
    public async Task CreateKernelBuilderBalancedAsync_ModelMissingFromSelectedConnection_ThrowsInvalidOperationExceptionAndReleasesConnection()
    {
        // Arrange - Connection exists but does not contain the requested model
        var connectionWithoutModel = new ProviderConnection
        {
            Id = "conn-missing-model",
            ProviderType = "Ollama",
            Models = new List<ModelConfig> { new ModelConfig { Id = "different-model" } }
        };

        _loadBalancerMock.Setup(l => l.SelectConnectionAsync("requested-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(connectionWithoutModel);

        var service = new KernelProviderService(
            _httpClientFactoryMock.Object,
            _options,
            _loadBalancerMock.Object,
            NullLogger<KernelProviderService>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.CreateKernelBuilderBalancedAsync("requested-model");
        });

        // Verify connection was released upon exception
        _loadBalancerMock.Verify(l => l.ReleaseConnection("conn-missing-model"), Times.Once);
    }

    [TestMethod]
    [DataRow("not-a-valid-url")]
    [DataRow("localhost:11434")] // missing scheme
    [DataRow("")]
    [DataRow(null)]
    public void CreateKernelBuilder_OllamaWithInvalidOrMissingEndpoint_ThrowsInvalidOperationException(string? endpoint)
    {
        // Arrange
        var connection = new ProviderConnection
        {
            Id = "ollama-invalid",
            ProviderType = "Ollama",
            Endpoint = endpoint!,
            Models = new List<ModelConfig> { new ModelConfig { Id = "test-model" } }
        };
        _options.Connections.Add(connection);

        var service = new KernelProviderService(
            _httpClientFactoryMock.Object,
            _options,
            _loadBalancerMock.Object,
            NullLogger<KernelProviderService>.Instance);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            service.CreateKernelBuilder("ollama-invalid", "test-model");
        });

        StringAssert.Contains(ex.Message, "invalid endpoint URL");
    }

    [TestMethod]
    [DataRow("not-a-valid-url")]
    [DataRow("localhost:8000")] // missing scheme
    [DataRow("")]
    [DataRow(null)]
    public void CreateKernelBuilder_OpenAiCompatibleWithInvalidOrMissingEndpoint_ThrowsInvalidOperationException(string? endpoint)
    {
        // Arrange
        var connection = new ProviderConnection
        {
            Id = "openai-compat-invalid",
            ProviderType = "OpenAiCompatible",
            Endpoint = endpoint!,
            Models = new List<ModelConfig> { new ModelConfig { Id = "test-model" } }
        };
        _options.Connections.Add(connection);

        var service = new KernelProviderService(
            _httpClientFactoryMock.Object,
            _options,
            _loadBalancerMock.Object,
            NullLogger<KernelProviderService>.Instance);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            service.CreateKernelBuilder("openai-compat-invalid", "test-model");
        });

        StringAssert.Contains(ex.Message, "invalid endpoint URL");
    }

    [TestMethod]
    public void CreateKernelBuilder_WithDifferentCasing_ResolvesProviderAndModelSuccessfully()
    {
        // Arrange
        var service = new KernelProviderService(
            _httpClientFactoryMock.Object,
            _options,
            _loadBalancerMock.Object,
            NullLogger<KernelProviderService>.Instance);

        // Act - Request using different casing for both provider ID and model ID
        var builder = service.CreateKernelBuilder("OPENAI-CLOUD", "GPT-4O");
        var kernel = builder.Build();

        // Assert
        Assert.IsNotNull(kernel);
    }
}
