namespace SemanticAgentBroker.Sdk;

/// <summary>
/// Optional abstract base class for plugins. Provides common patterns and reduces boilerplate.
/// Plugin authors can implement <see cref="IAgentPlugin"/> directly if they prefer full control.
/// </summary>
public abstract class PluginBase : IAgentPlugin
{
    private IPluginContext? _context;

    /// <summary>
    /// The plugin context, available after <see cref="InitializeAsync"/> is called.
    /// </summary>
    protected IPluginContext Context => _context ?? throw new InvalidOperationException("Plugin context is not initialized. InitializeAsync must be called first.");

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public virtual string Version => "1.0.0";

    /// <inheritdoc />
    public virtual string Author => "Unknown";

    /// <inheritdoc />
    public virtual string Icon => "extension";

    /// <inheritdoc />
    public virtual IReadOnlyList<string> Tags => [];

    /// <inheritdoc />
    public virtual IReadOnlyList<string> RequiredPlugins => [];

    /// <inheritdoc />
    public virtual PluginCategory Category => PluginCategory.Custom;

    /// <inheritdoc />
    public virtual Task InitializeAsync(IPluginContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public abstract IReadOnlyList<IPluginMethod> GetMethods();

    /// <inheritdoc />
    public virtual IReadOnlyList<PluginTaskDef> GetTasks() => [];

    /// <inheritdoc />
    public virtual IReadOnlyList<PluginConfigEntry> GetConfigurationSchema() => [];

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and - optionally - managed resources.
    /// </summary>
    /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        // To be overridden by derived classes if needed.
    }
}
