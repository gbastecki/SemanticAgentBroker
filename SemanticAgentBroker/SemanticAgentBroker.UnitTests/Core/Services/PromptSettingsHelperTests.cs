using Microsoft.SemanticKernel.Connectors.OpenAI;
using SemanticAgentBroker.Core.Services;

namespace SemanticAgentBroker.UnitTests.Core.Services;

[TestClass]
public class PromptSettingsHelperTests
{
    [TestMethod]
    public void BuildSettings_WithOpenAiCompatibleProvider_MapsTypedProperties()
    {
        // Arrange
        var fallback = new Dictionary<string, object>
        {
            ["temperature"] = 0.5,
            ["max_tokens"] = 2048,
            ["top_p"] = 0.9,
            ["stop"] = new List<string> { "<|im_end|>", "Observation:" }
        };

        var profile = new Dictionary<string, object>
        {
            ["temperature"] = 0.7
        };

        // Act
        var settings = PromptSettingsHelper.BuildSettings(profile, fallback, "OpenAiCompatible");

        // Assert
        Assert.IsInstanceOfType<OpenAIPromptExecutionSettings>(settings);
        var openAiSettings = (OpenAIPromptExecutionSettings)settings;
        Assert.AreEqual(0.7, openAiSettings.Temperature);
        Assert.AreEqual(2048, openAiSettings.MaxTokens);
        Assert.AreEqual(0.9, openAiSettings.TopP);
        Assert.IsNotNull(openAiSettings.StopSequences);
        Assert.AreEqual(2, openAiSettings.StopSequences.Count);
    }

    [TestMethod]
    public void BuildSettings_WithNumPredictAndNoMaxTokens_MapsToMaxTokens()
    {
        // Arrange - Ollama style num_predict
        var fallback = new Dictionary<string, object>
        {
            ["num_predict"] = 1000
        };

        // Act
        var settings = PromptSettingsHelper.BuildSettings(null, fallback, "Ollama");

        // Assert
        Assert.IsInstanceOfType<OpenAIPromptExecutionSettings>(settings);
        var openAiSettings = (OpenAIPromptExecutionSettings)settings;
        Assert.AreEqual(1000, openAiSettings.MaxTokens);
    }

    [TestMethod]
    public void BuildSettings_WithStringValues_ParsesToAppropriateTypes()
    {
        // Arrange - strings from configuration
        var fallback = new Dictionary<string, object>
        {
            ["temperature"] = "0.85",
            ["max_tokens"] = "4096",
            ["stop"] = "Observation:"
        };

        // Act
        var settings = PromptSettingsHelper.BuildSettings(null, fallback, "OpenAi");

        // Assert
        var openAiSettings = (OpenAIPromptExecutionSettings)settings;
        Assert.AreEqual(0.85, openAiSettings.Temperature);
        Assert.AreEqual(4096, openAiSettings.MaxTokens);
        Assert.IsNotNull(openAiSettings.StopSequences);
        Assert.AreEqual(1, openAiSettings.StopSequences.Count);
        Assert.AreEqual("Observation:", openAiSettings.StopSequences[0]);
    }

    [TestMethod]
    public void BuildSettings_WithEnableToolCalling_SetsToolCallBehavior()
    {
        // Arrange
        var fallback = new Dictionary<string, object>();

        // Act
        var settings = PromptSettingsHelper.BuildSettings(null, fallback, "OpenAiCompatible", enableToolCalling: true);

        // Assert
        var openAiSettings = (OpenAIPromptExecutionSettings)settings;
        Assert.IsNotNull(openAiSettings.ToolCallBehavior);
    }

    [TestMethod]
    public void BuildSettings_WithPenaltiesAndOllamaOptions_MapsToSettingsAndExtensionData()
    {
        // Arrange
        var fallback = new Dictionary<string, object>
        {
            ["presence_penalty"] = 0.2,
            ["frequency_penalty"] = 0.3,
            ["repeat_penalty"] = 1.15,
            ["top_k"] = 40,
            ["num_ctx"] = 32768
        };

        // Act
        var settings = PromptSettingsHelper.BuildSettings(null, fallback, "Ollama");

        // Assert
        var openAiSettings = (OpenAIPromptExecutionSettings)settings;
        Assert.AreEqual(0.2, openAiSettings.PresencePenalty);
        Assert.AreEqual(0.3, openAiSettings.FrequencyPenalty);
        Assert.IsNotNull(openAiSettings.ExtensionData);
        Assert.IsTrue(openAiSettings.ExtensionData.ContainsKey("options"));
        var options = (Dictionary<string, object>)openAiSettings.ExtensionData["options"];
        Assert.AreEqual(1.15, options["repeat_penalty"]);
        Assert.AreEqual(40, options["top_k"]);
        Assert.AreEqual(32768, options["num_ctx"]);
    }

