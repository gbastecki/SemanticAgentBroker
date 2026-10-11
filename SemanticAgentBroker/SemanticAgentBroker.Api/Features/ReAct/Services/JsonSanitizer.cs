using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SemanticAgentBroker.Api.Features.ReAct.Services;

/// <summary>
/// Robust JSON repair utility for LLM-generated tool arguments.
/// Handles common malformations from Ollama, vLLM, and other local model providers:
/// - Smart/curly quotes
/// - URL-encoded characters (%22, %27, %0A, etc.)
/// - Unescaped control characters (literal newlines, tabs)
/// - Trailing commas
/// - Missing closing brackets
/// - Unescaped double quotes inside string values
/// 
/// Design: Try fast-path parse first. On failure, apply progressive sanitization
/// and retry. Only returns failure if all repair strategies are exhausted.
/// </summary>
public static class JsonSanitizer
{
    /// <summary>
    /// Attempts to parse the raw JSON string. If parsing fails, applies
    /// progressive sanitization steps and retries.
    /// </summary>
    /// <returns>
    /// SanitizedJson: the repaired JSON string (or original if valid).
    /// WasSanitized: true if any repairs were applied.
    /// Warning: human-readable description of what was repaired (null if clean).
    /// </returns>
    public static (string SanitizedJson, bool WasSanitized, string? Warning) TrySanitize(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return ("{}", true, "Empty input replaced with empty object.");

        var trimmed = rawJson.Trim();
        // Fast path 1: if raw input is already valid JSON as-is, return immediately without any mutations
        if (IsValidJson(trimmed))
            return (trimmed, false, null);

        var warnings = new List<string>();
        var json = trimmed;

        // Step 1: Extract outermost balanced JSON payload if wrapped in conversational prose without code fences
        var before = json;
        json = ExtractJsonPayload(json);
        if (json != before) warnings.Add("Conversational preamble/postamble stripped");

        // Step 2: Strip Markdown code fences if payload is wrapped in ```...```
        before = json;
        json = StripMarkdownCodeFences(json);
        if (json != before) warnings.Add("Markdown code fences stripped");

        // Fast path 2: If extracting payload or stripping fences produced valid JSON, return immediately
        if (IsValidJson(json))
            return (json, warnings.Count > 0, warnings.Count > 0 ? string.Join("; ", warnings) : null);

        // Step 3: Strip comments (//, /* */, and #) outside string literals
        before = json;
        json = StripComments(json);
        if (json != before) warnings.Add("Comments stripped");

        // Step 4: Normalize in-body semicolons and ellipsis truncation markers outside strings
        before = json;
        json = NormalizeSemicolonsAndEllipsis(json);
        if (json != before) warnings.Add("In-body semicolons and ellipsis normalized");

        // Step 5: Normalize Unicode spaces, zero-width chars, and byte-fallback artifacts
        before = json;
        json = NormalizeUnicodeAndByteArtifacts(json);
        if (json != before) warnings.Add("Unicode spaces/artifacts normalized");

        // Step 6: Normalize Python triple-quoted strings ("""...""" and '''...''')
        before = json;
        json = NormalizeTripleQuotedStrings(json);
        if (json != before) warnings.Add("Triple-quoted strings normalized");

        // Step 7: Strip language string prefixes (r"...", @"...", b"...", f"...", $"...", etc.) outside strings
        before = json;
        json = StripStringPrefixes(json);
        if (json != before) warnings.Add("Language string prefixes stripped");

        // Step 8: Normalize JS/TS backtick template strings (`...`) outside double/single quotes
        before = json;
        json = NormalizeBacktickStrings(json);
        if (json != before) warnings.Add("Backtick template strings normalized");

        // Step 9: Normalize Python sets ({val1, val2} -> [val1, val2])
        before = json;
        json = NormalizePythonSets(json);
        if (json != before) warnings.Add("Python sets converted to arrays");

        // Fast path 3: try parsing as-is (after initial cleanup)
        if (IsValidJson(json))
            return (json, warnings.Count > 0, warnings.Count > 0 ? string.Join("; ", warnings) : null);

        // Step 10: Normalize Unicode smart/curly quotes to ASCII
        before = json;
        json = NormalizeQuotes(json);
        if (json != before) warnings.Add("Smart quotes normalized");

        // Step 11: Normalize assignment equals (=) outside strings between keys and values to colon (:)
        before = json;
        json = NormalizeAssignmentEquals(json);
        if (json != before) warnings.Add("Assignment equals normalized to colons");

        // Step 12: Convert Python tuples (...) outside strings into JSON arrays [...]
        before = json;
        json = RepairPythonTuples(json);
        if (json != before) warnings.Add("Python tuples converted to arrays");

        // Step 13: Merge concatenated strings ("str1" + "str2" -> "str1str2") outside strings
        before = json;
        json = MergeConcatenatedStrings(json);
        if (json != before) warnings.Add("Concatenated strings merged");

        // Step 14: Repair single-quoted JSON to standard double-quoted JSON
        before = json;
        json = RepairSingleQuotedJson(json);
        if (json != before) warnings.Add("Single-quoted JSON repaired");

        // Step 15: Normalize Python literals (True, False, None, NaN, Infinity, undefined, TRUE, FALSE, NULL, nil) outside strings
        before = json;
        json = NormalizePythonLiterals(json);
        if (json != before) warnings.Add("Python literals normalized");

        // Step 16: Quote unquoted object keys (JSON5 / JavaScript style bare keys)
        before = json;
        json = QuoteUnquotedKeys(json);
        if (json != before) warnings.Add("Unquoted object keys quoted");

        // Step 17: Normalize numeric underscore separators (1_000_000 -> 1000000) outside strings
        before = json;
        json = NormalizeNumericSeparators(json);
        if (json != before) warnings.Add("Numeric separators normalized");

        // Step 18: Normalize binary (0b...), octal (0o...), and hexadecimal (0x...) numbers outside strings
        before = json;
        json = NormalizeNonDecimalNumbers(json);
        if (json != before) warnings.Add("Non-decimal numbers normalized to decimal");

        // Step 19: Normalize leading and trailing float decimal points (.5 -> 0.5, 5. -> 5.0) outside strings
        before = json;
        json = NormalizeFloatDecimalPoints(json);
        if (json != before) warnings.Add("Float decimal points normalized");

        // Step 20: Normalize leading zeros in integer numbers (007 -> 7, 09 -> 9) outside strings
        before = json;
        json = NormalizeLeadingZeroIntegers(json);
        if (json != before) warnings.Add("Leading zero integers normalized");

        // Step 21: Quote unquoted string values (e.g. {status: active, path: /app/workdir})
        before = json;
        json = QuoteUnquotedValues(json);
        if (json != before) warnings.Add("Unquoted string values quoted");

        // Step 22: Normalize leading plus signs on numbers (+10 -> 10, +.5 -> 0.5)
        before = json;
        json = NormalizeLeadingPlusSigns(json);
        if (json != before) warnings.Add("Leading plus signs removed from numbers");

        // Step 23: Decode URL-encoded characters (%22, %27, %0A, %25, etc.)
        // Only apply if the string contains structurally-breaking % sequences.
        // Skip for harmless %20 etc. that may appear in URLs within valid JSON values.
        before = json;
        if (json.Contains("%22") || json.Contains("%27") || json.Contains("%0A") || json.Contains("%0D") || json.Contains("%0a") || json.Contains("%0d"))
            json = DecodeUrlEncodedCharacters(json);
        if (json != before) warnings.Add("URL-encoded characters decoded");

        // Step 24: Normalize 2-digit hex escapes (\xHH -> \u00HH) in strings
        before = json;
        json = NormalizeHexEscapes(json);
        if (json != before) warnings.Add("Hex escapes normalized");

        // Step 25: Normalize invalid \' escape sequences in double-quoted strings
        before = json;
        json = NormalizeEscapedSingleQuotes(json);
        if (json != before) warnings.Add("Invalid escaped single quotes repaired");

        // Step 26: Normalize invalid escapes and trailing backslashes before closing quotes
        before = json;
        json = NormalizeInvalidEscapesAndTrailingBackslashes(json);
        if (json != before) warnings.Add("Invalid escapes and trailing backslashes repaired");

        // Step 27: Insert missing commas between adjacent properties or array elements
        before = json;
        json = InsertMissingCommas(json);
        if (json != before) warnings.Add("Missing commas inserted");

        // Step 28: Remove/escape control characters inside string values
        before = json;
        json = EscapeControlCharacters(json);
        if (json != before) warnings.Add("Control characters escaped");

        // Step 29: Remove trailing commas before } or ]
        before = json;
        json = RemoveTrailingCommas(json);
        if (json != before) warnings.Add("Trailing commas removed");

        // Step 30: Balance brackets
        before = json;
        json = BalanceBrackets(json);
        if (json != before)
        {
            warnings.Add("Missing brackets added");
            var afterCommaRemoval = RemoveTrailingCommas(json);
            if (afterCommaRemoval != json)
            {
                json = afterCommaRemoval;
                warnings.Add("Trailing commas removed");
            }
        }

        // Check if repairs fixed it
        if (IsValidJson(json))
        {
            return (json, true, string.Join("; ", warnings));
        }

        // Step 31: Try to fix unescaped double quotes inside string values
        before = json;
        json = FixUnescapedQuotesInValues(json);
        if (json != before) warnings.Add("Unescaped quotes in values fixed");

        if (IsValidJson(json))
        {
            return (json, true, string.Join("; ", warnings));
        }

        // Step 32: Last resort - try to extract key-value pairs heuristically
        var extracted = TryExtractKeyValuePairs(json) ?? TryExtractKeyValuePairs(rawJson);
        if (extracted != null && IsValidJson(extracted))
        {
            warnings.Add("Heuristic key-value extraction applied");
            return (extracted, true, string.Join("; ", warnings));
        }

        // All repairs exhausted - return current content with all sanitizations applied
        // The caller will handle the parse error
        return (json, warnings.Count > 0, warnings.Count > 0 ? string.Join("; ", warnings) + " (still invalid)" : null);
    }

