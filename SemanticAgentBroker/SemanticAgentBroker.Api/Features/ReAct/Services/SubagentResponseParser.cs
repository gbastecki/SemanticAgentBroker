using SemanticAgentBroker.Core.Constants;
using SemanticAgentBroker.Shared.Configuration;
using System.Text.RegularExpressions;

namespace SemanticAgentBroker.Api.Features.ReAct.Services;

public record ParsedSubagentResponse(
    string? Thought,
    string? Plan,
    List<ToolAction> ToolCalls,
    string? FinalAnswer);

/// <summary>
/// Dedicated parser for ReAct subagent output. Handles markdown parsing, structured token extraction,
/// multi-action extraction, intent-based tool detection, and raw code wrapping.
/// </summary>
public static partial class SubagentResponseParser
{
    public static ParsedSubagentResponse ParseResponse(
        string reasoningContent,
        AiProviderOptions aiOptions,
        string? modelId,
        IReadOnlyList<string> availableTools)
    {
        if (string.IsNullOrWhiteSpace(reasoningContent))
            return new ParsedSubagentResponse(null, null, new List<ToolAction>(), null);

        // Pre-normalize Unicode spaces, zero-width chars, and byte-fallback tokens
        reasoningContent = JsonSanitizer.NormalizeUnicodeAndByteArtifacts(reasoningContent);

        // If a line-start Observation: token appears after the first Action: token,
        // truncate reasoningContent at that index to discard hallucinated environment output.
        int firstActionIdx = FindStrictTokenIndex(reasoningContent, ReactFormatTokens.Action);
        if (firstActionIdx >= 0)
        {
            int obsIdx = FindStrictTokenIndex(reasoningContent, ReactFormatTokens.Observation, firstActionIdx);
            if (obsIdx > firstActionIdx)
            {
                reasoningContent = reasoningContent[..obsIdx].TrimEnd();
            }
        }

        var thought = ExtractSection(reasoningContent, ReactFormatTokens.Thought,
            new[] { ReactFormatTokens.Action, ReactFormatTokens.Plan, ReactFormatTokens.FinalAnswer });
        var plan = ExtractSection(reasoningContent, ReactFormatTokens.Plan,
            new[] { ReactFormatTokens.Action, ReactFormatTokens.FinalAnswer });
        var finalAnswer = ExtractSection(reasoningContent, ReactFormatTokens.FinalAnswer,
            Array.Empty<string>());

        // If finalAnswer has a trailing Action line (e.g. Action: None), strip it
        if (!string.IsNullOrWhiteSpace(finalAnswer))
        {
            int actionInFinal = FindStrictTokenIndex(finalAnswer, ReactFormatTokens.Action);
            if (actionInFinal >= 0)
            {
                finalAnswer = finalAnswer[..actionInFinal].Trim();
                if (string.IsNullOrWhiteSpace(finalAnswer))
                    finalAnswer = null;
            }
        }

        var toolCalls = ExtractAllActionPairs(reasoningContent);

        // Fallback: extract tool name from reasoning text ONLY if no Action: token was found AND no FinalAnswer was produced
        if (toolCalls.Count == 0 && availableTools.Count > 0 && string.IsNullOrWhiteSpace(finalAnswer))
        {
            var fallbackAction = ExtractToolNameFromText(reasoningContent, plan, availableTools.ToList());
            if (!string.IsNullOrWhiteSpace(fallbackAction))
            {
                toolCalls.Add(new ToolAction(fallbackAction.Trim(), "{}", IsInferred: true));
            }
        }

        // If no explicit Thought: token but there's content before any token, use it as the thought
        if (string.IsNullOrWhiteSpace(thought) && !string.IsNullOrWhiteSpace(reasoningContent))
        {
            var firstToken = FindFirstTokenPosition(reasoningContent);
            if (firstToken > 0)
            {
                thought = reasoningContent[..firstToken].Trim();
            }
            else if (firstToken < 0)
            {
                thought = reasoningContent.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(thought))
        {
            thought = CleanThoughtText(thought, aiOptions, modelId);
        }

        return new ParsedSubagentResponse(thought, plan, toolCalls, finalAnswer);
    }

    public static string CleanThoughtText(string text, AiProviderOptions aiOptions, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var tokensToStrip = new List<string>();

        // 1. Add model-specific prefixes if configured for this model
        if (!string.IsNullOrEmpty(modelId))
        {
            foreach (var conn in aiOptions.Connections.Where(c => c.IsEnabled))
            {
                var modelConfig = conn.Models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
                if (modelConfig?.ThoughtPrefixesToStrip != null && modelConfig.ThoughtPrefixesToStrip.Count > 0)
                {
                    tokensToStrip.AddRange(modelConfig.ThoughtPrefixesToStrip);
                }
            }
        }

        // 2. Add global inference prefixes
        if (aiOptions.Inference.ThoughtPrefixesToStrip != null && aiOptions.Inference.ThoughtPrefixesToStrip.Count > 0)
        {
            tokensToStrip.AddRange(aiOptions.Inference.ThoughtPrefixesToStrip);
        }

        // 3. Fallback defaults if none configured
        if (tokensToStrip.Count == 0)
        {
            tokensToStrip.AddRange(new[] { "<channel|>Thought", "<channel|>", "</channel>", "<|thought|>", "<thought>", "</thought>", "[THOUGHT]" });
        }

        foreach (var token in tokensToStrip.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            text = Regex.Replace(text, Regex.Escape(token), "", RegexOptions.IgnoreCase);
        }

        text = Regex.Replace(text, @"^\s*Thought\s*:?\s*", "", RegexOptions.IgnoreCase);

        // Deduplicate degenerate repetitive lines (e.g. model looping identical reasoning lines 3+ times)
        text = DeduplicateRepetitiveLines(text);

        return text.Trim();
    }

    private static string DeduplicateRepetitiveLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (lines.Length <= 3) return text;

        var deduped = new List<string>(lines.Length);
        string? lastLine = null;
        int repeatCount = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 5 && string.Equals(trimmed, lastLine, StringComparison.Ordinal))
            {
                repeatCount++;
                if (repeatCount <= 2)
                {
                    deduped.Add(line);
                }
                else if (repeatCount == 3)
                {
                    deduped.Add("... [repetitive reasoning lines collapsed] ...");
                }
            }
            else
            {
                lastLine = trimmed.Length > 5 ? trimmed : null;
                repeatCount = 1;
                deduped.Add(line);
            }
        }

        return string.Join("\n", deduped);
    }

    public static int FindStrictTokenIndex(string content, string token, int startFrom = 0)
    {
        int idx = startFrom;
        while (true)
        {
            idx = content.IndexOf(token, idx, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;

            bool isStartOfLine = idx == 0 || content[idx - 1] == '\n' || content[idx - 1] == '\r';
            bool isValidMarkdownPrefix = false;
            if (!isStartOfLine)
            {
                int startOfLine = idx - 1;
                while (startOfLine > 0 && content[startOfLine] != '\n' && content[startOfLine] != '\r')
                {
                    startOfLine--;
                }
                int prefixStart = (startOfLine > 0 || content[startOfLine] == '\n' || content[startOfLine] == '\r') ? startOfLine + 1 : startOfLine;
                string prefix = content[prefixStart..idx].Trim();
                isValidMarkdownPrefix = MarkdownPrefixRegex().IsMatch(prefix);
            }

            if (isStartOfLine || isValidMarkdownPrefix)
            {
                return idx;
            }

            idx += token.Length;
        }
    }

    public static string? ExtractSection(string content, string token, string[] stopTokens)
    {
        int idx = FindStrictTokenIndex(content, token);
        if (idx < 0) return null;

        int start = idx + token.Length;
        int end = content.Length;

        foreach (var stop in stopTokens)
        {
            int stopIdx = FindStrictTokenIndex(content, stop, start);
            if (stopIdx >= start && stopIdx < end)
                end = stopIdx;
        }

        var result = content[start..end].Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public static int FindFirstTokenPosition(string content)
    {
        var tokens = new[] { ReactFormatTokens.Thought, ReactFormatTokens.Action,
            ReactFormatTokens.ActionInput, ReactFormatTokens.Plan,
            ReactFormatTokens.FinalAnswer, ReactFormatTokens.Observation };

        int minPos = -1;
        foreach (var token in tokens)
        {
            int pos = FindStrictTokenIndex(content, token);
            if (pos >= 0 && (minPos < 0 || pos < minPos))
                minPos = pos;
        }
        return minPos;
    }

    public static List<ToolAction> ExtractAllActionPairs(string content)
    {
        var pairs = new List<ToolAction>();
        int searchFrom = 0;

        int finalAnswerIdx = FindStrictTokenIndex(content, ReactFormatTokens.FinalAnswer);
        int effectiveEnd = finalAnswerIdx >= 0 ? finalAnswerIdx : content.Length;

        while (searchFrom < effectiveEnd)
        {
            int actionIdx = FindStrictTokenIndex(content, ReactFormatTokens.Action, searchFrom);
            if (actionIdx < 0 || actionIdx >= effectiveEnd) break;

            int actionContentStart = actionIdx + ReactFormatTokens.Action.Length;

            int nextActionIdx = FindStrictTokenIndex(content, ReactFormatTokens.Action, actionContentStart);
            int nextFinalIdx = FindStrictTokenIndex(content, ReactFormatTokens.FinalAnswer, actionContentStart);
            int nextActionOrFinal = effectiveEnd;
            if (nextActionIdx >= 0 && nextActionIdx < nextActionOrFinal) nextActionOrFinal = nextActionIdx;
            if (nextFinalIdx >= 0 && nextFinalIdx < nextActionOrFinal) nextActionOrFinal = nextFinalIdx;

            int inputIdx = FindStrictTokenIndex(content, ReactFormatTokens.ActionInput, actionContentStart);

            string actionName;
            string actionInput;
            int nextSearchStart;

            if (inputIdx >= 0 && inputIdx < nextActionOrFinal)
            {
                // Action has a corresponding ActionInput: block
                int actionEnd = inputIdx;
                int lastNewline = content.LastIndexOfAny(['\n', '\r'], inputIdx - 1, inputIdx - actionContentStart);
                if (lastNewline >= actionContentStart)
                {
                    actionEnd = lastNewline;
                }
                actionName = content[actionContentStart..actionEnd].Trim();

                int inputContentStart = inputIdx + ReactFormatTokens.ActionInput.Length;
                int inputEnd = nextActionOrFinal;
                if (nextActionOrFinal < content.Length)
                {
                    int nextTokenNewline = content.LastIndexOfAny(['\n', '\r'], nextActionOrFinal - 1, nextActionOrFinal - inputContentStart);
                    if (nextTokenNewline >= inputContentStart)
                    {
                        inputEnd = nextTokenNewline;
                    }
                }
                actionInput = content[inputContentStart..inputEnd].Trim();
                nextSearchStart = nextActionOrFinal;
            }
            else
            {
                // Action is parameterless (no ActionInput: block before next Action/FinalAnswer)
                int actionEnd = nextActionOrFinal;
                if (nextActionOrFinal < content.Length)
                {
                    int lastNewline = content.LastIndexOfAny(['\n', '\r'], nextActionOrFinal - 1, nextActionOrFinal - actionContentStart);
                    if (lastNewline >= actionContentStart)
                    {
                        actionEnd = lastNewline;
                    }
                }
                actionName = content[actionContentStart..actionEnd].Trim();
                actionInput = "{}";
                nextSearchStart = nextActionOrFinal;
            }

            actionName = actionName.Trim().Trim('`', '*', '_', '"', '\'').Trim();
            if (!actionInput.StartsWith("```"))
            {
                actionInput = actionInput.Trim().Trim('`').Trim();
            }

            if (!string.IsNullOrWhiteSpace(actionName))
            {
                pairs.Add(new ToolAction(actionName, string.IsNullOrWhiteSpace(actionInput) ? "{}" : actionInput));
            }

            searchFrom = nextSearchStart;
        }

        // If the model produced multiple PythonSandbox-ExecutePythonScript actions (drafting/revising in response),
        // retain only the last (finalized) script invocation to prevent running earlier abandoned drafts in parallel.
        var pythonActions = pairs.Where(p => p.ActionName.Equals("PythonSandbox-ExecutePythonScript", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pythonActions.Count > 1)
        {
            var lastPythonAction = pythonActions[^1];
            var filtered = new List<ToolAction>();
            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].ActionName.Equals("PythonSandbox-ExecutePythonScript", StringComparison.OrdinalIgnoreCase))
                {
                    if (ReferenceEquals(pairs[i], lastPythonAction) || pairs[i] == lastPythonAction)
                    {
                        filtered.Add(pairs[i]);
                    }
                }
                else
                {
                    filtered.Add(pairs[i]);
                }
            }
            pairs = filtered;
        }

        return pairs;
    }

    public static int FindLastStrictTokenIndex(string content, string token, int? beforeIdx = null)
    {
        int idx = beforeIdx ?? content.Length;
        if (idx <= 0) return -1;
        while (true)
        {
            idx = content.LastIndexOf(token, idx - 1, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;

            bool isStartOfLine = idx == 0 || content[idx - 1] == '\n' || content[idx - 1] == '\r';
            bool isValidMarkdownPrefix = false;
            if (!isStartOfLine)
            {
                int startOfLine = idx - 1;
                while (startOfLine > 0 && content[startOfLine] != '\n' && content[startOfLine] != '\r')
                {
                    startOfLine--;
                }
                int prefixStart = (startOfLine > 0 || content[startOfLine] == '\n' || content[startOfLine] == '\r') ? startOfLine + 1 : startOfLine;
                string prefix = content[prefixStart..idx].Trim();
                isValidMarkdownPrefix = MarkdownPrefixRegex().IsMatch(prefix);
            }

            if (isStartOfLine || isValidMarkdownPrefix)
                return idx;

            if (idx == 0) return -1;
        }
    }

    public static string? ExtractToolNameFromText(string content, string? planSection, List<string> availableTools)
    {
        if (string.IsNullOrWhiteSpace(content) || availableTools.Count == 0)
            return null;

        // If a plan section is present, remove it from search content to avoid matching future planned steps
        if (!string.IsNullOrWhiteSpace(planSection))
        {
            content = content.Replace(planSection, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        var pastPatterns = new[] {
            "already used", "used", "previously called", "completed", "results from", "output of",
            "returned", "showed", "called", "executed", "invoked", "already ran", "ran", "finished"
        };

        var intentRegex = IntentKeywordRegex();
        var matches = intentRegex.Matches(content);

        // Check matches from latest to earliest
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            int patternIdx = match.Index;

            int windowEnd = Math.Min(patternIdx + match.Length + 200, content.Length);
            string window = content[(patternIdx + match.Length)..windowEnd];

            var tool = FindFirstToolMention(window, availableTools);
            if (tool != null)
            {
                // Ensure the tool mention in the window is not preceded by past-tense indicators
                int toolOffset = FindStandaloneToolIndex(window, tool);
                string textBeforeTool = toolOffset >= 0 ? window[..toolOffset] : string.Empty;
                if (!pastPatterns.Any(p => textBeforeTool.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    return tool;
                }
            }
        }

        var mentionedTools = availableTools
            .Where(t => FindStandaloneToolIndex(content, t) >= 0)
            .Distinct()
            .ToList();

        if (mentionedTools.Count == 1)
        {
            var singleTool = mentionedTools[0];
            int mentionIdx = FindLastStandaloneToolIndex(content, singleTool);
            int contextStart = Math.Max(0, mentionIdx - 100);
            int contextEnd = Math.Min(content.Length, mentionIdx + singleTool.Length + 50);
            string context = content[contextStart..contextEnd];
            bool isPastTense = pastPatterns.Any(p => context.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (!isPastTense) return singleTool;
        }

        var lastMention = FindLastToolMention(content, availableTools);
        if (lastMention != null)
        {
            int mentionIdx = FindLastStandaloneToolIndex(content, lastMention);
            int contextStart = Math.Max(0, mentionIdx - 100);
            int contextEnd = Math.Min(content.Length, mentionIdx + lastMention.Length + 50);
            string context = content[contextStart..contextEnd];
            bool isPastTense = pastPatterns.Any(p => context.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (!isPastTense) return lastMention;
        }

        return null;
    }

    [GeneratedRegex(@"\b(will use|need to use|should use|going to use|let me use|I'll use|next step|execute|invoke|call)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntentKeywordRegex();

    /// <summary>
    /// Checks whether a tool name occurrence at the specified index represents a standalone tool reference,
    /// rather than being embedded inside a file path, URL, or compound identifier (e.g. /app/workdir/ToolName_step1.md).
    /// </summary>
    public static bool IsStandaloneToolMention(string text, int index, int length)
    {
        if (index < 0 || index + length > text.Length)
            return false;

        // Check preceding character
        if (index > 0)
        {
            char prev = text[index - 1];
            if (prev is '/' or '\\' or '_' or '-' or '.' || char.IsLetterOrDigit(prev))
                return false;
        }

        // Check following character
        if (index + length < text.Length)
        {
            char next = text[index + length];
            if (next is '/' or '\\' or '_' or '-' || char.IsLetterOrDigit(next))
                return false;

            // If followed by a dot, check if it's a file extension (e.g., .md, .txt, .py)
            if (next == '.' && index + length + 1 < text.Length && char.IsLetterOrDigit(text[index + length + 1]))
                return false;
        }

        return true;
    }

    public static int FindStandaloneToolIndex(string text, string tool, int startIndex = 0)
    {
        int pos = startIndex;
        while (pos < text.Length)
        {
            int found = text.IndexOf(tool, pos, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return -1;
            if (IsStandaloneToolMention(text, found, tool.Length))
                return found;
            pos = found + 1;
        }
        return -1;
    }

    public static int FindLastStandaloneToolIndex(string text, string tool)
    {
        int pos = text.Length;
        while (pos > 0)
        {
            int found = text.LastIndexOf(tool, pos - 1, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return -1;
            if (IsStandaloneToolMention(text, found, tool.Length))
                return found;
            pos = found - 1;
        }
        return -1;
    }

    public static string? FindFirstToolMention(string text, List<string> tools)
    {
        string? best = null;
        int bestPos = int.MaxValue;
        foreach (var t in tools)
        {
            int pos = FindStandaloneToolIndex(text, t);
            if (pos >= 0 && pos < bestPos) { bestPos = pos; best = t; }
        }
        return best;
    }

    public static string? FindLastToolMention(string text, List<string> tools)
    {
        string? best = null;
        int bestPos = -1;
        foreach (var t in tools)
        {
            int pos = FindLastStandaloneToolIndex(text, t);
            if (pos >= 0 && pos > bestPos) { bestPos = pos; best = t; }
        }
        return best;
    }

    public static string? TryWrapRawCodeInput(string actionName, string rawInput)
    {
        var trimmed = rawInput.Trim();

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return null;

        if (trimmed.StartsWith("```"))
        {
            int firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                int lastFence = trimmed.LastIndexOf("```");
                trimmed = lastFence > firstNewline
                    ? trimmed[(firstNewline + 1)..lastFence].Trim()
                    : trimmed[(firstNewline + 1)..].Trim();
            }
        }

        string? paramName = actionName switch
        {
            _ when actionName.Contains("ExecutePythonScript") => "script",
            _ when actionName.Contains("ExecuteBashScript")   => "script",
            _ when actionName.Contains("ExecuteCommand")      => "command",
            _ => null
        };

        if (paramName == null)
            return null;

        trimmed = JsonSanitizer.NormalizeUnicodeAndByteArtifacts(trimmed);
        var escaped = System.Text.Json.JsonSerializer.Serialize(trimmed);
        return $"{{\"{paramName}\": {escaped}}}";
    }

    [GeneratedRegex(@"^[\s*\#\-_`]+$")]
    private static partial Regex MarkdownPrefixRegex();
}
