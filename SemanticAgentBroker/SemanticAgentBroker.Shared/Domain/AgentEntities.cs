namespace SemanticAgentBroker.Shared.Domain;

// ── Dynamic Agent Definitions ──────────────────────────────────────────────

/// <summary>
/// A runtime-defined agent. Unlike the old <c>IAgentDefinition</c> interface,
/// agents are now created/edited via the UI and stored in the database.
/// Each agent selects specific plugin methods it can use as tools.
/// </summary>
public class AgentDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Unique agent name used for routing (e.g., "DataAnalyst").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable description shown in the UI.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Material icon name for the UI card.</summary>
    public string Icon { get; set; } = "smart_toy";

    /// <summary>CSS accent class for the UI card (e.g., "indigo", "cyan", "emerald", "amber").</summary>
    public string AccentClass { get; set; } = "indigo";

    /// <summary>The core system instruction injected into the agent's prompt.</summary>
    public string SystemInstruction { get; set; } = string.Empty;

    /// <summary>Whether this agent is available for task creation.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Whether this agent was created by the seeding system (cannot be fully deleted, only disabled).</summary>
    public bool IsSeeded { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// The plugin methods this agent has access to during execution.
    /// Only the bound methods are registered as Semantic Kernel functions.
    /// </summary>
    public List<AgentMethodBinding> MethodBindings { get; set; } = new();
}

/// <summary>
/// Join entity binding an agent to a specific method within a specific plugin.
/// This granular binding allows agents to use only selected methods from each plugin.
/// </summary>
public class AgentMethodBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public AgentDefinition? Agent { get; set; }

    /// <summary>Plugin identifier (matches <see cref="PluginRegistration.Id"/>).</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>Method identifier within the plugin (matches <see cref="Sdk.IPluginMethod.Id"/>).</summary>
    public string MethodId { get; set; } = string.Empty;
}