    /// <summary>
    /// Validates whether the given string is parseable JSON.
    /// </summary>
    public static bool IsValidJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Replace Unicode smart/curly quotes with their ASCII equivalents.
    /// Handles: " " ' ' (common in Ollama responses).
    /// </summary>
    private static string NormalizeQuotes(string json)
    {
        var sb = new StringBuilder(json.Length);
        foreach (var ch in json)
        {
            sb.Append(ch switch
            {
                '\u201C' or '\u201D' => '"',   // " "
                '\u2018' or '\u2019' => '\'',  // ' '
                '\u00AB' or '\u00BB' => '"',   // « »
                '\u201E' => '"',               // „
                _ => ch
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Merges adjacent double-quoted strings concatenated with '+' outside string literals (e.g. "str1" + "str2" -> "str1str2").
    /// </summary>
    private static string MergeConcatenatedStrings(string json)
    {
        if (!json.Contains('+'))
            return json;

        var sb = new StringBuilder(json.Length);
        int i = 0;

        while (i < json.Length)
        {
            if (json[i] == '"')
            {
                int close1 = FindClosingQuote(json, i + 1);
                if (close1 >= 0)
                {
                    int nextNonWs = FindNextNonWhitespace(json, close1 + 1);
                    if (nextNonWs < json.Length && json[nextNonWs] == '+')
                    {
                        int afterPlus = FindNextNonWhitespace(json, nextNonWs + 1);
                        if (afterPlus < json.Length && json[afterPlus] == '"')
                        {
                            int close2 = FindClosingQuote(json, afterPlus + 1);
                            if (close2 >= 0)
                            {
                                var c1 = json[(i + 1)..close1];
                                var c2 = json[(afterPlus + 1)..close2];
                                sb.Append('"');
                                sb.Append(c1);
                                sb.Append(c2);
                                sb.Append('"');
                                i = close2 + 1;
                                continue;
                            }
                        }
                    }
                }
            }

            sb.Append(json[i]);
            i++;
        }

        var result = sb.ToString();
        if (result.Contains('+') && result != json)
        {
            return MergeConcatenatedStrings(result);
        }

        return result;
    }

    /// <summary>
    /// Normalizes Python boolean and null literals (True -> true, False -> false, None -> null)
    /// when they appear outside string literals.
    /// </summary>
    private static string NormalizePythonLiterals(string json)
    {
        var sb = new StringBuilder(json.Length);
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];

            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                sb.Append(ch);
                if (i + 1 < json.Length) escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                sb.Append(ch);
                continue;
            }

            if (!inString)
            {
                if (MatchesWord(json, i, "True") || MatchesWord(json, i, "TRUE"))
                {
                    sb.Append("true");
                    i += 3;
                    continue;
                }
                if (MatchesWord(json, i, "False") || MatchesWord(json, i, "FALSE"))
                {
                    sb.Append("false");
                    i += 4;
                    continue;
                }
                if (MatchesWord(json, i, "None") || MatchesWord(json, i, "NULL") ||
                    MatchesWord(json, i, "Null") || MatchesWord(json, i, "NONE"))
                {
                    sb.Append("null");
                    i += 3;
                    continue;
                }
                if (MatchesWord(json, i, "nil") || MatchesWord(json, i, "Nil") || MatchesWord(json, i, "NIL"))
                {
                    sb.Append("null");
                    i += 2;
                    continue;
                }
                if (MatchesWord(json, i, "-inf") || MatchesWord(json, i, "+inf"))
                {
                    sb.Append("null");
                    i += 3;
                    continue;
                }
                if (MatchesWord(json, i, "inf"))
                {
                    sb.Append("null");
                    i += 2;
                    continue;
                }
                if (MatchesWord(json, i, "-NaN") || MatchesWord(json, i, "-nan") ||
                    MatchesWord(json, i, "+NaN") || MatchesWord(json, i, "+nan"))
                {
                    sb.Append("null");
                    i += 3;
                    continue;
                }
                if (MatchesWord(json, i, "NaN") || MatchesWord(json, i, "nan"))
                {
                    sb.Append("null");
                    i += 2;
                    continue;
                }
                if (MatchesWord(json, i, "-Infinity") || MatchesWord(json, i, "-infinity"))
                {
                    sb.Append("null");
                    i += 8;
                    continue;
                }
                if (MatchesWord(json, i, "+Infinity") || MatchesWord(json, i, "+infinity"))
                {
                    sb.Append("null");
                    i += 8;
                    continue;
                }
                if (MatchesWord(json, i, "Infinity") || MatchesWord(json, i, "infinity"))
                {
                    sb.Append("null");
                    i += 7;
                    continue;
                }
                if (MatchesWord(json, i, "undefined"))
                {
                    sb.Append("null");
                    i += 8;
                    continue;
                }
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static bool MatchesWord(string text, int index, string word)
    {
        if (index + word.Length > text.Length) return false;
        if (!text.AsSpan(index, word.Length).SequenceEqual(word)) return false;

        // Check boundary before: must not be preceded by letter, digit, or underscore
        if (index > 0 && (char.IsLetterOrDigit(text[index - 1]) || text[index - 1] == '_')) return false;

        // Check boundary after: must not be followed by letter, digit, or underscore
        int nextIndex = index + word.Length;
        if (nextIndex < text.Length && (char.IsLetterOrDigit(text[nextIndex]) || text[nextIndex] == '_')) return false;

        return true;
    }

    /// <summary>
    /// Replaces invalid JSON escape sequences \' inside double-quoted string literals with '.
    /// In RFC 8259, single quotes must not be escaped in JSON.
    /// </summary>
    private static string NormalizeEscapedSingleQuotes(string json)
    {
        if (!json.Contains("\\'")) return json;

        var sb = new StringBuilder(json.Length);
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];

            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                if (i + 1 < json.Length && json[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i++;
                    continue;
                }

                sb.Append(ch);
                if (i + 1 < json.Length) escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Decodes URL-encoded sequences that Ollama models sometimes produce.
    /// Only decodes sequences that are safe in JSON context.
    /// Handles %22 ("), %27 ('), %0A (newline→\n), %0D (CR), %09 (tab), %25 (%), %20 (space), etc.
    /// </summary>
    private static string DecodeUrlEncodedCharacters(string json)
    {
        if (!json.Contains('%'))
            return json;

        // Use regex to find %XX patterns and decode them selectively
        return Regex.Replace(json, @"%([0-9A-Fa-f]{2})", match =>
        {
            var hex = match.Groups[1].Value;
            var charValue = Convert.ToInt32(hex, 16);

            return charValue switch
            {
                0x22 => "\"",    // %22 → double quote (subsequent steps handle interior quote escaping if inside string)
                0x27 => "'",     // %27 → single quote
                0x0A => "\\n",   // %0A → escaped newline  
                0x0D => "\\r",   // %0D → escaped CR
                0x09 => "\\t",   // %09 → escaped tab
                0x25 => "%",     // %25 → literal percent sign
                0x20 => " ",     // %20 → space
                0x5C => "\\\\",  // %5C → escaped backslash
                _ when charValue >= 0x20 && charValue < 0x7F => ((char)charValue).ToString(),
                _ => match.Value // Leave unrecognized sequences as-is
            };
        });
    }

    /// <summary>
    /// Escapes literal control characters that appear inside JSON strings.
    /// Walks the string respecting JSON string boundaries (between unescaped quotes).
    /// Includes a structural look-ahead heuristic to handle partially broken JSON
    /// where unescaped interior quotes would otherwise desync string tracking.
    /// </summary>
    private static string EscapeControlCharacters(string json)
    {
        var sb = new StringBuilder(json.Length);
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            var ch = json[i];

            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                if (i + 1 < json.Length)
                {
                    sb.Append(ch);
                    escaped = true;
                    continue;
                }
                else
                {
                    // Trailing backslash with no following character - append as-is
                    sb.Append(ch);
                    break;
                }
            }

            if (ch == '"')
            {
                if (inString)
                {
                    // Look-ahead: check if the next non-whitespace character is structural.
                    // If not, this is likely an unescaped interior quote - escape it
                    // instead of toggling out of the string.
                    int next = i + 1;
                    bool hasNewline = false;
                    while (next < json.Length && char.IsWhiteSpace(json[next]))
                    {
                        if (json[next] == '\n' || json[next] == '\r') hasNewline = true;
                        next++;
                    }
                    bool looksStructural = next >= json.Length ||
                        json[next] is '}' or ']' or ':';

                    if (!looksStructural && json[next] == ',')
                    {
                        int afterComma = FindNextNonWhitespace(json, next + 1);
                        if (afterComma >= json.Length || json[afterComma] is '}' or ']' || LooksLikePropertyKey(json, afterComma) || LooksLikeArrayElement(json, afterComma))
                        {
                            looksStructural = true;
                        }
                    }

                    if (!looksStructural && hasNewline && (LooksLikePropertyKey(json, next) || LooksLikeArrayElement(json, next)))
                    {
                        looksStructural = true;
                    }

                    if (!looksStructural)
                    {
                        sb.Append("\\\"");
                        continue;
                    }
                }

                inString = !inString;
                sb.Append(ch);
                continue;
            }

            if (inString)
            {
                // Replace control characters with their escape sequences
                switch (ch)
                {
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (char.IsControl(ch))
                            sb.Append($"\\u{(int)ch:X4}");
                        else
                            sb.Append(ch);
                        break;
                }
            }
            else
            {
                // Outside strings: skip stray control chars (whitespace is fine)
                if (char.IsControl(ch) && ch != '\n' && ch != '\r' && ch != '\t')
                    continue;
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Removes trailing commas before closing braces/brackets outside string literals.
    /// E.g., {"a": 1,} → {"a": 1}, but preserves {"pattern": "[a,]"}.
    /// </summary>
    private static string RemoveTrailingCommas(string json)
    {
        var sb = new StringBuilder(json.Length);
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];

            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                sb.Append(ch);
                if (i + 1 < json.Length) escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                sb.Append(ch);
                continue;
            }

            if (!inString && ch == ',')
            {
                // Look ahead past whitespace to see if next char is } or ] or another comma
                int next = i + 1;
                while (next < json.Length && char.IsWhiteSpace(json[next]))
                    next++;

                if (next < json.Length && (json[next] == '}' || json[next] == ']' || json[next] == ','))
                {
                    // Skip trailing or doubled comma!
                    continue;
                }
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Counts unmatched opening brackets in LIFO order and appends missing closers,
    /// closing any unclosed string literals first, repairing nested unclosed containers, and trimming trailing commas outside strings.
    /// </summary>
    private static string BalanceBrackets(string json)
    {
        var delimiterStack = new Stack<char>();
        var sb = new StringBuilder(json.Length + 16);
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (escaped) { sb.Append(ch); escaped = false; continue; }
            if (ch == '\\' && inString) { sb.Append(ch); if (i + 1 < json.Length) escaped = true; continue; }
            if (ch == '"') { inString = !inString; sb.Append(ch); continue; }

            if (inString)
            {
                sb.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '{':
                    delimiterStack.Push('}');
                    sb.Append(ch);
                    break;
                case '[':
                    delimiterStack.Push(']');
                    sb.Append(ch);
                    break;
                case '}':
                    if (delimiterStack.Count > 0 && delimiterStack.Peek() == '}')
                    {
                        delimiterStack.Pop();
                        sb.Append(ch);
                    }
                    else if (delimiterStack.Contains('}'))
                    {
                        // Unclosed nested container(s) before closing brace: close them first!
                        while (delimiterStack.Count > 0 && delimiterStack.Peek() != '}')
                        {
                            sb.Append(delimiterStack.Pop());
                        }
                        if (delimiterStack.Count > 0 && delimiterStack.Peek() == '}')
                        {
                            delimiterStack.Pop();
                        }
                        sb.Append(ch);
                    }
                    else if (delimiterStack.Count > 0 && delimiterStack.Peek() == ']')
                    {
                        // Mismatched closing delimiter: model typed } instead of ]
                        sb.Append(delimiterStack.Pop());
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
                case ']':
                    if (delimiterStack.Count > 0 && delimiterStack.Peek() == ']')
                    {
                        delimiterStack.Pop();
                        sb.Append(ch);
                    }
                    else if (delimiterStack.Contains(']'))
                    {
                        // Unclosed nested container(s) before closing bracket: close them first!
                        while (delimiterStack.Count > 0 && delimiterStack.Peek() != ']')
                        {
                            sb.Append(delimiterStack.Pop());
                        }
                        if (delimiterStack.Count > 0 && delimiterStack.Peek() == ']')
                        {
                            delimiterStack.Pop();
                        }
                        sb.Append(ch);
                    }
                    else if (delimiterStack.Count > 0 && delimiterStack.Peek() == '}')
                    {
                        // Mismatched closing delimiter: model typed ] instead of }
                        sb.Append(delimiterStack.Pop());
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        if (inString)
        {
            if (escaped) sb.Append('\\');
            sb.Append('"');
        }
        else
        {
            // If outside string, trim any trailing comma and whitespace before appending closing delimiters
            int end = sb.Length - 1;
            while (end >= 0 && char.IsWhiteSpace(sb[end])) end--;
            if (end >= 0 && sb[end] == ',')
            {
                sb.Length = end; // remove the trailing comma and trailing whitespace
            }
        }

        while (delimiterStack.Count > 0)
        {
            sb.Append(delimiterStack.Pop());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Attempts to fix unescaped double quotes inside JSON string values.
    /// Strategy: Distinguishes keys from values. A value string can only be
    /// terminated by a comma (,), closing brace (}), closing bracket (]), or EOF.
    /// A colon (:) NEVER terminates a JSON value string.
    /// </summary>
    private static string FixUnescapedQuotesInValues(string json)
    {
        var trimmed = json.Trim();

        // Fast-path for single-key objects with string value (e.g. {"script": "...", "content": "..."})
        var singleKeyMatch = Regex.Match(trimmed, @"^\s*\{\s*""([^""\\]+)""\s*:\s*""(.*)""\s*\}\s*$", RegexOptions.Singleline);
        if (singleKeyMatch.Success)
        {
            var key = singleKeyMatch.Groups[1].Value;
            var innerContent = singleKeyMatch.Groups[2].Value;

            // Ensure this is genuinely a single-key object and not multiple keys swallowed by greedy match
            bool hasSubsequentKeys = Regex.IsMatch(innerContent, @"(?:^|[^\\])""\s*,\s*""[^""]+""\s*:");
            if (!hasSubsequentKeys)
            {
                var repairedContent = EscapeUnescapedDoubleQuotes(innerContent);
                var candidate = $"{{\"{key}\": \"{repairedContent}\"}}";
                if (IsValidJson(candidate))
                    return candidate;
            }
        }


        var sb = new StringBuilder(json.Length + 50);
        int i = 0;

        while (i < json.Length)
        {
            var ch = json[i];

            if (ch == '"')
            {
                // Find closing quote of prospective key
                int closeQuote = FindClosingQuote(json, i + 1);
                if (closeQuote >= 0)
                {
                    int afterClose = FindNextNonWhitespace(json, closeQuote + 1);
                    if (afterClose < json.Length && json[afterClose] == ':')
                    {
                        // This is a key - copy key and colon
                        sb.Append(json, i, (afterClose - i) + 1);
                        i = afterClose + 1;

                        // Now check if value is a string
                        int valueStart = FindNextNonWhitespace(json, i);
                        if (valueStart < json.Length && json[valueStart] == '"')
                        {
                            sb.Append(json, i, valueStart - i); // whitespace
                            sb.Append('"');
                            i = valueStart + 1;

                            // Process value content until legitimate closing quote
                            while (i < json.Length)
                            {
                                var vCh = json[i];
                                if (vCh == '\\' && i + 1 < json.Length)
                                {
                                    if (json[i + 1] == '"')
                                    {
                                        int nextNonWs = FindNextNonWhitespace(json, i + 2);
                                        bool isClosing = false;
                                        if (nextNonWs >= json.Length || json[nextNonWs] is '}' or ']')
                                        {
                                             isClosing = true;
                                        }
                                        else if (json[nextNonWs] == ',')
                                        {
                                             int afterComma = FindNextNonWhitespace(json, nextNonWs + 1);
                                             if (afterComma >= json.Length || json[afterComma] == '}' || json[afterComma] == ']' || LooksLikePropertyKey(json, afterComma) || LooksLikeArrayElement(json, afterComma))
                                             {
                                                 isClosing = true;
                                             }
                                        }

                                        if (isClosing)
                                        {
                                            sb.Append("\\\\");
                                            sb.Append('"');
                                            i += 2;
                                            break;
                                        }
                                    }

                                    char next = json[i + 1];
                                    if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                                    {
                                        sb.Append(vCh);
                                        sb.Append(next);
                                        i += 2;
                                        continue;
                                    }
                                    if (next == 'u')
                                    {
                                        if (i + 5 < json.Length &&
                                            IsHexDigit(json[i + 2]) &&
                                            IsHexDigit(json[i + 3]) &&
                                            IsHexDigit(json[i + 4]) &&
                                            IsHexDigit(json[i + 5]))
                                        {
                                            sb.Append(vCh);
                                            sb.Append(next);
                                            i += 2;
                                            continue;
                                        }

                                        sb.Append("\\\\");
                                        sb.Append('u');
                                        i += 2;
                                        continue;
                                    }
                                    sb.Append("\\\\");
                                    sb.Append(next);
                                    i += 2;
                                    continue;
                                }

                                if (vCh == '"')
                                {
                                    int nextNonWs = i + 1;
                                    bool hasNewline = false;
                                    while (nextNonWs < json.Length && char.IsWhiteSpace(json[nextNonWs]))
                                    {
                                        if (json[nextNonWs] == '\n' || json[nextNonWs] == '\r') hasNewline = true;
                                        nextNonWs++;
                                    }

                                    // For a JSON string VALUE, only comma (followed by another property key or '}'),
                                    // newline (followed by another property key), closing brace, closing bracket, or EOF are valid closing delimiters.
                                    // A colon is NEVER a delimiter after a value!
                                    bool isClosing = false;
                                    if (nextNonWs >= json.Length || json[nextNonWs] is '}' or ']')
                                    {
                                        isClosing = true;
                                    }
                                    else if (json[nextNonWs] == ',')
                                    {
                                        int afterComma = FindNextNonWhitespace(json, nextNonWs + 1);
                                        if (afterComma >= json.Length || json[afterComma] == '}' || json[afterComma] == ']' || LooksLikePropertyKey(json, afterComma) || LooksLikeArrayElement(json, afterComma))
                                        {
                                            isClosing = true;
                                        }
                                    }
                                    else if (hasNewline && (LooksLikePropertyKey(json, nextNonWs) || LooksLikeArrayElement(json, nextNonWs)))
                                    {
                                        isClosing = true;
                                    }

                                    if (isClosing)
                                    {
                                        sb.Append('"');
                                        i++;
                                        break;
                                    }
                                    else
                                    {
                                        // Inner unescaped quote inside string value
                                        sb.Append("\\\"");
                                        i++;
                                        continue;
                                    }
                                }

                                sb.Append(vCh);
                                i++;
                            }
                            continue;
                        }
                        else
                        {
                            // Non-string value (number, bool, object, array)
                            continue;
                        }
                    }
                }

                // Standalone quote outside a key-value context
                sb.Append(ch);
                i++;
            }
            else
            {
                sb.Append(ch);
                i++;
            }
        }

        return sb.ToString();
    }

    private static string EscapeUnescapedDoubleQuotes(string content)
    {
        var sb = new StringBuilder(content.Length + 20);
        for (int i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (ch == '\\' && i + 1 < content.Length)
            {
                char next = content[i + 1];
                if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                {
                    sb.Append(ch);
                    sb.Append(next);
                    i++;
                    continue;
                }
                if (next == 'u')
                {
                    if (i + 5 < content.Length &&
                        IsHexDigit(content[i + 2]) &&
                        IsHexDigit(content[i + 3]) &&
                        IsHexDigit(content[i + 4]) &&
                        IsHexDigit(content[i + 5]))
                    {
                        sb.Append(ch);
                        sb.Append(next);
                        i++;
                        continue;
                    }

                    sb.Append("\\\\");
                    sb.Append('u');
                    i++;
                    continue;
                }
                sb.Append("\\\\");
                sb.Append(next);
                i++;
                continue;
            }
            if (ch == '"')
            {
                sb.Append("\\\"");
            }
            else
            {
                sb.Append(ch);
            }
        }

        int backslashCount = 0;
        for (int j = sb.Length - 1; j >= 0 && sb[j] == '\\'; j--)
        {
            backslashCount++;
        }
        if (backslashCount % 2 != 0)
        {
            sb.Append('\\');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Last-resort: try to extract key-value pairs from malformed JSON using regex.
    /// Handles the common pattern of key: value or "key": "value".
    /// </summary>
    private static string? TryExtractKeyValuePairs(string raw)
    {
        // Try to find patterns like "key": "value", "key": value, key: "value", or key: value
        var matches = Regex.Matches(raw, @"(?:""([^""\r\n\t]+)""|(\b[\w.\-_]+\b))\s*:\s*(?:""((?:[^""\\]|\\.)*)""|([\w\d.+\-]+)|\{([^}]*)\}|\[([^\]]*)\])");

        if (matches.Count == 0)
            return null;

        var pairs = new Dictionary<string, string>();
        foreach (Match match in matches)
        {
            var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (match.Groups[3].Success)
                pairs[key] = $"\"{EscapeJsonString(match.Groups[3].Value)}\"";
            else if (match.Groups[4].Success)
                pairs[key] = IsJsonPrimitive(match.Groups[4].Value) ? match.Groups[4].Value : $"\"{EscapeJsonString(match.Groups[4].Value)}\"";
            else if (match.Groups[5].Success)
                pairs[key] = $"{{{match.Groups[5].Value}}}";
            else if (match.Groups[6].Success)
                pairs[key] = $"[{match.Groups[6].Value}]";
        }

        if (pairs.Count == 0)
            return null;

        return "{" + string.Join(", ", pairs.Select(p => $"\"{p.Key}\": {p.Value}")) + "}";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int FindClosingQuote(string json, int start)
    {
        for (int i = start; i < json.Length; i++)
        {
            if (json[i] == '\\') { i++; continue; } // skip escaped char
            if (json[i] == '"') return i;
        }
        return -1;
    }

    private static int FindNextNonWhitespace(string json, int start)
    {
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;
        return start;
    }

    private static bool LooksLikePropertyKey(string json, int start)
    {
        if (start >= json.Length) return false;

        // Double-quoted key: "key"\s*:
        if (json[start] == '"')
        {
            int closeQuote = FindClosingQuote(json, start + 1);
            if (closeQuote >= 0)
            {
                int afterKey = FindNextNonWhitespace(json, closeQuote + 1);
                return afterKey < json.Length && json[afterKey] == ':';
            }
            return false;
        }

        // Single-quoted key: 'key'\s*:
        if (json[start] == '\'')
        {
            for (int k = start + 1; k < json.Length; k++)
            {
                if (json[k] == '\\') { k++; continue; }
                if (json[k] == '\'')
                {
                    int afterKey = FindNextNonWhitespace(json, k + 1);
                    return afterKey < json.Length && json[afterKey] == ':';
                }
            }
            return false;
        }

        // Bare identifier key: key\s*:
        if (char.IsLetter(json[start]) || json[start] == '_' || json[start] == '$' || json[start] == '@')
        {
            int k = start;
            while (k < json.Length && (char.IsLetterOrDigit(json[k]) || json[k] == '_' || json[k] == '$' || json[k] == '-' || json[k] == '.' || json[k] == '@'))
            {
                k++;
            }
            int afterKey = FindNextNonWhitespace(json, k);
            return afterKey < json.Length && json[afterKey] == ':';
        }

        return false;
    }

    private static bool LooksLikeArrayElement(string json, int start)
    {
        if (start >= json.Length) return false;
        char ch = json[start];
        if (ch is '{' or '[' or ']') return true;
        if (char.IsDigit(ch) || ch == '-' || ch == '+') return true;
        if (MatchesWord(json, start, "true") || MatchesWord(json, start, "false") || MatchesWord(json, start, "null")) return true;

        if (ch == '"')
        {
            int closeQuote = FindClosingQuote(json, start + 1);
            if (closeQuote >= 0)
            {
                int afterClose = FindNextNonWhitespace(json, closeQuote + 1);
                if (afterClose < json.Length)
                {
                    if (json[afterClose] is ',' or ']' or '}') return true;
                    for (int k = closeQuote + 1; k < afterClose; k++)
                    {
                        if (json[k] == '\n' || json[k] == '\r') return true;
                    }
                    return false;
                }
                return true;
            }
            return false;
        }

        if (ch == '\'')
        {
            for (int k = start + 1; k < json.Length; k++)
            {
                if (json[k] == '\\') { k++; continue; }
                if (json[k] == '\'')
                {
                    int afterClose = FindNextNonWhitespace(json, k + 1);
                    if (afterClose < json.Length)
                    {
                        if (json[afterClose] is ',' or ']' or '}') return true;
                        for (int m = k + 1; m < afterClose; m++)
                        {
                            if (json[m] == '\n' || json[m] == '\r') return true;
                        }
                        return false;
                    }
                    return true;
                }
            }
            return false;
        }

        return false;
    }

    private static bool IsJsonPrimitive(string value) =>
        value is "true" or "false" or "null" ||
        double.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    private static string EscapeJsonString(string value)
    {
        // If the value already contains JSON escape sequences, do not double-escape.
        // This is called from TryExtractKeyValuePairs on regex captures from malformed JSON.
        if (value.Contains("\\\\") || value.Contains("\\\""))
            return value;  // Already escaped - pass through

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    /// <summary>
    /// Converts a single-quoted JSON (or mixed JSON) into standard double-quoted JSON.
    /// Safely escapes any double quotes that appear inside single-quoted strings.
    /// </summary>
    public static string RepairSingleQuotedJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;

        var sb = new StringBuilder(json.Length);
        bool inDoubleString = false;
        bool inSingleString = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];

            if (escaped)
            {
                if (inSingleString && ch == '\'')
                {
                    // Escaped single quote inside a single-quoted string.
                    // In a double-quoted string, single quotes do not need escaping.
                    sb.Append('\'');
                }
                else
                {
                    // Copy escape char as-is
                    sb.Append(ch);
                }
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                if (inSingleString && i + 1 < json.Length && json[i + 1] == '\'')
                {
                    // Escaped single quote inside a single-quoted string: \' -> '
                    // In a double-quoted string, single quotes do not need escaping.
                    // Skip the backslash and append the single quote directly.
                    sb.Append('\'');
                    i++;
                    continue;
                }

                if (inDoubleString && i + 1 < json.Length && json[i + 1] == '\'')
                {
                    // Invalid JSON escape \' inside a double-quoted string:
                    // In RFC 8259, single quotes must not be escaped in JSON.
                    sb.Append('\'');
                    i++;
                    continue;
                }

                sb.Append(ch);
                if (i + 1 < json.Length)
                {
                    escaped = true;
                }
                // If trailing backslash, don't set escaped - loop will end naturally
                continue;
            }

            if (inDoubleString)
            {
                if (ch == '"')
                {
                    inDoubleString = false;
                }
                sb.Append(ch);
                continue;
            }

            if (inSingleString)
            {
                if (ch == '\'')
                {
                    // Contraction / possessive apostrophe within a word (e.g. it's, don't, user's)
                    if (i > 0 && char.IsLetter(json[i - 1]) && i + 1 < json.Length && char.IsLetter(json[i + 1]))
                    {
                        sb.Append('\'');
                        continue;
                    }

                    inSingleString = false;
                    sb.Append('"'); // convert closing single quote to double quote
                }
                else if (ch == '"')
                {
                    // Double quote inside a single-quoted string being converted
                    // to double-quoted; this double quote must be escaped.
                    sb.Append("\\\"");
                }
                else
                {
                    sb.Append(ch);
                }
                continue;
            }

            // Outside any string
            if (ch == '"')
            {
                inDoubleString = true;
                sb.Append(ch);
            }
            else if (ch == '\'')
            {
                inSingleString = true;
                sb.Append('"'); // convert opening single quote to double quote
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Normalizes Unicode whitespace (non-breaking spaces, thin spaces, ideographic spaces),
    /// removes invisible zero-width characters, repairs broken UTF-8 byte decode anomalies (e.g. \u00C2\u00A0),
    /// and fixes byte-fallback tokens (such as &lt;0xC2&gt;dir -> workdir).
    /// </summary>
    public static string NormalizeUnicodeAndByteArtifacts(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // 1. Fix corrupted workspace paths produced by tokenizer byte-fallback (e.g. /app/<0xC2>dir -> /app/workdir)
        text = Regex.Replace(text, @"(?i)/app/<0x[0-9a-f]{2}>dir", "/app/workdir");
        text = Regex.Replace(text, @"(?i)/app/\u00C2dir", "/app/workdir");

        // 2. Normalize literal JSON unicode escape sequences for non-breaking and unusual spaces
        text = Regex.Replace(text, @"(?i)\\u00a0|\\u202f|\\u200[0-9ab]|\\u3000|\\u1680", " ");
        text = Regex.Replace(text, @"(?i)\\u200[cd]|\\ufeff|\\u2060|\\u00ad", "");

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];

            switch (ch)
            {
                // Non-breaking and unusual spaces -> standard ASCII space
                case '\u00A0': // NO-BREAK SPACE (NBSP)
                case '\u202F': // NARROW NO-BREAK SPACE
                case '\u2007': // FIGURE SPACE
                case '\u2008': // PUNCTUATION SPACE
                case '\u2009': // THIN SPACE
                case '\u200A': // HAIR SPACE
                case '\u3000': // IDEOGRAPHIC SPACE
                case '\u1680': // OGHAM SPACE MARK
                case '\u2000': // EN QUAD
                case '\u2001': // EM QUAD
                case '\u2002': // EN SPACE
                case '\u2003': // EM SPACE
                case '\u2004': // THREE-PER-EM SPACE
                case '\u2005': // FOUR-PER-EM SPACE
                case '\u2006': // SIX-PER-EM SPACE
                case '\u200B': // ZERO WIDTH SPACE (when acting as word separator)
                    sb.Append(' ');
                    break;

                // Zero-width and formatting invisible characters -> strip
                case '\u200C': // ZERO WIDTH NON-JOINER
                case '\u200D': // ZERO WIDTH JOINER
                case '\uFEFF': // ZERO WIDTH NO-BREAK SPACE (BOM)
                case '\u2060': // WORD JOINER
                case '\u00AD': // SOFT HYPHEN
                    break;

                // Corrupted UTF-8 sequence: \u00C2 preceding an NBSP or space
                case '\u00C2' when i + 1 < text.Length && (text[i + 1] == '\u00A0' || text[i + 1] == ' '):
                    // Skip \u00C2 and let the following space or NBSP be handled
                    break;

                default:
                    sb.Append(ch);
                    break;
            }
        }

        var result = sb.ToString();

        // Clean up remaining standalone <0xXX> byte-fallback tokens in general text
        if (result.Contains("<0x"))
        {
            result = Regex.Replace(result, @"<0x[0-9a-fA-F]{2}>", " ");
        }

        return result;
    }

    /// <summary>
    /// Strips markdown code fences (```json ... ``` or ``` ... ```) wrapping JSON payloads.
    /// </summary>
    private static string StripMarkdownCodeFences(string json)
    {
        var trimmed = json.Trim();
        if (!trimmed.Contains("```"))
            return json;

        var match = Regex.Match(trimmed, @"```(?:[a-zA-Z0-9_-]+)?\s*\r?\n?(.*?)\r?\n?```", RegexOptions.Singleline);
        if (match.Success)
        {
            var candidate = match.Groups[1].Value.Trim();
            if (candidate.StartsWith('{') || candidate.StartsWith('[') || (trimmed.StartsWith("```") && trimmed.EndsWith("```")))
            {
                return candidate;
            }
        }
        return json;
    }

    /// <summary>
    /// Strips single-line (// ...) and multi-line (/* ... */) comments outside string literals,
    /// preserving // inside URLs such as https://.
    /// </summary>
    private static string StripComments(string json)
    {
        if (!json.Contains("//") && !json.Contains("/*") && !json.Contains("#"))
            return json;

        var sb = new StringBuilder(json.Length);
        bool inDoubleQuote = false;
        bool inSingleQuote = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];

            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\' && (inDoubleQuote || inSingleQuote))
            {
                sb.Append(ch);
                if (i + 1 < json.Length)
                {
                    escaped = true;
                }
                continue;
            }

            if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                sb.Append(ch);
                continue;
            }

            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                sb.Append(ch);
                continue;
            }

            if (!inDoubleQuote && !inSingleQuote)
            {
                // Single-line comment: //
                if (ch == '/' && i + 1 < json.Length && json[i + 1] == '/')
                {
                    bool isUrl = (i >= 5 && json[(i - 5)..i].Equals("http:", StringComparison.OrdinalIgnoreCase)) ||
                                 (i >= 6 && json[(i - 6)..i].Equals("https:", StringComparison.OrdinalIgnoreCase));

                    if (!isUrl)
                    {
                        i += 2;
                        while (i < json.Length && json[i] != '\n' && json[i] != '\r')
                        {
                            i++;
                        }
                        if (i < json.Length)
                        {
                            sb.Append(json[i]);
                        }
                        continue;
                    }
                }

                // Hash comment: # ... (Python, YAML, shell style)
                if (ch == '#')
                {
                    i++;
                    while (i < json.Length && json[i] != '\n' && json[i] != '\r')
                    {
                        i++;
                    }
                    if (i < json.Length)
                    {
                        sb.Append(json[i]);
                    }
                    continue;
                }

                // Multi-line comment: /* ... */
                if (ch == '/' && i + 1 < json.Length && json[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < json.Length && !(json[i] == '*' && json[i + 1] == '/'))
                    {
                        i++;
                    }
                    if (i + 1 < json.Length && json[i] == '*' && json[i + 1] == '/')
                    {
                        i++; // skip '*' (loop increment will skip '/')
                    }
                    else
                    {
                        i = json.Length;
                    }
                    continue;
                }
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Normalizes semicolons outside strings (converting in-body ';' to ',' and trimming trailing ';')
    /// and strips LLM truncation/placeholder ellipsis markers ('...' and '…') outside strings.
    /// </summary>
    private static string NormalizeSemicolonsAndEllipsis(string json)
    {
        if (!json.Contains(';') && !json.Contains("...") && !json.Contains('…'))
            return json;

        var sb = new StringBuilder(json.Length);
        bool inDoubleQuote = false;
        bool inSingleQuote = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (escaped) { sb.Append(ch); escaped = false; continue; }
            if (ch == '\\' && (inDoubleQuote || inSingleQuote)) { sb.Append(ch); if (i + 1 < json.Length) escaped = true; continue; }
            if (ch == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; sb.Append(ch); continue; }
            if (ch == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; sb.Append(ch); continue; }

            if (!inDoubleQuote && !inSingleQuote)
            {
                // Ellipsis: ... (3 dots) outside strings
                if (ch == '.' && i + 2 < json.Length && json[i + 1] == '.' && json[i + 2] == '.')
                {
                    TrimTrailingCommaAndWhitespace(sb);
                    i += 2; // skip remaining dots (loop increment advances past last dot)
                    continue;
                }

                // Ellipsis: … (single unicode char) outside strings
                if (ch == '…')
                {
                    TrimTrailingCommaAndWhitespace(sb);
                    continue;
                }

                // Semicolon outside strings
                if (ch == ';')
                {
                    // Check if followed by closing delimiter } or ]
                    int next = FindNextNonWhitespace(json, i + 1);
                    if (next >= json.Length || json[next] is '}' or ']')
                    {
                        // Trailing semicolon before closing container or EOF: skip!
                        continue;
                    }
                    else
                    {
                        // In-body semicolon separating properties: convert to comma!
                        sb.Append(',');
                        continue;
                    }
                }
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static void TrimTrailingCommaAndWhitespace(StringBuilder sb)
    {
        int k = sb.Length - 1;
        while (k >= 0 && char.IsWhiteSpace(sb[k])) k--;
        if (k >= 0 && sb[k] == ',') k--;
        while (k >= 0 && char.IsWhiteSpace(sb[k])) k--;
        sb.Length = k + 1;
    }

    /// <summary>
    /// Quotes unquoted JavaScript / JSON5 object keys outside string literals.
    /// E.g. {action: "run", params: {id: 123}} -> {"action": "run", "params": {"id": 123}}.
    /// </summary>
    private static string QuoteUnquotedKeys(string json)
    {
        var sb = new StringBuilder(json.Length + 32);
        bool inString = false;
        bool escaped = false;
        int segStart = 0;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                if (i + 1 < json.Length) escaped = true;
                continue;
            }

            if (ch == '"')
            {
                if (!inString)
                {
                    var segment = json[segStart..i];
                    sb.Append(QuoteUnquotedKeysInSegment(segment));
                    segStart = i;
                    inString = true;
                }
                else
                {
                    sb.Append(json, segStart, (i - segStart) + 1);
                    segStart = i + 1;
                    inString = false;
                }
            }
        }

        if (segStart < json.Length)
        {
            if (inString)
            {
                sb.Append(json, segStart, json.Length - segStart);
            }
            else
            {
                var segment = json[segStart..];
                sb.Append(QuoteUnquotedKeysInSegment(segment));
            }
        }

        return sb.ToString();
    }

    private static string QuoteUnquotedKeysInSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return segment;
        return Regex.Replace(segment, @"(?<=(?:^|[{,]|[\r\n])\s*)([@a-zA-Z0-9_$\-]+(?:\.[@a-zA-Z0-9_$\-]+)*)\s*:(?!\/\/)", "\"$1\":");
    }

    /// <summary>
    /// Normalizes invalid JSON escape sequences (such as Windows path backslashes \Users)
    /// and repairs unescaped trailing backslashes immediately before closing quotes (e.g. "path\": \"C:\test\"} -> "path\": \"C:\test\\\"}).
    /// </summary>
    private static string NormalizeInvalidEscapesAndTrailingBackslashes(string json)
    {
        var sb = new StringBuilder(json.Length + 16);
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];

            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                sb.Append(ch);
                continue;
            }

            if (inString && ch == '\\')
            {
                if (i + 1 < json.Length)
                {
                    char next = json[i + 1];

                    // Check if this backslash precedes a quote that appears to be structural closing quote
                    if (next == '"')
                    {
                        int afterQuote = FindNextNonWhitespace(json, i + 2);
                        if (afterQuote >= json.Length || json[afterQuote] is ',' or '}' or ']')
                        {
                            // Trailing backslash before closing quote:
                            // Model wrote \"} intending literal \ then closing quote.
                            sb.Append("\\\\"); // escape the backslash
                            sb.Append('"');   // closing quote
                            inString = false; // string is now closed!
                            i++;              // skip the quote
                            continue;
                        }

                        // Legitimate escaped quote inside string
                        sb.Append('\\');
                        sb.Append('"');
                        i++;
                        continue;
                    }

                    // Check if next is a valid JSON escape char: \, /, b, f, n, r, t
                    if (next is '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                    {
                        sb.Append('\\');
                        sb.Append(next);
                        i++;
                        continue;
                    }

                    if (next == 'u')
                    {
                        if (i + 5 < json.Length &&
                            IsHexDigit(json[i + 2]) &&
                            IsHexDigit(json[i + 3]) &&
                            IsHexDigit(json[i + 4]) &&
                            IsHexDigit(json[i + 5]))
                        {
                            sb.Append('\\');
                            sb.Append('u');
                            i++;
                            continue;
                        }

                        // Invalid Unicode escape (e.g. \users, \utils) -> escape the backslash!
                        sb.Append("\\\\");
                        sb.Append('u');
                        i++;
                        continue;
                    }

                    // Otherwise, invalid escape character (e.g. \Users, \path) -> escape the backslash
                    sb.Append("\\\\");
                    sb.Append(next);
                    i++;
                    continue;
                }
                else
                {
                    // Backslash at end of entire input
                    sb.Append("\\\\");
                    continue;
                }
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>
    /// Normalizes Python multiline triple-quoted strings ("""...""" and '''...''') into standard double-quoted JSON strings.
    /// </summary>
    private static string NormalizeTripleQuotedStrings(string json)
    {
        if (!json.Contains("\"\"\"") && !json.Contains("'''"))
            return json;

        var sb = new StringBuilder(json.Length);
        int i = 0;

        while (i < json.Length)
        {
            if (i + 2 < json.Length && json[i] == '"' && json[i + 1] == '"' && json[i + 2] == '"')
            {
                // Opening """
                int closeIdx = json.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                if (closeIdx >= 0)
                {
                    var content = json[(i + 3)..closeIdx];
                    var escapedContent = EscapeUnescapedDoubleQuotes(content);
                    sb.Append('"');
                    sb.Append(escapedContent);
                    sb.Append('"');
                    i = closeIdx + 3;
                    continue;
                }
            }
            else if (i + 2 < json.Length && json[i] == '\'' && json[i + 1] == '\'' && json[i + 2] == '\'')
            {
                // Opening '''
                int closeIdx = json.IndexOf("'''", i + 3, StringComparison.Ordinal);
                if (closeIdx >= 0)
                {
                    var content = json[(i + 3)..closeIdx];
                    var escapedContent = EscapeUnescapedDoubleQuotes(content);
                    sb.Append('"');
                    sb.Append(escapedContent);
                    sb.Append('"');
                    i = closeIdx + 3;
                    continue;
                }
            }

            sb.Append(json[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Strips language string prefixes (r"...", @"...", b"...", f"...", $"...", etc.) outside string literals.
    /// </summary>
    private static string StripStringPrefixes(string json)
    {
        if (!json.Contains('"') && !json.Contains('\''))
            return json;

        var sb = new StringBuilder(json.Length);
        bool inDoubleQuote = false;
        bool inSingleQuote = false;
        bool escaped = false;
        int segStart = 0;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (escaped) { escaped = false; continue; }
            if (ch == '\\' && (inDoubleQuote || inSingleQuote))
            {
                if (i + 1 < json.Length) escaped = true;
                continue;
            }

            if (ch == '"' && !inSingleQuote)
            {
                if (!inDoubleQuote)
                {
                    var segment = json[segStart..i];
                    sb.Append(StripStringPrefixesInSegment(segment));
                    segStart = i;
                    inDoubleQuote = true;
                }
                else
                {
                    sb.Append(json, segStart, (i - segStart) + 1);
                    segStart = i + 1;
                    inDoubleQuote = false;
                }
            }
            else if (ch == '\'' && !inDoubleQuote)
            {
                if (!inSingleQuote)
                {
                    var segment = json[segStart..i];
                    sb.Append(StripStringPrefixesInSegment(segment));
                    segStart = i;
                    inSingleQuote = true;
                }
                else
                {
                    sb.Append(json, segStart, (i - segStart) + 1);
                    segStart = i + 1;
                    inSingleQuote = false;
                }
            }
        }

        if (segStart < json.Length)
        {
            if (inDoubleQuote || inSingleQuote)
                sb.Append(json, segStart, json.Length - segStart);
            else
                sb.Append(StripStringPrefixesInSegment(json[segStart..]));
        }

        return sb.ToString();
    }

    private static string StripStringPrefixesInSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return segment;
        return Regex.Replace(segment, @"(?<=(?:[:\[,]\s*|\s+|^))(?:[rRbBfFuU]{1,2}|[@$])(?=$|\s*$)", "");
    }

    /// <summary>
    /// Normalizes numbers with readability underscore separators outside string literals (e.g. 1_000_000 -> 1000000, 0xFF_FF -> 0xFFFF).
    /// </summary>
    private static string NormalizeNumericSeparators(string json)
    {
        if (!json.Contains('_'))
            return json;

        var sb = new StringBuilder(json.Length);
        bool inString = false;
        bool escaped = false;
        int segStart = 0;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (escaped) { escaped = false; continue; }
            if (ch == '\\' && inString) { if (i + 1 < json.Length) escaped = true; continue; }
            if (ch == '"')
            {
                if (!inString)
                {
                    var segment = json[segStart..i];
                    sb.Append(NormalizeNumericSeparatorsInSegment(segment));
                    segStart = i;
                    inString = true;
                }
                else
                {
                    sb.Append(json, segStart, (i - segStart) + 1);
                    segStart = i + 1;
                    inString = false;
                }
            }
        }

        if (segStart < json.Length)
        {
            if (inString)
                sb.Append(json, segStart, json.Length - segStart);
            else
                sb.Append(NormalizeNumericSeparatorsInSegment(json[segStart..]));
        }

        return sb.ToString();
    }

    private static string NormalizeNumericSeparatorsInSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || !segment.Contains('_'))
            return segment;

        return Regex.Replace(segment, @"(?<=(?:[:\[,]\s*|\s+|^))([+-]?(?:0[xXbBoO][0-9a-fA-F_]+|[0-9][0-9_]*(?:\.[0-9_]+)?(?:[eE][+-]?[0-9_]+)?))(?=\s*[,\]}]|$)", match =>
        {
            var val = match.Value;
            if (val.Contains('_'))
                return val.Replace("_", "");
            return val;
        });
    }

    /// <summary>
    /// Normalizes binary (0b...), octal (0o...), and hexadecimal (0x...) numbers outside string literals to base-10 decimal numbers.
    /// Handles optional leading signs (+ or -).
    /// </summary>
    private static string NormalizeNonDecimalNumbers(string json)
    {
        if (!json.Contains("0x") && !json.Contains("0X") &&
            !json.Contains("0b") && !json.Contains("0B") &&
            !json.Contains("0o") && !json.Contains("0O"))
            return json;

        var sb = new StringBuilder(json.Length);
        bool inString = false;
        bool escaped = false;
        int segStart = 0;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (escaped) { escaped = false; continue; }
            if (ch == '\\' && inString) { if (i + 1 < json.Length) escaped = true; continue; }
            if (ch == '"')
            {
                if (!inString)
                {
                    var segment = json[segStart..i];
                    sb.Append(NormalizeNonDecimalInSegment(segment));
                    segStart = i;
                    inString = true;
                }
                else
                {
                    sb.Append(json, segStart, (i - segStart) + 1);
                    segStart = i + 1;
                    inString = false;
                }
            }
        }

        if (segStart < json.Length)
        {
            if (inString)
                sb.Append(json, segStart, json.Length - segStart);
            else
                sb.Append(NormalizeNonDecimalInSegment(json[segStart..]));
        }

        return sb.ToString();
    }

    private static string NormalizeNonDecimalInSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
            return segment;

        return Regex.Replace(segment, @"(?<=(?:[:\[,]\s*|\s+|^))([+-]?)0([xXbBoO])([0-9a-fA-F]+)(?=\s*[,\]}]|$)", match =>
        {
            bool isNeg = match.Groups[1].Value == "-";
            char spec = char.ToLowerInvariant(match.Groups[2].Value[0]);
            var digits = match.Groups[3].Value;

            try
            {
                int fromBase = spec switch
                {
                    'x' => 16,
                    'b' => 2,
                    'o' => 8,
                    _ => 10
                };

                long val = Convert.ToInt64(digits, fromBase);
                if (isNeg) val = -val;
                return val.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return match.Value;
            }
        });
    }

    /// <summary>
    /// Normalizes leading (.5 -> 0.5) and trailing (5. -> 5.0) decimal points in floating point numbers outside strings.
    /// </summary>
    private static string NormalizeFloatDecimalPoints(string json)
    {
        if (!json.Contains('.'))
            return json;

        var sb = new StringBuilder(json.Length);
        bool inString = false;
        bool escaped = false;
        int segStart = 0;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (escaped) { escaped = false; continue; }
            if (ch == '\\' && inString) { if (i + 1 < json.Length) escaped = true; continue; }
            if (ch == '"')
            {
                if (!inString)
                {
                    var segment = json[segStart..i];
                    sb.Append(NormalizeFloatInSegment(segment));
                    segStart = i;
                    inString = true;
                }
                else
                {
                    sb.Append(json, segStart, (i - segStart) + 1);
                    segStart = i + 1;
                    inString = false;
                }
            }
        }

        if (segStart < json.Length)
        {
            if (inString)
                sb.Append(json, segStart, json.Length - segStart);
            else
                sb.Append(NormalizeFloatInSegment(json[segStart..]));
        }

        return sb.ToString();
    }

    private static string NormalizeFloatInSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || !segment.Contains('.'))
            return segment;

        // Leading dot: .5 -> 0.5, -.5 -> -0.5, .5e2 -> 0.5e2, -.5e-3 -> -0.5e-3
        var res = Regex.Replace(segment, @"(?<=(?:[:\[,]\s*|\s+|^))(-?)\.([0-9]+(?:[eE][+-]?[0-9]+)?)(?=\s*[,\]}]|$)", "${1}0.$2");
        // Trailing dot: 5. -> 5.0, -5. -> -5.0, 5.e3 -> 5.0e3
        res = Regex.Replace(res, @"(?<=(?:[:\[,]\s*|\s+|^))(-?[0-9]+)\.((?:[eE][+-]?[0-9]+)?)(?=\s*[,\]}]|$)", "$1.0$2");
        return res;
    }

    /// <summary>
    /// Normalizes illegal leading zeros in integer numbers outside string literals per RFC 8259 Section 6 (e.g. 007 -> 7, 09 -> 9, -012 -> -12, 00 -> 0).
    /// Preserves floating-point numbers starting with zero (e.g. 0.5) and standalone 0.
    /// </summary>
    private static string NormalizeLeadingZeroIntegers(string json)
    {
        if (!json.Contains('0'))
            return json;

        var sb = new StringBuilder(json.Length);
        bool inString = false;
        bool escaped = false;
        int segStart = 0;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (escaped) { escaped = false; continue; }
            if (ch == '\\' && inString) { if (i + 1 < json.Length) escaped = true; continue; }
            if (ch == '"')
            {
                if (!inString)
                {
                    var segment = json[segStart..i];
                    sb.Append(NormalizeLeadingZeroInSegment(segment));
                    segStart = i;
                    inString = true;
                }
                else
                {
                    sb.Append(json, segStart, (i - segStart) + 1);
                    segStart = i + 1;
                    inString = false;
                }
            }
        }

        if (segStart < json.Length)
        {
            if (inString)
                sb.Append(json, segStart, json.Length - segStart);
            else
                sb.Append(NormalizeLeadingZeroInSegment(json[segStart..]));
        }

        return sb.ToString();
    }

    private static string NormalizeLeadingZeroInSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || !segment.Contains('0'))
            return segment;

        // 007 -> 7, -012 -> -12
        var res = Regex.Replace(segment, @"(?<=(?:[:\[,]\s*|\s+|^))(-?)0+([1-9][0-9]*)(?!\.[0-9])(?=\s*[,\]}]|$)", "$1$2");
        // 00 -> 0, -00 -> 0
        res = Regex.Replace(res, @"(?<=(?:[:\[,]\s*|\s+|^))(-?)0{2,}(?!\.[0-9])(?=\s*[,\]}]|$)", "0");
        return res;
    }

    /// <summary>
    /// Extracts the outermost balanced JSON object ({...}) or array ([...]) if surrounded by conversational preamble/postamble.
    /// Handles conversational brackets in preambles (e.g. "Here are options [a, b]: { ... }") by evaluating both candidates.
    /// </summary>
    private static string ExtractJsonPayload(string text)
    {
        int firstBrace = text.IndexOf('{');
        int firstBracket = text.IndexOf('[');

        if (firstBrace < 0 && firstBracket < 0)
            return text;

        if (firstBrace >= 0 && firstBracket >= 0)
        {
            var braceCandidate = ExtractBalancedSegment(text, firstBrace, '{', '}');
            var bracketCandidate = ExtractBalancedSegment(text, firstBracket, '[', ']');

            if (firstBracket < firstBrace)
            {
                // If bracket ends before first brace begins and brace candidate has key-value structure,
                // the bracket was part of conversational prose (e.g. [Step 1]: { ... } or [opt1, opt2]: { ... })
                if (firstBracket + bracketCandidate.Length <= firstBrace && braceCandidate.Contains(':'))
                {
                    return braceCandidate;
                }
            }
            else
            {
                // firstBrace < firstBracket
                if (firstBrace + braceCandidate.Length <= firstBracket && !braceCandidate.Contains(':') && (bracketCandidate.Contains(',') || bracketCandidate.Contains('{')))
                {
                    return bracketCandidate;
                }
            }

            return firstBrace < firstBracket ? braceCandidate : bracketCandidate;
        }

        if (firstBrace >= 0)
            return ExtractBalancedSegment(text, firstBrace, '{', '}');

        return ExtractBalancedSegment(text, firstBracket, '[', ']');
    }

    private static string ExtractBalancedSegment(string text, int startIdx, char openChar, char closeChar)
    {
        int depth = 0;
        bool inDoubleQuote = false;
        bool inSingleQuote = false;
        bool escaped = false;
        int endIdx = -1;

        for (int i = startIdx; i < text.Length; i++)
        {
            char ch = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\' && (inDoubleQuote || inSingleQuote))
            {
                if (i + 1 < text.Length) escaped = true;
                continue;
            }

            if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (!inDoubleQuote && !inSingleQuote)
            {
                if (ch == openChar) depth++;
                else if (ch == closeChar)
                {
                    depth--;
                    if (depth == 0)
                    {
                        endIdx = i;
                        break;
                    }
                }
            }
        }

        if (endIdx > startIdx)
        {
            return text[startIdx..(endIdx + 1)].Trim();
        }

        int lastClose = text.LastIndexOf(closeChar);
        if (lastClose > startIdx)
        {
            return text[startIdx..(lastClose + 1)].Trim();
        }

        return text[startIdx..].Trim();
    }

    /// <summary>
    /// Converts Python tuple syntax outside string literals (e.g. (10, 20)) into JSON array syntax ([10, 20]).
    /// </summary>
    private static string RepairPythonTuples(string json)
    {
        if (!json.Contains('(') && !json.Contains(')'))
            return json;

        var sb = new StringBuilder(json.Length);
        bool inDoubleQuote = false;
        bool inSingleQuote = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];

            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\' && (inDoubleQuote || inSingleQuote))
            {
                sb.Append(ch);
                if (i + 1 < json.Length) escaped = true;
                continue;
            }

            if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                sb.Append(ch);
                continue;
            }

            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                sb.Append(ch);
                continue;
            }

