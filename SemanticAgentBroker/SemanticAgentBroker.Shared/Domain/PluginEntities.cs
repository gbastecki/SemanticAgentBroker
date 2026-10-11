using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.Shared.Domain;

/// <summary>
/// Tracks the runtime load state of a plugin.
/// </summary>
public enum PluginLoadState
{
    /// <summary>Plugin DLL found on disk and successfully loaded into memory.</summary>
    Loaded,
    /// <summary>Plugin was enabled but DLL is missing from disk. Will auto-load on next restart if found.</summary>
    NotLoaded,
    /// <summary>New plugin DLL discovered but not yet enabled by user.</summary>
    Discovered
}

public class TaskEnabledState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PluginId { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

// ── Plugin Persistence ─────────────────────────────────────────────────────

/// <summary>
/// Persisted record of a loaded plugin DLL. Tracks metadata, source DLL,
/// enabled state, and a cached method manifest for quick UI display.
/// </summary>
public class PluginRegistration
{
    /// <summary>Plugin ID (matches <see cref="Sdk.IAgentPlugin.Id"/>).</summary>
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Icon { get; set; } = "extension";
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>Filename of the source DLL (e.g., "MyCustomPlugin.dll").</summary>
    public string DllFileName { get; set; } = string.Empty;

    /// <summary>SHA256 hash of the DLL for integrity / change detection.</summary>
    public string DllHash { get; set; } = string.Empty;

    /// <summary>Whether this plugin is currently enabled and available for agent assignment.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Runtime load state tracking the plugin's lifecycle.</summary>
    public PluginLoadState LoadState { get; set; } = PluginLoadState.Discovered;

    /// <summary>Built-in plugins ship with the framework and cannot be deleted.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Plugin category determining lifecycle and disableability rules.</summary>
    public PluginCategory Category { get; set; } = PluginCategory.Custom;

    public DateTime LoadedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastErrorAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// Serialized method manifest (JSON) for quick UI display without loading the DLL.
    /// Contains an array of method IDs, names, descriptions, and parameters.
    /// </summary>
    public string MethodManifestJson { get; set; } = "[]";

    /// <summary>
    /// Dependencies this plugin explicitly requests.
    /// </summary>
    public IReadOnlyList<string> RequiredPlugins { get; set; } = [];

    /// <summary>
    /// Serialized list of PluginTaskDef objects representing template tasks exported by this plugin.
    /// </summary>
    public string TasksJson { get; set; } = "[]";

    /// <summary>
    /// Plugin-specific configuration values stored in the database.
    /// Overrides appsettings.json values for the same keys.
    /// </summary>
    public List<PluginConfigValue> ConfigValues { get; set; } = new();
}

/// <summary>
/// Persisted key-value configuration for a plugin.
/// </summary>
public class PluginConfigValue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PluginId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsSensitive { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public PluginRegistration? Plugin { get; set; }
}
