using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Globalization;
using System.Text.Json;

namespace SemanticAgentBroker.Core.Services;

/// <summary>
/// Helper for constructing provider-aware Semantic Kernel <see cref="PromptExecutionSettings"/>.
/// </summary>
public static class PromptSettingsHelper
{
    /// <summary>
    /// Builds provider-aware PromptExecutionSettings. For Ollama providers, known keys
    /// (temperature, num_predict, top_k, top_p, stop) are mapped to typed OllamaPromptExecutionSettings
    /// properties so they actually reach the Ollama API. Remaining keys go into ExtensionData.
    /// </summary>
    /// <param name="profile">The custom settings profile dictionary.</param>
    /// <param name="fallback">Fallback default settings dictionary.</param>
    /// <param name="providerType">The AI provider type string.</param>
    /// <param name="enableToolCalling">Whether tool calling should be enabled.</param>
    /// <returns>A configured <see cref="PromptExecutionSettings"/> instance.</returns>
    public static PromptExecutionSettings BuildSettings(
        Dictionary<string, object>? profile,
        Dictionary<string, object> fallback,
        string? providerType,
        bool enableToolCalling = false)
    {
        var merged = MergeProfileWithFallback(profile, fallback);

        if (IsOpenAiProvider(providerType))
        {
            var settings = BuildOpenAiSettings(merged);
            if (enableToolCalling) settings.ToolCallBehavior = ToolCallBehavior.EnableKernelFunctions;
            return settings;
        }

        return new PromptExecutionSettings
        {
            ExtensionData = merged
        };
    }

    /// <summary>
    /// Backward-compatible overload. Uses generic PromptExecutionSettings (no provider awareness).
    /// Prefer the 3-parameter overload when providerType is known.
    /// </summary>
    /// <param name="profile">The custom settings profile dictionary.</param>
    /// <param name="fallback">Fallback default settings dictionary.</param>
    /// <returns>A configured <see cref="PromptExecutionSettings"/> instance.</returns>
    public static PromptExecutionSettings BuildSettings(
        Dictionary<string, object>? profile,
        Dictionary<string, object> fallback)
    {
        var merged = MergeProfileWithFallback(profile, fallback);
        return new PromptExecutionSettings
        {
            ExtensionData = merged
        };
    }

