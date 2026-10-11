using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.Core.Features.Agents.Implementations;

/// <inheritdoc />
public class AgentTaskContext : IAgentTaskContext
{
    private readonly AiProviderOptions _options;
    private readonly AgentSystemOptions _sysOptions;

    /// <summary>
    /// Initializes a new instance of <see cref="AgentTaskContext"/>.
    /// </summary>
    /// <param name="options">The AI provider configuration options.</param>
    /// <param name="sysOptions">The agent system configuration options.</param>
    public AgentTaskContext(AiProviderOptions options, AgentSystemOptions sysOptions)
    {
        _options = options;
        _sysOptions = sysOptions;
    }

    /// <inheritdoc />
    public Guid TaskId { get; private set; }
    
    /// <inheritdoc />
    public string WorkspacePath { get; private set; } = string.Empty;

    /// <inheritdoc />
    public void Initialize(Guid taskId)
    {
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("TaskId cannot be Guid.Empty.", nameof(taskId));
        }

        TaskId = taskId;
        WorkspacePath = GetWorkspacePath(taskId, _sysOptions);
        Directory.CreateDirectory(WorkspacePath);
    }

    /// <summary>
    /// Computes the absolute workspace directory path for a task given the agent system options.
    /// </summary>
    /// <param name="taskId">The task ID.</param>
    /// <param name="sysOptions">The agent system options.</param>
    /// <returns>The resolved absolute directory path.</returns>
    public static string GetWorkspacePath(Guid taskId, AgentSystemOptions sysOptions)
    {
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("TaskId cannot be Guid.Empty.", nameof(taskId));
        }

        ArgumentNullException.ThrowIfNull(sysOptions);

        string baseDir = PathResolutionHelper.ResolvePath(sysOptions.TaskWorkspacesPath);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.GetTempPath();
        }
        else if (!Path.IsPathRooted(baseDir))
        {
            baseDir = Path.Combine(AppContext.BaseDirectory, baseDir);
        }
        return Path.Combine(baseDir, taskId.ToString());
    }


    /// <inheritdoc />
    public void Cleanup()
    {
        if (Directory.Exists(WorkspacePath))
        {
            try
            {
                Directory.Delete(WorkspacePath, recursive: true);
            }
            catch
            {
            }
        }
    }
}
