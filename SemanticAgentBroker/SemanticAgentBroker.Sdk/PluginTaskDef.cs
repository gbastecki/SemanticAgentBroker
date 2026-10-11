namespace SemanticAgentBroker.Sdk;

/// <summary>
/// Reference to a method in another plugin.
/// </summary>
public class ExternalMethodRef
{
    /// <summary>Identifier of the external plugin.</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>Identifier of the method within the external plugin.</summary>
    public string MethodId { get; set; } = string.Empty;

    /// <summary>Optional safety override for this external method (dangerous or safe).</summary>
    public bool? IsDangerousOverride { get; set; }
}

/// <summary>
/// Safety override configuration for a bound tool.
/// </summary>
public class MethodSafetyOverride
{
    /// <summary>Identifier of the method to override safety for.</summary>
    public string MethodId { get; set; } = string.Empty;

    /// <summary>Whether the method is marked as dangerous.</summary>
    public bool? IsDangerous { get; set; }
}

/// <summary>
/// Represents a predefined task or use case defined by a plugin author.
/// This allows grouping a specific set of methods to accomplish a known task,
/// preventing the LLM from accessing unrelated, potentially dangerous methods
/// when executing this specific workflow.
/// </summary>
public class PluginTaskDef
{
    /// <summary>Unique identifier for this task within the plugin.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable name of the task.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// A descriptive prompt template that tells the LLM what this task is.
    /// This is injected as the initial user prompt or system hint.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The specific method IDs (from this plugin) that the LLM is allowed to use 
    /// while executing this task. Methods not listed here will be hidden.
    /// </summary>
    public IReadOnlyList<string> AllowedMethods { get; set; } = [];

    /// <summary>
    /// System instruction for the auto-generated runtime agent. 
    /// When null, falls back to a generic task-oriented system prompt.
    /// </summary>
    public string? SystemInstruction { get; set; }

    /// <summary>
    /// Cross-plugin method references (e.g., "HttpClient:HttpGet").
    /// </summary>
    public IReadOnlyList<ExternalMethodRef> ExternalMethods { get; set; } = [];

    /// <summary>
    /// Per-method safety overrides for this task.
    /// </summary>
    public IReadOnlyList<MethodSafetyOverride> SafetyOverrides { get; set; } = [];

    /// <summary>
    /// Whether this task is enabled by default when the plugin is enabled.
    /// </summary>
    public bool EnabledByDefault { get; set; } = true;
}
