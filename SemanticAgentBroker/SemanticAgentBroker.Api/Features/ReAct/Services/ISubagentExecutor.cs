using Microsoft.SemanticKernel;

namespace SemanticAgentBroker.Api.Features.ReAct.Services;

public record ToolAction(string ActionName, string ActionInput, bool IsInferred = false);

public record SubagentResult(
    string? Thought,
    string? Plan,
    IReadOnlyList<ToolAction> Actions,
    string? FinalAnswer,
    bool IsError,
    string? ErrorMessage);

public interface ISubagentExecutor
{
    Task<SubagentResult> ExecuteStepAsync(
        Guid sessionId,
        Kernel kernel,
        string systemPrompt,
        string stateInjection,
        int step,
        int maxContextTokens,
        bool debugMode,
        CancellationToken ct);
}