            if (!inDoubleQuote && !inSingleQuote)
            {
                if (ch == '(')
                {
                    sb.Append('[');
                    continue;
                }
                if (ch == ')')
                {
                    sb.Append(']');
                    continue;
                }
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Normalizes leading plus signs on numbers outside string literals (+10 -> 10, +0.5 -> 0.5).
    /// RFC 8259 Section 6 explicitly forbids leading plus signs on numbers.
    /// </summary>
    private static string NormalizeLeadingPlusSigns(string json)
    {
        if (!json.Contains('+'))
            return json;

        var sb = new StringBuilder(json.Length);
        bool inDoubleQuote = false;
        bool inSingleQuote = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];

            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\' && (inDoubleQuote || inSingleQuote))
            {
                sb.Append(ch);
                if (i + 1 < json.Length) escaped = true;
                continue;
            }

            if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                sb.Append(ch);
                continue;
            }

            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                sb.Append(ch);
                continue;
            }

            if (!inDoubleQuote && !inSingleQuote && ch == '+')
            {
                if (i + 1 < json.Length && char.IsDigit(json[i + 1]))
                {
                    continue; // omit leading plus before digits
                }
                if (i + 1 < json.Length && json[i + 1] == '.')
                {
                    sb.Append('0'); // convert +.5 -> 0.5
                    continue;
                }
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quotes unquoted string identifier values after a colon outside strings (e.g. {status: active} -> {status: "active"}).
    /// Preserves standard primitives (true, false, null, numbers).
    /// </summary>
    private static string QuoteUnquotedValues(string json)
    {
        var sb = new StringBuilder(json.Length + 32);
        bool inString = false;
        bool escaped = false;
        int segStart = 0;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                if (i + 1 < json.Length) escaped = true;
                continue;
            }

            if (ch == '"')
            {
                if (!inString)
                {
                    var segment = json[segStart..i];
                    sb.Append(QuoteUnquotedValuesInSegment(segment));
                    segStart = i;
                    inString = true;
                }
                else
                {
                    sb.Append(json, segStart, (i - segStart) + 1);
                    segStart = i + 1;
                    inString = false;
                }
            }
        }

        if (segStart < json.Length)
        {
            if (inString)
            {
                sb.Append(json, segStart, json.Length - segStart);
            }
            else
            {
                var segment = json[segStart..];
                sb.Append(QuoteUnquotedValuesInSegment(segment));
            }
        }

        return sb.ToString();
    }

    private static string QuoteUnquotedValuesInSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return segment;

        // 1. Quote unquoted URLs: http://... or https://...
        segment = Regex.Replace(segment, @"(?<=(?:[:\[,])\s*)(https?:\/\/[^\s,}\]]+)(?=\s*(?:[,}\]]|[\r\n]|$))", "\"$1\"");

        // 2. Quote unquoted Windows file paths with drive letters: C:\... or D:/...
        segment = Regex.Replace(segment, @"(?<=(?:[:\[,])\s*)([a-zA-Z]:[\\/][^\s,}\]]+)(?=\s*(?:[,}\]]|[\r\n]|$))", match =>
        {
            var path = match.Groups[1].Value.Replace("\\", "\\\\");
            return $"\"{path}\"";
        });

        // 3. Quote unquoted Unix/relative file paths (/app/..., ./..., ../...)
        segment = Regex.Replace(segment, @"(?<=(?:[:\[,])\s*)(\.{0,2}\/[a-zA-Z0-9_\-\.\/]+)(?=\s*(?:[,}\]]|[\r\n]|$))", "\"$1\"");

        return Regex.Replace(segment, @"(?<=(?:[:\[,])\s*)([a-zA-Z_$][a-zA-Z0-9_$-]*)(?=\s*(?:[,}\]]|[\r\n]|$))", match =>
        {
            var val = match.Groups[1].Value;
            if (val is "true" or "false" or "null" or "True" or "False" or "None" or "NaN" or "Infinity" or "-Infinity" or "undefined" or "TRUE" or "FALSE" or "NULL" or "nil" or "inf" or "-inf" or "+inf" or "Null" or "NONE" or "Nil" or "NIL")
            {
                return val;
            }
            return $"\"{val}\"";
        });
    }

    /// <summary>
    /// Normalizes JavaScript/TypeScript backtick template strings (`...`) outside double/single quotes into standard double-quoted JSON strings.
    /// Escapes internal double quotes and control characters.
    /// </summary>
    private static string NormalizeBacktickStrings(string json)
    {
        if (!json.Contains('`'))
            return json;

        var sb = new StringBuilder(json.Length + 16);
        bool inDoubleQuote = false;
        bool inSingleQuote = false;
        bool escaped = false;
        int i = 0;

        while (i < json.Length)
        {
            char ch = json[i];
            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                i++;
                continue;
            }

            if (ch == '\\' && (inDoubleQuote || inSingleQuote))
            {
                sb.Append(ch);
                if (i + 1 < json.Length) escaped = true;
                i++;
                continue;
            }

            if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                sb.Append(ch);
                i++;
                continue;
            }

            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                sb.Append(ch);
                i++;
                continue;
            }

            if (!inDoubleQuote && !inSingleQuote && ch == '`')
            {
                // Opening backtick! Find closing backtick
                int closeIdx = -1;
                bool backtickEscaped = false;
                for (int k = i + 1; k < json.Length; k++)
                {
                    if (backtickEscaped) { backtickEscaped = false; continue; }
                    if (json[k] == '\\') { backtickEscaped = true; continue; }
                    if (json[k] == '`') { closeIdx = k; break; }
                }

                if (closeIdx > i)
                {
                    var inner = json[(i + 1)..closeIdx];
                    var escapedInner = EscapeUnescapedDoubleQuotes(inner);
                    escapedInner = escapedInner
                        .Replace("\r\n", "\\n")
                        .Replace("\n", "\\n")
                        .Replace("\r", "\\r")
                        .Replace("\t", "\\t");

                    sb.Append('"');
                    sb.Append(escapedInner);
                    sb.Append('"');
                    i = closeIdx + 1;
                    continue;
                }
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Inserts missing commas between adjacent properties or array elements outside string literals.
    /// Handles both multi-line (separated by newlines) and single-line (separated by whitespace) JSON.
    /// E.g. {"a": 1\n "b": 2} -> {"a": 1,\n "b": 2} and {"a": 1 "b": 2} -> {"a": 1, "b": 2}.
    /// </summary>
    private static string InsertMissingCommas(string json)
    {
        var sb = new StringBuilder(json.Length + 32);
        bool inDoubleQuote = false;
        bool inSingleQuote = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (escaped) { sb.Append(ch); escaped = false; continue; }
            if (ch == '\\' && (inDoubleQuote || inSingleQuote)) { sb.Append(ch); if (i + 1 < json.Length) escaped = true; continue; }
            if (ch == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; sb.Append(ch); }
            else if (ch == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; sb.Append(ch); }
            else { sb.Append(ch); }

            if (!inDoubleQuote && !inSingleQuote)
            {
                bool valueEnded = (ch == '"' || ch == '}' || ch == ']') ||
                    (char.IsLetterOrDigit(ch) && (i + 1 >= json.Length || (!char.IsLetterOrDigit(json[i + 1]) && json[i + 1] != '.')));

                if (valueEnded)
                {
                    int next = i + 1;
                    bool hasWhitespace = false;
                    while (next < json.Length && char.IsWhiteSpace(json[next]))
                    {
                        hasWhitespace = true;
                        next++;
                    }

                    if (hasWhitespace && next < json.Length)
                    {
                        char nextChar = json[next];
                        if (nextChar != ',' && nextChar != '}' && nextChar != ']')
                        {
                            bool isNextKey = LooksLikePropertyKey(json, next);
                            bool isNextElement = LooksLikeArrayElement(json, next) || (json[next] == '"' && !isNextKey);
                            if (isNextKey || isNextElement)
                            {
                                sb.Append(',');
                            }
                        }
                    }
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Normalizes assignment equals sign (=) outside strings between keys and values to colon (:).
    /// Handles both quoted keys ("key" = value) and bare keys (key = value).
    /// </summary>
    private static string NormalizeAssignmentEquals(string json)
    {
        if (!json.Contains('='))
            return json;

        var sb = new StringBuilder(json.Length);
        bool inDoubleQuote = false;
        bool inSingleQuote = false;
        bool escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (escaped) { sb.Append(ch); escaped = false; continue; }
            if (ch == '\\' && (inDoubleQuote || inSingleQuote)) { sb.Append(ch); if (i + 1 < json.Length) escaped = true; continue; }
            if (ch == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; sb.Append(ch); continue; }
            if (ch == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; sb.Append(ch); continue; }

            if (!inDoubleQuote && !inSingleQuote && ch == '=')
            {
                // Check if next char is '=' or '>' (e.g. == or =>): preserve those
                if ((i + 1 < json.Length && (json[i + 1] == '=' || json[i + 1] == '>')) ||
                    (i > 0 && (json[i - 1] == '!' || json[i - 1] == '<' || json[i - 1] == '>')))
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append(':');
                }
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts Python set literals outside strings (e.g. {"a", "b"} or {1, 2}) into JSON arrays ([...]).
    /// A set is identified by a { ... } block that contains commas or elements but zero colons outside strings.
    /// Empty objects {} are preserved.
    /// </summary>
    private static string NormalizePythonSets(string json)
    {
        if (!json.Contains('{') || !json.Contains('}'))
            return json;

        var sb = new StringBuilder(json.Length);
        bool inDoubleQuote = false;
        bool inSingleQuote = false;
        bool escaped = false;
        int i = 0;

        while (i < json.Length)
        {
            char ch = json[i];
            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                i++;
                continue;
            }

            if (ch == '\\')
            {
                sb.Append(ch);
                if (i + 1 < json.Length) escaped = true;
                i++;
                continue;
            }

            if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                sb.Append(ch);
                i++;
                continue;
            }

            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                sb.Append(ch);
                i++;
                continue;
            }

            if (!inDoubleQuote && !inSingleQuote && ch == '{')
            {
                // Find matching closing brace
                int closeIdx = FindMatchingDelimiter(json, i, '{', '}');
                if (closeIdx > i)
                {
                    var inner = json[(i + 1)..closeIdx];
                    if (!string.IsNullOrWhiteSpace(inner) && !ContainsCharOutsideStrings(inner, ':'))
                    {
                        // No colons outside strings! This is a Python set!
                        sb.Append('[');
                        sb.Append(NormalizePythonSets(inner)); // recursive for nested sets
                        sb.Append(']');
                        i = closeIdx + 1;
                        continue;
                    }
                }
            }

            sb.Append(json[i]);
            i++;
        }

        return sb.ToString();
    }

    private static int FindMatchingDelimiter(string text, int startIdx, char openChar, char closeChar)
    {
        int depth = 0;
        bool inDoubleQuote = false;
        bool inSingleQuote = false;
        bool escaped = false;

        for (int i = startIdx; i < text.Length; i++)
        {
            char ch = text[i];
            if (escaped) { escaped = false; continue; }
            if (ch == '\\') { if (i + 1 < text.Length) escaped = true; continue; }
            if (ch == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; continue; }
            if (ch == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; continue; }

            if (!inDoubleQuote && !inSingleQuote)
            {
                if (ch == openChar) depth++;
                else if (ch == closeChar)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
        }

        return -1;
    }

    private static bool ContainsCharOutsideStrings(string text, char target)
    {
        bool inDoubleQuote = false;
        bool inSingleQuote = false;
        bool escaped = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (escaped) { escaped = false; continue; }
            if (ch == '\\') { if (i + 1 < text.Length) escaped = true; continue; }
            if (ch == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; continue; }
            if (ch == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; continue; }

            if (!inDoubleQuote && !inSingleQuote && ch == target)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Normalizes 2-digit hex escape sequences (\xHH) inside double-quoted string literals to standard 4-digit JSON unicode escapes (\u00HH).
    /// </summary>
    private static string NormalizeHexEscapes(string json)
    {
        if (!json.Contains("\\x") && !json.Contains("\\X"))
            return json;

        return Regex.Replace(json, @"\\([xX])([0-9a-fA-F]{2})", "\\u00$2");
    }
}
