namespace SemanticAgentBroker.Sdk;

/// <summary>
/// Describes a parameter for a plugin method.
/// Used by the Semantic Kernel adapter to generate function metadata
/// and by the UI to display parameter documentation.
/// </summary>
public class PluginParameter
{
    /// <summary>Parameter name (used as the JSON key in ActionInput).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable description of what this parameter represents.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The .NET type of this parameter (default: string).</summary>
    public Type ParameterType { get; set; } = typeof(string);

    /// <summary>Whether this parameter is required.</summary>
    public bool IsRequired { get; set; } = true;

    /// <summary>Default value when the parameter is optional and not provided.</summary>
    public object? DefaultValue { get; set; }
}

/// <summary>
/// Describes a configuration entry that a plugin requires.
/// Used by the UI to render a configuration form for the plugin.
/// </summary>
public class PluginConfigEntry
{
    /// <summary>Configuration key (e.g., "ApiKey", "BaseUrl").</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Human-readable label for the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Description / help text.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether this config is required for the plugin to function.</summary>
    public bool IsRequired { get; set; } = true;

    /// <summary>Whether this value should be masked in the UI (e.g., passwords, API keys).</summary>
    public bool IsSensitive { get; set; } = false;

    /// <summary>Default value (if any).</summary>
    public string? DefaultValue { get; set; }
}
