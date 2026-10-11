namespace SemanticAgentBroker.Core.Constants;

/// <summary>
/// Well-known log level identifiers used in the agent task event system.
/// These are domain-specific log levels (not to be confused with Microsoft.Extensions.Logging.LogLevel).
/// </summary>
public static class LogLevelNames
{
    /// <summary>System-level informational log.</summary>
    public const string System = "System";

    /// <summary>Tool invocation and execution log.</summary>
    public const string Tool = "Tool";

    /// <summary>Reasoning and ReAct thought log.</summary>
    public const string Reasoning = "Reasoning";

    /// <summary>User interaction and prompt log.</summary>
    public const string User = "User";

    /// <summary>Warning log.</summary>
    public const string Warning = "Warning";
}
