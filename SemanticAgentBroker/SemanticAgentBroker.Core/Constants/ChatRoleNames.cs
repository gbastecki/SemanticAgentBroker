namespace SemanticAgentBroker.Core.Constants;

/// <summary>
/// Well-known chat message role identifiers used throughout the ReAct pipeline.
/// </summary>
public static class ChatRoleNames
{
    /// <summary>System role for system instructions.</summary>
    public const string System = "system";

    /// <summary>User role for user prompts and instructions.</summary>
    public const string User = "user";

    /// <summary>Assistant role for model outputs.</summary>
    public const string Assistant = "assistant";
}
