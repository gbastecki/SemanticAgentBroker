using Microsoft.SemanticKernel.ChatCompletion;

namespace SemanticAgentBroker.Core.Features.TokenCounting;

/// <summary>
/// Abstraction for counting tokens in text and chat histories.
/// Enables accurate token budget tracking for context compaction and proactive overflow prevention.
/// </summary>
public interface ITokenCounter
{
    /// <summary>Counts tokens in a single text string.</summary>
    int CountTokens(string text, string modelId);

    /// <summary>Counts total tokens across all messages in a chat history, including per-message overhead.</summary>
    int CountTokens(ChatHistory history, string modelId);
}
