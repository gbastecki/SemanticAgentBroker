namespace SemanticAgentBroker.Core.Infrastructure.Http;

/// <summary>
/// Ambient execution context storing task-scoped HTTP trace logging state.
/// </summary>
public static class TraceLoggingContext
{
    private static readonly AsyncLocal<bool> _isTraceEnabled = new();
    private static readonly AsyncLocal<Guid?> _currentTaskId = new();

    /// <summary>
    /// Gets or sets whether HTTP payload trace logging is enabled in the current async context.
    /// </summary>
    public static bool IsTraceEnabled
    {
        get => _isTraceEnabled.Value;
        set => _isTraceEnabled.Value = value;
    }

    /// <summary>
    /// Gets or sets the current task ID for naming trace log files in the current async context.
    /// </summary>
    public static Guid? CurrentTaskId
    {
        get => _currentTaskId.Value;
        set => _currentTaskId.Value = value;
    }
}
