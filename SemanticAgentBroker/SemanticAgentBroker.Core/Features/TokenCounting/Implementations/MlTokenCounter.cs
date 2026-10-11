using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using Microsoft.SemanticKernel.ChatCompletion;
using SemanticAgentBroker.Shared.Configuration;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SemanticAgentBroker.Core.Features.TokenCounting.Implementations;

/// <summary>
/// Token counter using Microsoft.ML.Tokenizers for accurate BPE-based token counting.
/// Falls back to a configurable heuristic if tokenizer initialization fails.
/// </summary>
public class MlTokenCounter : ITokenCounter
{
    private readonly AiProviderOptions _options;
    private readonly ILogger<MlTokenCounter> _logger;
    private readonly ConcurrentDictionary<string, Tokenizer?> _tokenizers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _useFallback = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TokenCountingOptions> _modelOptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of <see cref="MlTokenCounter"/>.
    /// </summary>
    /// <param name="options">The AI provider options.</param>
    /// <param name="logger">Logger instance.</param>
    public MlTokenCounter(AiProviderOptions options, ILogger<MlTokenCounter> logger)
    {
        _options = options;
        _logger = logger;
    }

    private TokenCountingOptions GetOptions(string modelId)
    {
        return _modelOptions.GetOrAdd(modelId, id =>
        {
            var modelConfig = _options.Connections
                .SelectMany(c => c.Models)
                .FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            
            return modelConfig?.TokenCounting ?? new TokenCountingOptions();
        });
    }

    private Tokenizer? GetTokenizer(string modelId)
    {
        if (_useFallback.TryGetValue(modelId, out bool fallback) && fallback)
        {
            return null;
        }

        return _tokenizers.GetOrAdd(modelId, id =>
        {
            var options = GetOptions(id);
            try
            {
                Tokenizer? tokenizer = null;
                var resolvedPath = PathResolutionHelper.ResolvePath(options.TokenizerJsonPath);
                if (!string.IsNullOrEmpty(resolvedPath) && System.IO.File.Exists(resolvedPath))
                {
                    if (resolvedPath.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
                    {
                        using var stream = System.IO.File.OpenRead(resolvedPath);
                        tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: true, addEndOfSentence: false, specialTokens: null);
                    }
                    else if (resolvedPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        tokenizer = LoadHuggingFaceTokenizerJson(resolvedPath);
                    }
                    else
                    {
                        using var stream = System.IO.File.OpenRead(resolvedPath);
                        tokenizer = TiktokenTokenizer.Create(stream, preTokenizer: null, normalizer: null, specialTokens: null, cacheSize: 8192);
                    }
                    _logger.LogInformation("ML.Tokenizers initialized successfully from {Path} for model {Model}", resolvedPath, id);
                }
                else
                {
                    tokenizer = TiktokenTokenizer.CreateForModel(options.TokenizerModel);
                    _logger.LogInformation("ML.Tokenizers initialized successfully ({TokenizerModel}) for model {Model}", options.TokenizerModel, id);
                }
                
                _useFallback[id] = false;
                return tokenizer;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize ML.Tokenizers for model '{Model}'. Falling back to heuristic token counting", id);
                _useFallback[id] = true;
                return null;
            }
        });
    }

    private static Tokenizer LoadHuggingFaceTokenizerJson(string path)
    {
        using var jsonDoc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var root = jsonDoc.RootElement;

        if (!root.TryGetProperty("model", out var modelElement))
            throw new InvalidOperationException("Invalid tokenizer.json: 'model' property not found.");

        var modelType = modelElement.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString()
            : null;

        if (modelType != "BPE")
            throw new InvalidOperationException($"Unsupported tokenizer model type: {modelType}. Microsoft.ML.Tokenizers supports BPE models via tokenizer.json parsing.");

        var vocabDict = new Dictionary<string, int>();

        if (modelElement.TryGetProperty("vocab", out var vocabProp))
        {
            if (vocabProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in vocabProp.EnumerateObject())
                    vocabDict[prop.Name] = prop.Value.GetInt32();
            }
            else if (vocabProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in vocabProp.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
                    {
                        var token = item[0].GetString();
                        if (token != null)
                            vocabDict[token] = item[1].GetInt32();
                    }
                }
            }
        }

        if (root.TryGetProperty("added_tokens", out var addedTokens) && addedTokens.ValueKind == JsonValueKind.Array)
        {
            foreach (var added in addedTokens.EnumerateArray())
            {
                if (added.TryGetProperty("content", out var contentProp) && added.TryGetProperty("id", out var idProp))
                {
                    var token = contentProp.ValueKind == JsonValueKind.String ? contentProp.GetString() : null;
                    if (token != null)
                        vocabDict[token] = idProp.GetInt32();
                }
            }
        }

        using var vocabMs = new MemoryStream();
        var writerOptions = new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var writer = new Utf8JsonWriter(vocabMs, writerOptions))
        {
            writer.WriteStartObject();
            foreach (var kvp in vocabDict)
                writer.WriteNumber(kvp.Key, kvp.Value);
            writer.WriteEndObject();
        }
        vocabMs.Position = 0;

        using var mergesMs = new MemoryStream();
        if (modelElement.TryGetProperty("merges", out var mergesProp) && mergesProp.ValueKind == JsonValueKind.Array)
        {
            using var writer = new StreamWriter(mergesMs, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true);
            writer.WriteLine("#version: 0.2");

            foreach (var merge in mergesProp.EnumerateArray())
            {
                string? mergeLine = null;
                if (merge.ValueKind == JsonValueKind.String)
                    mergeLine = merge.GetString();
                else if (merge.ValueKind == JsonValueKind.Array)
                {
                    var partsList = new List<string>();
                    foreach (var element in merge.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.String)
                            partsList.Add(element.GetString() ?? "");
                    }
                    mergeLine = string.Join(" ", partsList);
                }

                if (!string.IsNullOrEmpty(mergeLine))
                {
                    if (mergeLine.Contains('\n') || mergeLine.Contains('\r')) continue;
                    var parts = mergeLine.Split(' ');
                    if (parts.Length == 2 && vocabDict.ContainsKey(parts[0]) && vocabDict.ContainsKey(parts[1]))
                        writer.WriteLine(mergeLine);
                }
            }
            writer.Flush();
            mergesMs.Position = 0;
        }
        else
        {
            throw new InvalidOperationException("Invalid BPE model: 'merges' array not found in model configuration.");
        }

        return BpeTokenizer.Create(vocabMs, mergesMs);
    }

    /// <inheritdoc />
    public int CountTokens(string text, string modelId)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var options = GetOptions(modelId);
        var tokenizer = GetTokenizer(modelId);
        bool useFallback = _useFallback.TryGetValue(modelId, out var fb) && fb;

        double divisor = options.FallbackDivisor > 0 && !double.IsNaN(options.FallbackDivisor) && !double.IsInfinity(options.FallbackDivisor)
            ? options.FallbackDivisor
            : 2.5;

        if (useFallback || tokenizer == null)
            return (int)(text.Length / divisor);

        try
        {
            return tokenizer.CountTokens(text);
        }
        catch
        {
            return (int)(text.Length / divisor);
        }
    }

    /// <inheritdoc />
    public int CountTokens(ChatHistory history, string modelId)
    {
        var options = GetOptions(modelId);
        int total = 0;
        foreach (var msg in history)
        {
            total += CountTokens(msg.Content ?? "", modelId);
            total += options.PerMessageOverhead;
        }
        return total;
    }
}