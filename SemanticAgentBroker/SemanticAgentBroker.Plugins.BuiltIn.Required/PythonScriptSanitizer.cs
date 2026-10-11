using System.Text;
using System.Text.RegularExpressions;

namespace SemanticAgentBroker.Plugins.BuiltIn.Required;

/// <summary>
/// Deterministic pre-flight sanitizer that repairs common local LLM syntax mistakes
/// in Python scripts (missing colons on block statements, invalid open modes, f-string quote collisions)
/// before executing them in the Python sandbox container.
/// </summary>
public static partial class PythonScriptSanitizer
{
    // Compound statements that require a trailing colon in Python
    private static readonly Regex BlockHeaderRegex = new(
        @"^\s*(for\s+.+?\s+in\s+[^:\r\n]+|if\s+[^:\r\n]+|elif\s+[^:\r\n]+|while\s+[^:\r\n]+|else|try|finally|except(?:\s+[^:\r\n]*)?|with\s+[^:\r\n]+|def\s+[^:\r\n]+|class\s+[^:\r\n]+)\s*$",
        RegexOptions.Compiled);

    // File open mode typo: 'n' instead of 'w'
    private static readonly Regex OpenModeTypoRegex = new(
        @"(open\s*\([^,]+,\s*)['""]n['""]",
        RegexOptions.Compiled);

    private static readonly Regex OpenNamedModeTypoRegex = new(
        @"(open\s*\([^)]*?mode\s*=\s*)['""]n['""]",
        RegexOptions.Compiled);

    // Loop target token stutter: e.g. "for s in filtered 'filtered_sensors':" -> "for s in filtered_sensors:"
    private static readonly Regex LoopStutterRegex = new(
        @"\bfor\s+([a-zA-Z_]\w*)\s+in\s+([a-zA-Z_]\w*)\s+['""]([a-zA-Z_]\w*)['""]\s*:",
        RegexOptions.Compiled);

    // f-string with accidental leading quote dot in interpolation: e.g. {'.variable'} -> {variable}
    private static readonly Regex FStringQuoteDotVarRegex = new(
        @"\{'\.([a-zA-Z_]\w*)'\}",
        RegexOptions.Compiled);

    /// <summary>
    /// Applies deterministic, syntax-safe repairs to a Python script before execution.
    /// </summary>
    /// <param name="script">The raw Python script text.</param>
    /// <returns>The sanitized Python script text.</returns>
    public static string Sanitize(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return script ?? string.Empty;

        var code = script;

        // 1. Fix open mode typo ('n' -> 'w')
        code = OpenModeTypoRegex.Replace(code, "$1'w'");
        code = OpenNamedModeTypoRegex.Replace(code, "$1'w'");

        // 2. Fix loop target token stutter
        code = LoopStutterRegex.Replace(code, "for $1 in $3:");

        // 3. Fix f-string quote dot variable: {'.var'} -> {var}
        code = FStringQuoteDotVarRegex.Replace(code, "{$1}");

        // 4. Fix nested single quotes in single-quoted f-strings: f'... {dict['key']} ...' -> f'... {dict["key"]} ...'
        code = FixFStringSingleQuoteCollisions(code);

        // 5. Line-by-line checks: missing colons on compound statements
        code = FixMissingColons(code);

        return code;
    }

