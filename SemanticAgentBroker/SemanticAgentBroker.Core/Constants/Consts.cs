namespace SemanticAgentBroker.Core.Constants;

/// <summary>
/// Global constants and default configurations for SemanticAgentBroker.
/// </summary>
public static class Consts
{
    // API Routes
    /// <summary>API route for task endpoints.</summary>
    public const string ApiTasks = "api/tasks";

    /// <summary>API route for plugin management endpoints.</summary>
    public const string ApiPlugins = "api/plugins";

    /// <summary>API route for agent definition endpoints.</summary>
    public const string ApiAgents = "api/agents";

    // Defaults
    /// <summary>Default Windows Docker named pipe URI.</summary>
    public const string DefaultDockerUri = "npipe://./pipe/docker_engine";

    /// <summary>Default Linux Docker socket URI.</summary>
    public const string DefaultLinuxDockerUri = "unix:///var/run/docker.sock";

    /// <summary>Default local directory path for agent workspaces.</summary>
    public const string DefaultWorkspacesPath = "TaskWorkspaces";

    /// <summary>Default working directory path inside sandbox containers.</summary>
    public const string DefaultContainerDataPath = "/app/workdir";

    /// <summary>Default Docker image tag for the Python sandbox.</summary>
    public const string DefaultPythonImage = "ai-sandbox-python:latest";

    // Core Plugin IDs
    /// <summary>Plugin ID for the Python Sandbox plugin.</summary>
    public const string PluginPythonSandbox = "PythonSandbox";

    /// <summary>Plugin ID for the File I/O plugin.</summary>
    public const string PluginFileIO = "FileIO";

    /// <summary>Plugin ID for the Workspace Info plugin.</summary>
    public const string PluginWorkspaceInfo = "WorkspaceInfo";

    // Misc
    /// <summary>Default maximum context token limit before triggering compaction.</summary>
    public const int DefaultMaxContextTokens = 16384;


    /// <summary>
    /// Estimated token overhead added by chat templates per message (e.g. delimiters and markers).
    /// </summary>
    public const int PerMessageTokenOverhead = 20;

    /// <summary>Maximum number of format retry attempts when LLM produces invalid ReAct formatting.</summary>
    public const int MaxFormatRetries = 3;
}
