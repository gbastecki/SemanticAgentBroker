namespace SemanticAgentBroker.Sdk;

/// <summary>
/// Categorizes plugins by their origin and lifecycle rules.
/// </summary>
public enum PluginCategory
{
    /// <summary>Custom plugins from external DLLs. Disabled by default, can be enabled/disabled by users.</summary>
    Custom,
    /// <summary>Built-in optional plugins shipped with the framework. Disabled by default, can be enabled/disabled.</summary>
    BuiltInOptional,
    /// <summary>Built-in required plugins. Always enabled, cannot be disabled. Application throws if not found.</summary>
    BuiltInRequired
}

/// <summary>
/// Represents a loadable plugin module containing one or more methods
/// that agents can use as tools. Each DLL in the plugins directory
/// should contain at least one class implementing this interface.
/// </summary>
public interface IAgentPlugin : IDisposable
{
    /// <summary>Unique plugin identifier (e.g., "PythonSandbox", "HttpClient", "DatabaseQuery").</summary>
    string Id { get; }

    /// <summary>Human-readable display name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Description of the plugin's purpose and capabilities.</summary>
    string Description { get; }

    /// <summary>Semantic version string (e.g., "1.2.0").</summary>
    string Version { get; }

    /// <summary>Author or organization name.</summary>
    string Author { get; }

    /// <summary>Material icon name for UI display (e.g., "code", "cloud", "storage").</summary>
    string Icon { get; }

    /// <summary>
    /// Optional tags for categorization and search (e.g., "data", "networking", "ai").
    /// </summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Explicit list of plugin IDs that this plugin depends on.
    /// If this plugin exports a task, the tools from the required plugins
    /// will also be whitelisted when running that task.
    /// </summary>
    IReadOnlyList<string> RequiredPlugins { get; }

    /// <summary>
    /// The category of this plugin, determining its lifecycle and disableability rules.
    /// BuiltInRequired plugins are auto-injected into every task and cannot be disabled.
    /// </summary>
    PluginCategory Category { get; }

    /// <summary>
    /// Called once when the plugin is loaded into the host.
    /// Use for one-time initialization (connecting to services, validating config, etc.).
    /// </summary>
    /// <param name="context">Plugin context providing access to framework services.</param>
    Task InitializeAsync(IPluginContext context);

    /// <summary>
    /// Returns all methods this plugin exposes to agents.
    /// Each method becomes a callable tool that the LLM can invoke during execution.
    /// </summary>
    IReadOnlyList<IPluginMethod> GetMethods();

    /// <summary>
    /// Returns a list of predefined tasks (use cases) exported by this plugin.
    /// Tasks act as templates that restrict the LLM to a specific subset of internal methods,
    /// promoting safety and focused context.
    /// </summary>
    IReadOnlyList<PluginTaskDef> GetTasks();

    /// <summary>
    /// Optional: returns the configuration schema this plugin expects.
    /// The UI will render a configuration form based on this schema.
    /// Return an empty list if no configuration is needed.
    /// </summary>
    IReadOnlyList<PluginConfigEntry> GetConfigurationSchema() => [];
}
