namespace SemanticAgentBroker.Core.Constants;

/// <summary>
/// Keyword tokens that define the structured ReAct (Reasoning and Acting) output format.
/// These are used for both generating prompts and parsing LLM responses.
/// </summary>
public static class ReactFormatTokens
{
    /// <summary>Thought token prefix.</summary>
    public const string Thought = "Thought:";

    /// <summary>Action / tool name token prefix.</summary>
    public const string Action = "Action:";

    /// <summary>Action input / JSON arguments token prefix.</summary>
    public const string ActionInput = "ActionInput:";

    /// <summary>Final answer token prefix.</summary>
    public const string FinalAnswer = "FinalAnswer:";

    /// <summary>Plan token prefix.</summary>
    public const string Plan = "Plan:";

    /// <summary>Observation / tool result token prefix.</summary>
    public const string Observation = "Observation:";

    /// <summary>The action value the LLM uses to indicate no tool is needed.</summary>
    public const string ActionNone = "None";
}
