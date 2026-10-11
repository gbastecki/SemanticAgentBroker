namespace SemanticAgentBroker.Sdk;

/// <summary>
/// A single invocable method within a plugin. This becomes a tool that the LLM
/// can call during agent execution via the Semantic Kernel function system.
/// </summary>
public interface IPluginMethod
{
    /// <summary>Method identifier, unique within the plugin (e.g., "ExecuteScript", "ReadFile").</summary>
    string Id { get; }

    /// <summary>Human-readable name for UI display.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Description used by the LLM to understand what this method does.
    /// Be specific - this directly impacts the LLM's tool selection accuracy.
    /// </summary>
    string Description { get; }

    /// <summary>Parameter definitions for this method.</summary>
    IReadOnlyList<PluginParameter> Parameters { get; }

    /// <summary>Human-readable description of what this method returns.</summary>
    string ReturnDescription { get; }

    /// <summary>
    /// Whether this method performs destructive or irreversible operations.
    /// Dangerous methods trigger a user approval gate before execution.
    /// </summary>
    bool IsDangerous { get; }

    /// <summary>
    /// Execute the method with the provided arguments.
    /// </summary>
    /// <param name="arguments">Dictionary of argument name → value (all values are strings from JSON).</param>
    /// <param name="context">Plugin context for the current execution scope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>String result to feed back into the LLM as an observation.</returns>
    Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct = default);
}
