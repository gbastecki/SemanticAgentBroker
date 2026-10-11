using Microsoft.Extensions.Logging;
using SemanticAgentBroker.Sdk;
namespace SemanticAgentBroker.Core.Features.PluginHost.Implementations;

/// <summary>
/// Implementation of <see cref="IPluginContext"/> that provides framework services to plugins
/// during initialization and execution. A new instance is created per task execution scope.
/// 
/// Configuration is hybrid: DB values override appsettings.json values.
/// </summary>
public class PluginExecutionContext : IPluginContext
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceProvider? _kernelServices;
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<string, string> _configuration;

    /// <summary>Absolute path to the workspace directory on disk.</summary>
    public string WorkspacePath { get; }

    /// <summary>Unique task session identifier.</summary>
    public Guid SessionId { get; }

    /// <summary>Scoped logger for this plugin execution.</summary>
    public ILogger Logger => _logger;

    /// <summary>Active key-value configuration values for this plugin.</summary>
    public IReadOnlyDictionary<string, string> Configuration => _configuration;

    /// <summary>
    /// Initializes a new instance of <see cref="PluginExecutionContext"/>.
    /// </summary>
    /// <param name="workspacePath">Path to the task workspace.</param>
    /// <param name="sessionId">Task session ID.</param>
    /// <param name="serviceProvider">Root service provider.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="configuration">Optional configuration dictionary.</param>
    /// <param name="kernelServices">Optional kernel scoped service provider.</param>
    public PluginExecutionContext(
        string workspacePath,
        Guid sessionId,
        IServiceProvider serviceProvider,
        ILogger logger,
        IReadOnlyDictionary<string, string>? configuration = null,
        IServiceProvider? kernelServices = null)
    {
        WorkspacePath = workspacePath;
        SessionId = sessionId;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration ?? new Dictionary<string, string>();
        _kernelServices = kernelServices;
    }

    /// <summary>
    /// Resolves a service of type <typeparamref name="T"/> from the execution context.
    /// </summary>
    /// <typeparam name="T">The service type to resolve.</typeparam>
    /// <returns>The resolved service instance or null.</returns>
    public T? GetService<T>() where T : class
    {
        return (_kernelServices?.GetService(typeof(T)) ?? _serviceProvider.GetService(typeof(T))) as T;
    }
}

/// <summary>
/// Factory for creating <see cref="PluginExecutionContext"/> instances.
/// Merges configuration from appsettings.json and database.
/// </summary>
public class PluginContextFactory : IPluginContextFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _appConfiguration;

    /// <summary>
    /// Initializes a new instance of <see cref="PluginContextFactory"/>.
    /// </summary>
    /// <param name="serviceProvider">The root service provider.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="appConfiguration">The application configuration.</param>
    public PluginContextFactory(IServiceProvider serviceProvider, ILoggerFactory loggerFactory, Microsoft.Extensions.Configuration.IConfiguration appConfiguration)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        _appConfiguration = appConfiguration;
    }

    /// <summary>
    /// Creates an initialization context (used during plugin loading, before any task exists).
    /// </summary>
    public PluginExecutionContext CreateInitContext()
    {
        return new PluginExecutionContext(
            workspacePath: string.Empty,
            sessionId: Guid.Empty,
            serviceProvider: _serviceProvider,
            logger: _loggerFactory.CreateLogger("PluginInit"));
    }

    /// <summary>
    /// Creates an execution context for a specific task and plugin.
    /// </summary>
    /// <param name="workspacePath">Absolute workspace path for the current task.</param>
    /// <param name="sessionId">Current task session ID.</param>
    /// <param name="pluginId">Plugin ID for scoped logging and configuration.</param>
    /// <param name="dbConfig">Plugin configuration values from the database (overrides appsettings).</param>
    /// <param name="appSettingsConfig">Plugin configuration from appsettings.json (fallback).</param>
    /// <param name="kernelServices">Optional kernel scoped service provider.</param>
    public PluginExecutionContext CreateExecutionContext(
        string workspacePath,
        Guid sessionId,
        string pluginId,
        IReadOnlyDictionary<string, string>? dbConfig = null,
        IReadOnlyDictionary<string, string>? appSettingsConfig = null,
        IServiceProvider? kernelServices = null)
    {
        // Hybrid: appsettings as base, DB values override
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var configSection = _appConfiguration.GetSection($"AgentSystem:Plugins:{pluginId}");
        foreach (var child in configSection.GetChildren())
        {
            if (child.Value != null)
            {
                merged[child.Key] = child.Value;
            }
        }

        if (appSettingsConfig != null)
        {
            foreach (var kvp in appSettingsConfig)
                merged[kvp.Key] = kvp.Value;
        }

        if (dbConfig != null)
        {
            foreach (var kvp in dbConfig)
                merged[kvp.Key] = kvp.Value;
        }

        return new PluginExecutionContext(
            workspacePath,
            sessionId,
            _serviceProvider,
            _loggerFactory.CreateLogger($"Plugin.{pluginId}"),
            merged,
            kernelServices);
    }
}
