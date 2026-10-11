using SemanticAgentBroker.Core.Services;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Core.Services;

[TestClass]
public class ProviderResolutionHelperTests
{
    [TestMethod]
    public void ResolvePrimaryProviderType_WithStickyProviderId_ReturnsMatchingConnectionProviderType()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection { Id = "openai-main", ProviderType = "OpenAiCompatible", IsEnabled = true },
                new ProviderConnection { Id = "ollama-local", ProviderType = "Ollama", IsEnabled = true }
            ]
        };
        options.ModelRouting[TaskModelRole.PrimaryTextModel] = new ModelRouteConfig
        {
            ProviderId = "ollama-local",
            ModelId = "llama3"
        };

        // Act
        var providerType = ProviderResolutionHelper.ResolvePrimaryProviderType(options);

        // Assert
        Assert.AreEqual("Ollama", providerType);
    }

    [TestMethod]
    public void ResolvePrimaryProviderType_WithoutProviderId_MatchesEnabledModelId()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "vllm-server",
                    ProviderType = "OpenAiCompatible",
                    IsEnabled = true,
                    Models = [new ModelConfig { Id = "qwen-2.5-72b" }]
                }
            ]
        };
        options.ModelRouting[TaskModelRole.PrimaryTextModel] = new ModelRouteConfig
        {
            ModelId = "qwen-2.5-72b"
        };

        // Act
        var providerType = ProviderResolutionHelper.ResolvePrimaryProviderType(options);

        // Assert
        Assert.AreEqual("OpenAiCompatible", providerType);
    }

    [TestMethod]
    public void ResolvePrimaryProviderType_WhenConnectionIsDisabled_ReturnsNull()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "disabled-conn",
                    ProviderType = "Ollama",
                    IsEnabled = false,
                    Models = [new ModelConfig { Id = "llama3" }]
                }
            ]
        };
        options.ModelRouting[TaskModelRole.PrimaryTextModel] = new ModelRouteConfig
        {
            ModelId = "llama3"
        };

        // Act
        var providerType = ProviderResolutionHelper.ResolvePrimaryProviderType(options);

        // Assert
        Assert.IsNull(providerType);
    }

    [TestMethod]
    public void ResolvePrimaryProviderType_WhenModelRouteMissing_ReturnsNull()
    {
        // Arrange
        var options = new AiProviderOptions();

        // Act
        var providerType = ProviderResolutionHelper.ResolvePrimaryProviderType(options);

        // Assert
        Assert.IsNull(providerType);
    }

    [TestMethod]
    public void ResolvePrimaryProviderType_WhenModelNotFoundInConnections_ReturnsNull()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "conn-1",
                    ProviderType = "Ollama",
                    IsEnabled = true,
                    Models = [new ModelConfig { Id = "mistral" }]
                }
            ]
        };
        options.ModelRouting[TaskModelRole.PrimaryTextModel] = new ModelRouteConfig
        {
            ModelId = "gpt-4o"
        };

        // Act
        var providerType = ProviderResolutionHelper.ResolvePrimaryProviderType(options);

        // Assert
        Assert.IsNull(providerType);
    }

    [TestMethod]
    public void ResolvePrimaryProviderType_WithDifferentCasing_ReturnsMatchingProviderType()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "Ollama-Main",
                    ProviderType = "Ollama",
                    IsEnabled = true,
                    Models = [new ModelConfig { Id = "Qwen2.5:14b" }]
                }
            ]
        };
        options.ModelRouting[TaskModelRole.PrimaryTextModel] = new ModelRouteConfig
        {
            ModelId = "qwen2.5:14b",
            ProviderId = "ollama-main"
        };

        // Act
        var providerType = ProviderResolutionHelper.ResolvePrimaryProviderType(options);

        // Assert
        Assert.AreEqual("Ollama", providerType);
    }
}