    [TestMethod]
    public void BuildSettings_WithJsonElementValues_UnwrapsNumbersBooleansAndObjectsCorrectly()
    {
        // Arrange
        var jsonDoc = System.Text.Json.JsonDocument.Parse("""
            {
                "temperature": 0.65,
                "max_tokens": 1024,
                "presence_penalty": 0.1,
                "options": {
                    "num_ctx": 16384,
                    "top_k": 50
                }
            }
            """);

        var profile = new Dictionary<string, object>
        {
            ["temperature"] = jsonDoc.RootElement.GetProperty("temperature"),
            ["max_tokens"] = jsonDoc.RootElement.GetProperty("max_tokens"),
            ["presence_penalty"] = jsonDoc.RootElement.GetProperty("presence_penalty"),
            ["options"] = jsonDoc.RootElement.GetProperty("options")
        };

        // Act
        var settings = PromptSettingsHelper.BuildSettings(profile, new Dictionary<string, object>(), "OpenAiCompatible");

        // Assert
        Assert.IsInstanceOfType<OpenAIPromptExecutionSettings>(settings);
        var openAiSettings = (OpenAIPromptExecutionSettings)settings;
        Assert.AreEqual(0.65, openAiSettings.Temperature);
        Assert.AreEqual(1024, openAiSettings.MaxTokens);
        Assert.AreEqual(0.1, openAiSettings.PresencePenalty);
        Assert.IsNotNull(openAiSettings.ExtensionData);
        Assert.IsTrue(openAiSettings.ExtensionData.ContainsKey("options"));
        var options = (Dictionary<string, object>)openAiSettings.ExtensionData["options"];
        Assert.AreEqual(16384, options["num_ctx"]);
        Assert.AreEqual(50, options["top_k"]);
    }

    [TestMethod]
    public void BuildSettings_WithNonOpenAiProvider_ReturnsGenericSettingsWithExtensionData()
    {
        // Arrange
        var fallback = new Dictionary<string, object>
        {
            ["custom_param"] = "custom_value",
            ["temperature"] = 0.8
        };

        // Act
        var settings = PromptSettingsHelper.BuildSettings(null, fallback, "CustomProvider");

        // Assert
        Assert.IsNotInstanceOfType<OpenAIPromptExecutionSettings>(settings);
        Assert.IsNotNull(settings.ExtensionData);
        Assert.AreEqual("custom_value", settings.ExtensionData["custom_param"]);
        Assert.AreEqual(0.8, settings.ExtensionData["temperature"]);
    }

    [TestMethod]
    public void BuildSettings_WithoutProviderType_ReturnsGenericSettingsWithMergedFallback()
    {
        // Arrange
        var fallback = new Dictionary<string, object>
        {
            ["default_key"] = "default_val",
            ["temperature"] = 0.5
        };
        var profile = new Dictionary<string, object>
        {
            ["temperature"] = 0.9
        };

        // Act
        var settings = PromptSettingsHelper.BuildSettings(profile, fallback);

        // Assert
        Assert.IsNotNull(settings.ExtensionData);
        Assert.AreEqual("default_val", settings.ExtensionData["default_key"]);
        Assert.AreEqual(0.9, settings.ExtensionData["temperature"]);
    }

    [TestMethod]
    public void BuildSettings_WithJsonElementStopArray_ParsesStopSequences()
    {
        // Arrange
        var jsonDoc = System.Text.Json.JsonDocument.Parse("""
            {
                "stop": ["<|im_end|>", "Observation:"]
            }
            """);

        var profile = new Dictionary<string, object>
        {
            ["stop"] = jsonDoc.RootElement.GetProperty("stop")
        };

        // Act
        var settings = PromptSettingsHelper.BuildSettings(profile, new Dictionary<string, object>(), "OpenAiCompatible");

        // Assert
        Assert.IsInstanceOfType<OpenAIPromptExecutionSettings>(settings);
        var openAiSettings = (OpenAIPromptExecutionSettings)settings;
        Assert.IsNotNull(openAiSettings.StopSequences, "StopSequences should not be null when parsed from JsonElement array");
        Assert.AreEqual(2, openAiSettings.StopSequences.Count);
        CollectionAssert.AreEqual(new[] { "<|im_end|>", "Observation:" }, openAiSettings.StopSequences.ToArray());
    }

    [TestMethod]
    public void BuildSettings_WithStringArrayStopSequences_ParsesStopSequences()
    {
        // Arrange
        var fallback = new Dictionary<string, object>
        {
            ["stop"] = new[] { "STOP1", "STOP2" }
        };

        // Act
        var settings = PromptSettingsHelper.BuildSettings(null, fallback, "OpenAiCompatible");

        // Assert
        Assert.IsInstanceOfType<OpenAIPromptExecutionSettings>(settings);
        var openAiSettings = (OpenAIPromptExecutionSettings)settings;
        Assert.IsNotNull(openAiSettings.StopSequences, "StopSequences should not be null when parsed from string array");
        Assert.AreEqual(2, openAiSettings.StopSequences.Count);
        CollectionAssert.AreEqual(new[] { "STOP1", "STOP2" }, openAiSettings.StopSequences.ToArray());
    }

    [TestMethod]
    public void BuildSettings_WithObjectListStopSequences_ParsesStopSequences()
    {
        // Arrange
        var fallback = new Dictionary<string, object>
        {
            ["stop"] = new List<object> { "STOP_OBJ_1", "STOP_OBJ_2" }
        };

        // Act
        var settings = PromptSettingsHelper.BuildSettings(null, fallback, "Ollama");

        // Assert
        Assert.IsInstanceOfType<OpenAIPromptExecutionSettings>(settings);
        var openAiSettings = (OpenAIPromptExecutionSettings)settings;
        Assert.IsNotNull(openAiSettings.StopSequences, "StopSequences should not be null when parsed from object list");
        Assert.AreEqual(2, openAiSettings.StopSequences.Count);
        CollectionAssert.AreEqual(new[] { "STOP_OBJ_1", "STOP_OBJ_2" }, openAiSettings.StopSequences.ToArray());
    }
}
