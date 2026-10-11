namespace SemanticAgentBroker.Shared.Domain;

public class AgentTaskRequest
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public ExecutionMode Mode { get; set; } = ExecutionMode.Auto;

    /// <summary>When true, the engine logs raw LLM output, parsed fields, tool args, and all internal decisions.</summary>
    public bool DebugMode { get; set; } = false;

    /// <summary>When true, everything sent to and from the LLM (the full chat history) is saved to a file.</summary>
    public bool TraceMode { get; set; } = false;

    public bool IsPrivate { get; set; } = false;
    public string? UserName { get; set; }

    /// <summary>
    /// If provided, restricts the agent to ONLY these specific tools (Method Ids from requested tasks + required plugins).
    /// </summary>
    public List<string>? RestrictedMethodIds { get; set; }

    /// <summary>When true, task execution resumes from existing ChatHistory instead of starting from scratch.</summary>
    public bool IsContinuation { get; set; } = false;

    public int? MaxSteps { get; set; }
    public string? PluginTaskId { get; set; }
    public string? SourcePluginId { get; set; }
}
