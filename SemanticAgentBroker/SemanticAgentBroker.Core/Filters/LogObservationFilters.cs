using SemanticAgentBroker.Core.Constants;
using Microsoft.SemanticKernel;

using SemanticAgentBroker.Core.Features.Agents;

namespace SemanticAgentBroker.Core.Filters;

/// <summary>
/// Abstraction for storing agent task events and logs in persistence.
/// </summary>
public interface IAgentTaskStore
{
    /// <summary>Logs an event message for a task session.</summary>
    /// <param name="sessionId">The task session ID.</param>
    /// <param name="level">The event log level name.</param>
    /// <param name="message">The log message text.</param>
    Task LogAsync(Guid sessionId, string level, string message);

    /// <summary>Gets the highest sequence number logged for a session.</summary>
    /// <param name="sessionId">The task session ID.</param>
    /// <returns>The highest sequence number.</returns>
    Task<int> GetMaxSequenceNumberAsync(Guid sessionId);
}

/// <summary>
/// Uses AgentTaskContext (scoped) to resolve the current TaskId,
/// instead of looking for a non-existent "SessionId" in KernelArguments.
/// </summary>
public class FunctionInvocationLoggerFilter : IFunctionInvocationFilter
{
    private readonly IAgentTaskStore _taskStore;
    private readonly IAgentTaskContext _context;

    /// <summary>
    /// Initializes a new instance of <see cref="FunctionInvocationLoggerFilter"/>.
    /// </summary>
    /// <param name="taskStore">The agent task store.</param>
    /// <param name="context">The scoped agent task context.</param>
    public FunctionInvocationLoggerFilter(IAgentTaskStore taskStore, IAgentTaskContext context)
    {
        _taskStore = taskStore;
        _context = context;
    }

    /// <inheritdoc />
    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var taskId = _context.TaskId;

        if (taskId != Guid.Empty)
        {
            await _taskStore.LogAsync(taskId, LogLevelNames.Tool, $"Invoking {context.Function.PluginName}.{context.Function.Name} with args: {string.Join(", ", context.Arguments.Select(a => $"{a.Key}={a.Value}"))}");
        }

        await next(context);

        if (taskId != Guid.Empty)
        {
            await _taskStore.LogAsync(taskId, LogLevelNames.System, $"Function {context.Function.Name} returned: {context.Result?.GetValue<object>()}");
        }
    }
}

/// <summary>
/// Semantic Kernel filter that logs rendered prompts to the agent task store.
/// </summary>
public class PromptRenderLoggerFilter : IPromptRenderFilter
{
    private readonly IAgentTaskStore _taskStore;
    private readonly IAgentTaskContext _context;

    /// <summary>
    /// Initializes a new instance of <see cref="PromptRenderLoggerFilter"/>.
    /// </summary>
    /// <param name="taskStore">The agent task store.</param>
    /// <param name="context">The scoped agent task context.</param>
    public PromptRenderLoggerFilter(IAgentTaskStore taskStore, IAgentTaskContext context)
    {
        _taskStore = taskStore;
        _context = context;
    }

    /// <inheritdoc />
    public async Task OnPromptRenderAsync(PromptRenderContext context, Func<PromptRenderContext, Task> next)
    {
        await next(context);

        var taskId = _context.TaskId;
        if (taskId != Guid.Empty)
        {
            await _taskStore.LogAsync(taskId, LogLevelNames.Reasoning, $"Prompt Sent to LLM:\n{context.RenderedPrompt}");
        }
    }
}
