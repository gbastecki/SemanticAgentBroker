using Microsoft.SemanticKernel;

namespace SemanticAgentBroker.Api.Features.ReAct.Services;

/// <summary>
/// Abstraction for the ReAct (Reasoning and Acting) execution engine.
/// Supports pause/resume, interruption, and step-by-step approval.
/// </summary>
public interface IReactExecutionEngine
{
    Task<string> ExecuteAsync(
        Guid sessionId,
        Kernel kernel,
        string systemPrompt,
        string originalObjective,
        bool debugMode = false,
        bool isContinuation = false,
        int? maxSteps = null,
        SemaphoreSlim? executionSemaphore = null,
        SemaphoreOwnershipState? semaphoreState = null,
        CancellationToken ct = default);
}
