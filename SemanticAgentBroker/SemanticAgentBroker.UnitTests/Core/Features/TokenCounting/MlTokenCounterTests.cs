using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using SemanticAgentBroker.Core.Features.TokenCounting.Implementations;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Core.Features.TokenCounting;

[TestClass]
public class MlTokenCounterTests
{
    [TestMethod]
    public void CountTokens_WithEmptyOrNullString_ReturnsZero()
    {
        // Arrange
        var options = new AiProviderOptions();
        var counter = new MlTokenCounter(options, NullLogger<MlTokenCounter>.Instance);

        // Act & Assert
        Assert.AreEqual(0, counter.CountTokens("", "default"));
        Assert.AreEqual(0, counter.CountTokens((string)null!, "default"));
    }

    [TestMethod]
    public void CountTokens_WithFallbackHeuristic_ComputesLengthDividedByDivisor()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "conn-1",
                    Models =
                    [
                        new ModelConfig
                        {
                            Id = "test-model",
                            TokenCounting = new TokenCountingOptions
                            {
                                FallbackDivisor = 4.0,
                                TokenizerModel = "non-existent-tokenizer-model-123"
                            }
                        }
                    ]
                }
            ]
        };

        var counter = new MlTokenCounter(options, NullLogger<MlTokenCounter>.Instance);
        string text = new string('a', 100); // 100 chars / 4.0 = 25 tokens

        // Act
        int tokens = counter.CountTokens(text, "test-model");

        // Assert
        Assert.AreEqual(25, tokens);
    }

    [TestMethod]
    public void CountTokens_WithTiktokenTokenizer_CountsAccurately()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "conn-openai",
                    Models =
                    [
                        new ModelConfig
                        {
                            Id = "gpt-4o",
                            TokenCounting = new TokenCountingOptions
                            {
                                TokenizerModel = "gpt-4o"
                            }
                        }
                    ]
                }
            ]
        };

        var counter = new MlTokenCounter(options, NullLogger<MlTokenCounter>.Instance);

        // Act
        int tokens = counter.CountTokens("Hello, world! How are you doing today?", "gpt-4o");

        // Assert - Real BPE tokenizer returns > 0 and reasonable token count for sentence
        Assert.IsTrue(tokens > 0 && tokens < 20);
    }

    [TestMethod]
    public void CountTokens_WithHuggingFaceBpeTokenizerJson_LoadsAndCountsTokens()
    {
        // Arrange - Create a minimal valid HuggingFace BPE tokenizer.json
        var tempFile = Path.Combine(Path.GetTempPath(), "test_bpe_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var tokenizerJson = """
            {
                "model": {
                    "type": "BPE",
                    "vocab": {
                        "h": 0,
                        "e": 1,
                        "l": 2,
                        "o": 3,
                        "he": 4,
                        "ll": 5,
                        "hello": 6
                    },
                    "merges": [
                        "h e",
                        "l l",
                        "he ll"
                    ]
                },
                "added_tokens": [
                    { "id": 100, "content": "<|endoftext|>" }
                ]
            }
            """;
            File.WriteAllText(tempFile, tokenizerJson);

            var options = new AiProviderOptions
            {
                Connections =
                [
                    new ProviderConnection
                    {
                        Id = "hf-conn",
                        Models =
                        [
                            new ModelConfig
                            {
                                Id = "custom-bpe-model",
                                TokenCounting = new TokenCountingOptions
                                {
                                    TokenizerJsonPath = tempFile
                                }
                            }
                        ]
                    }
                ]
            };

            var counter = new MlTokenCounter(options, NullLogger<MlTokenCounter>.Instance);

            // Act
            int tokens = counter.CountTokens("hello", "custom-bpe-model");

            // Assert
            Assert.IsTrue(tokens > 0);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [TestMethod]
    public void CountTokens_WithCorruptedTokenizerJson_FallsBackToHeuristicWithoutThrowing()
    {
        // Arrange
        var tempFile = Path.Combine(Path.GetTempPath(), "corrupted_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempFile, "{ invalid json corrupt content");

            var options = new AiProviderOptions
            {
                Connections =
                [
                    new ProviderConnection
                    {
                        Id = "corrupt-conn",
                        Models =
                        [
                            new ModelConfig
                            {
                                Id = "corrupt-model",
                                TokenCounting = new TokenCountingOptions
                                {
                                    TokenizerJsonPath = tempFile,
                                    FallbackDivisor = 4.0
                                }
                            }
                        ]
                    }
                ]
            };

            var counter = new MlTokenCounter(options, NullLogger<MlTokenCounter>.Instance);

            // Act (16 characters / 4 = 4 tokens)
            int tokens = counter.CountTokens("1234567890123456", "corrupt-model");

            // Assert
            Assert.AreEqual(4, tokens);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [TestMethod]
    public void CountTokens_ForChatHistory_IncludesMessageOverhead()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "conn-1",
                    Models =
                    [
                        new ModelConfig
                        {
                            Id = "test-model",
                            TokenCounting = new TokenCountingOptions
                            {
                                FallbackDivisor = 4.0,
                                PerMessageOverhead = 4,
                                TokenizerModel = "invalid-tokenizer-name"
                            }
                        }
                    ]
                }
            ]
        };

        var counter = new MlTokenCounter(options, NullLogger<MlTokenCounter>.Instance);
        var history = new ChatHistory();
        history.AddUserMessage(new string('a', 40)); // 10 tokens + 4 overhead = 14
        history.AddAssistantMessage(new string('b', 80)); // 20 tokens + 4 overhead = 24

        // Act
        int tokens = counter.CountTokens(history, "test-model");

        // Assert (14 + 24 = 38)
        Assert.AreEqual(38, tokens);
    }

    [TestMethod]
    public void CountTokens_ForChatHistoryWithNullMessageContent_CountsOverheadOnly()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "conn-1",
                    Models =
                    [
                        new ModelConfig
                        {
                            Id = "test-model",
                            TokenCounting = new TokenCountingOptions
                            {
                                FallbackDivisor = 4.0,
                                PerMessageOverhead = 4,
                                TokenizerModel = "invalid-tokenizer-name"
                            }
                        }
                    ]
                }
            ]
        };

        var counter = new MlTokenCounter(options, NullLogger<MlTokenCounter>.Instance);
        var history = new ChatHistory();
        history.Add(new ChatMessageContent(AuthorRole.Assistant, content: (string?)null)); // Null text content -> 0 tokens + 4 overhead

        // Act
        int tokens = counter.CountTokens(history, "test-model");

        // Assert
        Assert.AreEqual(4, tokens);
    }

    [TestMethod]
    public void CountTokens_ForEmptyChatHistory_ReturnsZero()
    {
        // Arrange
        var options = new AiProviderOptions();
        var counter = new MlTokenCounter(options, NullLogger<MlTokenCounter>.Instance);
        var emptyHistory = new ChatHistory();

        // Act
        int tokens = counter.CountTokens(emptyHistory, "default");

        // Assert
        Assert.AreEqual(0, tokens);
    }

    [TestMethod]
    public void CountTokens_WithDifferentCasingModelId_ResolvesConfigAndTokenizer()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "conn-1",
                    Models =
                    [
                        new ModelConfig
                        {
                            Id = "Test-Model-Uppercase",
                            TokenCounting = new TokenCountingOptions
                            {
                                FallbackDivisor = 2.0,
                                TokenizerModel = "invalid-tokenizer-name"
                            }
                        }
                    ]
                }
            ]
        };

        var counter = new MlTokenCounter(options, NullLogger<MlTokenCounter>.Instance);
        string text = new string('a', 100); // 100 chars / 2.0 = 50 tokens

        // Act - request with lowercase model ID
        int tokens = counter.CountTokens(text, "test-model-uppercase");

        // Assert - should match configuration with FallbackDivisor = 2.0 (50 tokens), not default 3.5 (28 tokens)
        Assert.AreEqual(50, tokens);
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(-1.0)]
    [DataRow(-50.5)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void CountTokens_WhenFallbackDivisorIsInvalid_DefaultsToSafeDivisorWithoutCrashing(double invalidDivisor)
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "conn-invalid-div",
                    Models =
                    [
                        new ModelConfig
                        {
                            Id = "model-invalid-div",
                            TokenCounting = new TokenCountingOptions
                            {
                                FallbackDivisor = invalidDivisor,
                                TokenizerModel = "non-existent-tokenizer"
                            }
                        }
                    ]
                }
            ]
        };

        var counter = new MlTokenCounter(options, NullLogger<MlTokenCounter>.Instance);
        string text = new string('a', 100);

        // Act
        int tokens = counter.CountTokens(text, "model-invalid-div");

        // Assert - Should use safe fallback (2.5) -> 100 / 2.5 = 40, must be positive
        Assert.IsTrue(tokens > 0, $"Expected positive token count, got {tokens}");
        Assert.AreEqual(40, tokens);
    }
}