    /// <summary>
    /// Merges profile values on top of fallback values, parsing string values to appropriate types.
    /// </summary>
    private static Dictionary<string, object> MergeProfileWithFallback(
        Dictionary<string, object>? profile,
        Dictionary<string, object> fallback)
    {
        var result = new Dictionary<string, object>();

        // Add fallback values first
        foreach (var kvp in fallback)
        {
            result[kvp.Key] = kvp.Value;
        }

        // Override with profile values, parsing strings to appropriate types if necessary
        if (profile != null)
        {
            foreach (var kvp in profile)
            {
                result[kvp.Key] = ParseValue(kvp.Value);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a value from appsettings.json - strings may need conversion to int/double/bool,
    /// and JsonElement values (from System.Text.Json deserialization) need unwrapping.
    /// </summary>
    private static object ParseValue(object value)
    {
        // Handle JsonElement from appsettings.json deserialization
        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.Number when jsonElement.TryGetInt32(out int intVal) => intVal,
                JsonValueKind.Number when jsonElement.TryGetInt64(out long longVal) => longVal,
                JsonValueKind.Number => jsonElement.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => jsonElement.GetString() ?? "",
                _ => value
            };
        }

        if (value is string strValue)
        {
            if (int.TryParse(strValue, out int intVal))
                return intVal;
            if (double.TryParse(strValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double doubleVal))
                return doubleVal;
            if (bool.TryParse(strValue, out bool boolVal))
                return boolVal;
        }

        return value;
    }



    /// <summary>
    /// Builds OpenAIPromptExecutionSettings with known keys mapped to typed properties.
    /// Crucially maps num_predict to MaxTokens if max_tokens isn't explicitly provided,
    /// ensuring Ollama-style configs work seamlessly on OpenAiCompatible endpoints (like vLLM).
    /// </summary>
    private static OpenAIPromptExecutionSettings BuildOpenAiSettings(Dictionary<string, object> merged)
    {
        var settings = new OpenAIPromptExecutionSettings();
        var extensionData = new Dictionary<string, object>();

        foreach (var kvp in merged)
        {
            var parsed = ParseValue(kvp.Value);

            switch (kvp.Key.ToLowerInvariant())
            {
                case "temperature":
                    settings.Temperature = ConvertToDouble(parsed);
                    break;

                case "max_tokens":
                    settings.MaxTokens = ConvertToInt(parsed);
                    break;

                case "num_predict":
                    // Only use num_predict if max_tokens wasn't explicitly provided
                    settings.MaxTokens ??= ConvertToInt(parsed);
                    break;

                case "top_p":
                    settings.TopP = ConvertToDouble(parsed);
                    break;

                case "presence_penalty":
                    var pp = ConvertToDouble(parsed);
                    if (pp.HasValue)
                    {
                        settings.PresencePenalty = pp.Value;
                        EnsureOptionsDictionary(extensionData)["presence_penalty"] = pp.Value;
                    }
                    break;

                case "frequency_penalty":
                    var fp = ConvertToDouble(parsed);
                    if (fp.HasValue)
                    {
                        settings.FrequencyPenalty = fp.Value;
                        EnsureOptionsDictionary(extensionData)["frequency_penalty"] = fp.Value;
                    }
                    break;

                case "repeat_penalty":
                    var rp = ConvertToDouble(parsed);
                    if (rp.HasValue)
                    {
                        extensionData["repeat_penalty"] = rp.Value;
                        EnsureOptionsDictionary(extensionData)["repeat_penalty"] = rp.Value;
                    }
                    break;

                case "top_k":
                    var tk = ConvertToInt(parsed);
                    if (tk.HasValue)
                    {
                        extensionData["top_k"] = tk.Value;
                        EnsureOptionsDictionary(extensionData)["top_k"] = tk.Value;
                    }
                    break;

                case "stop":
                    var stopList = ConvertToStringList(parsed);
                    if (stopList != null && stopList.Count > 0)
                    {
                        settings.StopSequences = stopList;
                        EnsureOptionsDictionary(extensionData)["stop"] = stopList;
                    }
                    break;

                case "num_ctx":
                    var numCtx = ConvertToInt(parsed);
                    if (numCtx.HasValue)
                    {
                        extensionData["num_ctx"] = numCtx.Value;
                        EnsureOptionsDictionary(extensionData)["num_ctx"] = numCtx.Value;
                    }
                    break;

                case "options":
                    var targetOpt = EnsureOptionsDictionary(extensionData);
                    if (parsed is Dictionary<string, object> optDict)
                    {
                        foreach (var (k, v) in optDict) targetOpt[k] = ParseValue(v);
                    }
                    else if (parsed is JsonElement je && je.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in je.EnumerateObject()) targetOpt[prop.Name] = ParseValue(prop.Value);
                    }
                    break;

                default:
                    // Other keys go to ExtensionData
                    extensionData[kvp.Key] = parsed;
                    break;
            }
        }

        if (extensionData.Count > 0)
        {
            settings.ExtensionData = extensionData;
        }

        return settings;
    }

    private static Dictionary<string, object> EnsureOptionsDictionary(Dictionary<string, object> extensionData)
    {
        if (!extensionData.TryGetValue("options", out var existing) || existing is not Dictionary<string, object> optDict)
        {
            optDict = new Dictionary<string, object>();
            extensionData["options"] = optDict;
        }
        return optDict;
    }



    private static bool IsOpenAiProvider(string? providerType) =>
        !string.IsNullOrEmpty(providerType) &&
        (providerType.Equals("OpenAiCompatible", StringComparison.OrdinalIgnoreCase) ||
         providerType.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ||
         providerType.Equals("OpenAi", StringComparison.OrdinalIgnoreCase));



    private static int? ConvertToInt(object value) => value switch
    {
        int i => i,
        long l => (int)l,
        float f => (int)f,
        double d => (int)d,
        decimal dec => (int)dec,
        string s when int.TryParse(s, out int i) => i,
        _ => null
    };

    private static double? ConvertToDouble(object value) => value switch
    {
        double d => d,
        float f => (double)f,
        int i => i,
        long l => l,
        decimal dec => (double)dec,
        string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double d) => d,
        _ => null
    };

    private static List<string>? ConvertToStringList(object? value)
    {
        if (value == null) return null;

        if (value is List<string> strList)
            return strList;

        if (value is string[] strArr)
            return strArr.ToList();

        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in jsonElement.EnumerateArray())
                {
                    var s = item.GetString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
                return list;
            }
            if (jsonElement.ValueKind == JsonValueKind.String)
            {
                var s = jsonElement.GetString();
                return !string.IsNullOrEmpty(s) ? [s] : [];
            }
        }

        if (value is IEnumerable<string> strEnumerable)
            return strEnumerable.ToList();

        if (value is System.Collections.IEnumerable objEnumerable and not string)
        {
            var list = new List<string>();
            foreach (var item in objEnumerable)
            {
                var s = item?.ToString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list;
        }

        if (value is string singleStr)
            return !string.IsNullOrEmpty(singleStr) ? [singleStr] : [];

        return null;
    }
}
