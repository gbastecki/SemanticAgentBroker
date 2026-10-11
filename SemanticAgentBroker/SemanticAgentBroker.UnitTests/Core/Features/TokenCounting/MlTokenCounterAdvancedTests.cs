using Microsoft.Extensions.Logging.Abstractions;
using SemanticAgentBroker.Core.Features.TokenCounting.Implementations;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Core.Features.TokenCounting;

[TestClass]
public class MlTokenCounterAdvancedTests
{
    [TestMethod]
    public void CountTokens_WithEnvironmentVariableInTokenizerPath_ExpandsPathAndLoads()
    {
        // Arrange - Place a tokenizer JSON in %TEMP% and configure path with %TEMP%
        var fileName = "sab_env_test_" + Guid.NewGuid().ToString("N") + ".json";
        var fullPath = Path.Combine(Path.GetTempPath(), fileName);

        try
        {
            var tokenizerJson = """
            {
                "model": {
                    "type": "BPE",
                    "vocab": { "a": 0, "b": 1, "ab": 2 },
                    "merges": [ "a b" ]
                }
            }
            """;
            File.WriteAllText(fullPath, tokenizerJson);

            var options = new AiProviderOptions
            {
                Connections =
                [
                    new ProviderConnection
                    {
                        Id = "env-conn",
                        Models =
                        [
                            new ModelConfig
                            {
                                Id = "env-model",
                                TokenCounting = new TokenCountingOptions
                                {
                                    TokenizerJsonPath = "%TEMP%\\" + fileName
                                }
                            }
                        ]
                    }
                ]
            };

            var counter = new MlTokenCounter(options, NullLogger<MlTokenCounter>.Instance);

            // Act
            int tokens = counter.CountTokens("ab", "env-model");

            // Assert
            Assert.IsTrue(tokens > 0);
        }
        finally
        {
            if (File.Exists(fullPath))
            {
                try { File.Delete(fullPath); } catch { }
            }
        }
    }

    [TestMethod]
    public void CountTokens_RepeatedCalls_UsesCachedTokenizerInstance()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "cached-conn",
                    Models =
                    [
                        new ModelConfig
                        {
                            Id = "gpt-4o",
                            TokenCounting = new TokenCountingOptions { TokenizerModel = "gpt-4o" }
                        }
                    ]
                }
            ]
        };

        var counter = new MlTokenCounter(options, NullLogger<MlTokenCounter>.Instance);

        // Act - Call multiple times to ensure internal ConcurrentDictionary caching works stably
        int count1 = counter.CountTokens("Analyze dataset metrics", "gpt-4o");
        int count2 = counter.CountTokens("Analyze dataset metrics", "gpt-4o");
        int count3 = counter.CountTokens("Analyze dataset metrics", "gpt-4o");

        // Assert
        Assert.AreEqual(count1, count2);
        Assert.AreEqual(count2, count3);
        Assert.IsTrue(count1 > 0);
    }
}