    private static string FixMissingColons(string script)
    {
        var lines = script.Split('\n');
        var sb = new StringBuilder(script.Length + 32);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineEnding = line.EndsWith('\r') ? "\r" : string.Empty;
            var cleanLine = lineEnding.Length > 0 ? line[..^1] : line;

            // Separate line code from trailing comment
            var (codePart, commentPart) = SplitCodeAndComment(cleanLine);

            if (!string.IsNullOrWhiteSpace(codePart))
            {
                var trimmedCode = codePart.TrimEnd();

                // If not already ending with colon or line continuation backslash
                if (!trimmedCode.EndsWith(':') && !trimmedCode.EndsWith('\\'))
                {
                    // Check balanced delimiters on this line (no unclosed parens/brackets)
                    if (HasBalancedDelimiters(trimmedCode) && BlockHeaderRegex.IsMatch(trimmedCode))
                    {
                        var trailingSpaces = codePart[trimmedCode.Length..];
                        codePart = trimmedCode + ":" + (trailingSpaces.Length > 0 ? trailingSpaces : (string.IsNullOrEmpty(commentPart) ? string.Empty : " "));
                    }
                }
            }

            sb.Append(codePart);
            if (!string.IsNullOrEmpty(commentPart))
            {
                sb.Append(commentPart);
            }
            sb.Append(lineEnding);

            if (i < lines.Length - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private static (string codePart, string commentPart) SplitCodeAndComment(string line)
    {
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '\\' && (inSingleQuote || inDoubleQuote))
            {
                i++; // Skip escaped char
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (c == '#' && !inSingleQuote && !inDoubleQuote)
            {
                return (line[..i], line[i..]);
            }
        }

        return (line, string.Empty);
    }

    private static bool HasBalancedDelimiters(string text)
    {
        int parens = 0;
        int brackets = 0;
        int braces = 0;
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && (inSingleQuote || inDoubleQuote))
            {
                i++;
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (!inSingleQuote && !inDoubleQuote)
            {
                if (c == '(') parens++;
                else if (c == ')') parens--;
                else if (c == '[') brackets++;
                else if (c == ']') brackets--;
                else if (c == '{') braces++;
                else if (c == '}') braces--;

                if (parens < 0 || brackets < 0 || braces < 0)
                    return false;
            }
        }

        return parens == 0 && brackets == 0 && braces == 0 && !inSingleQuote && !inDoubleQuote;
    }

    private static string FixFStringSingleQuoteCollisions(string code)
    {
        // Matches f'...' where inner {...} has single quotes: e.g. f'Found {d['a']} here'
        // In Python 3.12, normalizes nested quotes and prevents subtle quote collisions in JSON-encoded scripts.
        // Inner single quotes inside {...} within single-quoted f-strings are replaced with double quotes.
        var result = new StringBuilder(code.Length);
        int i = 0;

        while (i < code.Length)
        {
            // Detect start of single-quoted f-string: f' or F' or rf' or fr'
            if ((i + 1 < code.Length && (code[i] == 'f' || code[i] == 'F') && code[i + 1] == '\'') ||
                (i + 2 < code.Length && (code[i] == 'r' || code[i] == 'R') && (code[i + 1] == 'f' || code[i + 1] == 'F') && code[i + 2] == '\''))
            {
                int prefixLen = (code[i] == 'r' || code[i] == 'R') ? 2 : 1;
                result.Append(code.AsSpan(i, prefixLen + 1)); // append f' or rf'
                i += prefixLen + 1;

                bool inBrace = false;
                while (i < code.Length)
                {
                    char c = code[i];
                    if (c == '\\')
                    {
                        result.Append(c);
                        if (i + 1 < code.Length)
                        {
                            result.Append(code[i + 1]);
                            i += 2;
                            continue;
                        }
                    }
                    else if (c == '{')
                    {
                        // Escaped {{ ?
                        if (i + 1 < code.Length && code[i + 1] == '{')
                        {
                            result.Append("{{");
                            i += 2;
                            continue;
                        }
                        inBrace = true;
                        result.Append(c);
                    }
                    else if (c == '}')
                    {
                        // Escaped }} ?
                        if (i + 1 < code.Length && code[i + 1] == '}')
                        {
                            result.Append("}}");
                            i += 2;
                            continue;
                        }
                        inBrace = false;
                        result.Append(c);
                    }
                    else if (c == '\'')
                    {
                        if (inBrace)
                        {
                            // Single quote inside f-string {...}: convert to double quote for consistency!
                            result.Append('"');
                        }
                        else
                        {
                            // Closing quote of f-string!
                            result.Append(c);
                            i++;
                            break;
                        }
                    }
                    else
                    {
                        result.Append(c);
                    }
                    i++;
                }
            }
            else
            {
                result.Append(code[i]);
                i++;
            }
        }

        return result.ToString();
    }
}
