using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.Plugins.BuiltIn.Required;

/// <summary>
/// Built-in required plugin providing system-level information to agents.
/// Gives agents awareness of the current date/time, which is essential for
/// time-dependent tasks (scheduling, date calculations, relative time references).
/// </summary>
public class SystemInfoAgentPlugin : PluginBase
{
    public override string Id => "SystemInfo";
    public override string DisplayName => "System Info";
    public override string Description => "Provides system information like current date/time. Essential for time-aware tasks, date calculations, and understanding \"today\", \"yesterday\", etc.";
    public override string Version => "1.0.0";
    public override string Author => "SemanticAgentBroker";
    public override string Icon => "schedule";
    public override IReadOnlyList<string> Tags => ["system", "datetime", "info"];
    public override PluginCategory Category => PluginCategory.BuiltInRequired;

    public override IReadOnlyList<IPluginMethod> GetMethods() =>
    [
        new GetCurrentDateTimeMethod(),
        new GetCurrentUtcDateTimeMethod()
    ];
}

/// <summary>
/// Returns the current local date/time with timezone offset in ISO 8601 format.
/// </summary>
public class GetCurrentDateTimeMethod : PluginMethodBase
{
    public override string Id => "GetCurrentDateTime";
    public override string DisplayName => "Get Current Date/Time";
    public override string Description => "Returns the current local date and time with timezone offset in ISO 8601 format (e.g. '2026-04-25T11:00:45+02:00'). Use this to understand what 'today', 'yesterday', 'last week' means.";
    public override string ReturnDescription => "Current local date/time in ISO 8601 format with timezone offset.";

    public override IReadOnlyList<PluginParameter> Parameters => [];

    public override Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        return Task.FromResult(now.ToString("o"));
    }
}

/// <summary>
/// Returns the current UTC date/time in ISO 8601 format.
/// </summary>
public class GetCurrentUtcDateTimeMethod : PluginMethodBase
{
    public override string Id => "GetCurrentUtcDateTime";
    public override string DisplayName => "Get Current UTC Date/Time";
    public override string Description => "Returns the current UTC date and time in ISO 8601 format (e.g. '2026-04-25T09:00:45Z'). Use this for timezone-independent timestamps.";
    public override string ReturnDescription => "Current UTC date/time in ISO 8601 format.";

    public override IReadOnlyList<PluginParameter> Parameters => [];

    public override Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var utcNow = DateTimeOffset.UtcNow;
        return Task.FromResult(utcNow.ToString("o"));
    }
}
