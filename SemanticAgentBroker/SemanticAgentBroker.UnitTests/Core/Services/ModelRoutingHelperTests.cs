using SemanticAgentBroker.Core.Services;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Core.Services;

[TestClass]
public class ModelRoutingHelperTests
{
    [TestMethod]
    public void ResolveMaxContextTokens_WithStickyProviderId_ResolvesFromSpecifiedConnection()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "sticky-provider",
                    IsEnabled = true,
                    Models = [new ModelConfig { Id = "qwen-2.5", MaxContextTokens = 65536 }]
                },
                new ProviderConnection
                {
                    Id = "other-provider",
                    IsEnabled = true,
                    Models = [new ModelConfig { Id = "qwen-2.5", MaxContextTokens = 32768 }]
                }
            ],
            ModelRouting = new Dictionary<TaskModelRole, ModelRouteConfig>
            {
                [TaskModelRole.PrimaryTextModel] = new() { ModelId = "qwen-2.5", ProviderId = "sticky-provider" }
            }
        };

        // Act
        int maxTokens = ModelRoutingHelper.ResolveMaxContextTokens(options);

        // Assert
        Assert.AreEqual(65536, maxTokens);
    }

    [TestMethod]
    public void ResolveMaxContextTokens_WithoutStickyProvider_ScansEnabledConnections()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "disabled-provider",
                    IsEnabled = false,
                    Models = [new ModelConfig { Id = "qwen-2.5", MaxContextTokens = 128000 }]
                },
                new ProviderConnection
                {
                    Id = "enabled-provider",
                    IsEnabled = true,
                    Models = [new ModelConfig { Id = "qwen-2.5", MaxContextTokens = 32768 }]
                }
            ],
            ModelRouting = new Dictionary<TaskModelRole, ModelRouteConfig>
            {
                [TaskModelRole.PrimaryTextModel] = new() { ModelId = "qwen-2.5" }
            }
        };

        // Act
        int maxTokens = ModelRoutingHelper.ResolveMaxContextTokens(options);

        // Assert
        Assert.AreEqual(32768, maxTokens);
    }

    [TestMethod]
    public void ResolveMaxContextTokens_WithNoMatchingModel_ReturnsDefault16384()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections = [],
            ModelRouting = new Dictionary<TaskModelRole, ModelRouteConfig>()
        };

        // Act
        int maxTokens = ModelRoutingHelper.ResolveMaxContextTokens(options);

        // Assert
        Assert.AreEqual(16384, maxTokens);
    }

    [TestMethod]
    public void ResolveMaxContextTokens_WithNonExistentStickyProvider_FallsBackToDefault16384()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "valid-conn",
                    IsEnabled = true,
                    Models = [new ModelConfig { Id = "gpt-4", MaxContextTokens = 8192 }]
                }
            ],
            ModelRouting = new Dictionary<TaskModelRole, ModelRouteConfig>
            {
                [TaskModelRole.PrimaryTextModel] = new() { ModelId = "gpt-4", ProviderId = "missing-provider-id" }
            }
        };

        // Act
        int maxTokens = ModelRoutingHelper.ResolveMaxContextTokens(options);

        // Assert
        Assert.AreEqual(16384, maxTokens);
    }

    [TestMethod]
    public void ResolveMaxContextTokens_WithStickyProviderExistingButModelMissing_ReturnsDefault16384()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "existing-provider",
                    IsEnabled = true,
                    Models = [new ModelConfig { Id = "other-model", MaxContextTokens = 8192 }]
                }
            ],
            ModelRouting = new Dictionary<TaskModelRole, ModelRouteConfig>
            {
                [TaskModelRole.PrimaryTextModel] = new() { ModelId = "target-model-not-in-conn", ProviderId = "existing-provider" }
            }
        };

        // Act
        int maxTokens = ModelRoutingHelper.ResolveMaxContextTokens(options);

        // Assert
        Assert.AreEqual(16384, maxTokens);
    }

    [TestMethod]
    public void ResolveMaxContextTokens_WithDifferentCasing_ResolvesSuccessfully()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "Ollama-Main",
                    IsEnabled = true,
                    Models = [new ModelConfig { Id = "Qwen2.5:14b", MaxContextTokens = 32768 }]
                }
            ],
            ModelRouting = new Dictionary<TaskModelRole, ModelRouteConfig>
            {
                [TaskModelRole.PrimaryTextModel] = new() { ModelId = "qwen2.5:14b", ProviderId = "ollama-main" }
            }
        };

        // Act
        int maxTokens = ModelRoutingHelper.ResolveMaxContextTokens(options);

        // Assert
        Assert.AreEqual(32768, maxTokens);
    }
}

