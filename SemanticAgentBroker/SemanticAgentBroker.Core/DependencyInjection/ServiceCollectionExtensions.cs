using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.Core.Features.Agents;
using SemanticAgentBroker.Core.Features.Agents.Implementations;
using SemanticAgentBroker.Core.Features.KernelProvider;
using SemanticAgentBroker.Core.Features.KernelProvider.Implementations;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Core.Features.PluginHost.Implementations;
using SemanticAgentBroker.Core.Features.TaskState;
using SemanticAgentBroker.Core.Features.TaskState.Implementations;
using SemanticAgentBroker.Core.Features.TokenCounting;
using SemanticAgentBroker.Core.Features.TokenCounting.Implementations;
using SemanticAgentBroker.Core.Filters;
using SemanticAgentBroker.Core.Infrastructure.Http;
using SemanticAgentBroker.Core.LoadBalancing;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.Core.DependencyInjection;

/// <summary>
/// Extension methods to register SemanticAgentBroker.Core services into a DI container.
/// This is the primary integration point for consuming applications.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all core SemanticAgentBroker services, configuration, and load balancing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration root.</param>
    /// <returns>An <see cref="AgentBrokerBuilder"/> for further customization.</returns>
    public static AgentBrokerBuilder AddSemanticAgentBrokerCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Configuration Binding (Configure options pattern) ─────────────
        services.Configure<AiProviderOptions>(configuration.GetSection("AiProviders"));
        services.Configure<AgentSystemOptions>(configuration.GetSection("AgentSystem"));

        var aiOptions = configuration.GetSection("AiProviders").Get<AiProviderOptions>() ?? new AiProviderOptions();
        foreach (var conn in aiOptions.Connections)
        {
            foreach (var model in conn.Models)
            {
                if (!string.IsNullOrEmpty(model.TokenCounting?.TokenizerJsonPath))
                {
                    model.TokenCounting.TokenizerJsonPath = PathResolutionHelper.ResolvePath(model.TokenCounting.TokenizerJsonPath);
                }
            }
        }
        services.AddSingleton(aiOptions);

        var systemOptions = configuration.GetSection("AgentSystem").Get<AgentSystemOptions>() ?? new AgentSystemOptions();
        systemOptions.TaskWorkspacesPath = PathResolutionHelper.ResolvePath(systemOptions.TaskWorkspacesPath);
        systemOptions.TraceLogsDirectory = PathResolutionHelper.ResolvePath(systemOptions.TraceLogsDirectory);
        systemOptions.PluginsDirectory = PathResolutionHelper.ResolvePath(systemOptions.PluginsDirectory);
        systemOptions.DatabaseConnectionString = PathResolutionHelper.ResolveConnectionString(systemOptions.DatabaseConnectionString);
        systemOptions.AuthDatabaseConnectionString = PathResolutionHelper.ResolveConnectionString(systemOptions.AuthDatabaseConnectionString);
        systemOptions.TokensDatabaseConnectionString = PathResolutionHelper.ResolveConnectionString(systemOptions.TokensDatabaseConnectionString);
        services.AddSingleton(systemOptions);


        // Expose sub-options for plugins that need them
        services.AddSingleton(aiOptions.PythonSandbox);
        services.AddSingleton(aiOptions.Inference);
        services.AddSingleton(aiOptions.HealthCheck);

        // ── Core Services ──────────────────────────────────────────────────
        services.AddSingleton<IKernelProviderService, KernelProviderService>();
        services.AddSingleton<IWorkspaceLockManager, WorkspaceLockManager>();
        services.AddSingleton<SemanticAgentBroker.Sdk.IWorkspaceLockManager>(sp => sp.GetRequiredService<IWorkspaceLockManager>());
        services.AddScoped<IAgentTaskContext, AgentTaskContext>();
        services.AddSingleton<ITokenCounter, MlTokenCounter>();
        services.AddScoped<ITaskStateManager, TaskStateManager>();

        // ── Dynamic Agent Registry ─────────────────────────────────────────
        services.AddSingleton<IAgentRegistry, AgentRegistry>();

        // ── Plugin Host System ─────────────────────────────────────────────
        services.AddSingleton<IPluginRegistry, PluginRegistry>();
        services.AddScoped<IPluginContextFactory, PluginContextFactory>();
        services.AddSingleton<IPluginLoader, PluginLoader>(sp =>
        {
            var pluginsDir = systemOptions.PluginsDirectory;
            if (!Path.IsPathRooted(pluginsDir))
                pluginsDir = Path.Combine(AppContext.BaseDirectory, pluginsDir);

            return new PluginLoader(
                pluginsDir,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PluginLoader>>());
        });

        // ── Load Balancing ─────────────────────────────────────────────────
        services.AddSingleton<EndpointHealthCheckService>();
        services.AddSingleton<IEndpointHealthMonitor>(sp => sp.GetRequiredService<EndpointHealthCheckService>());
        services.AddHostedService(sp => sp.GetRequiredService<EndpointHealthCheckService>());
        services.AddSingleton<IConnectionLoadBalancer, LeastUsedConnectionLoadBalancer>();

        // ── Kernel Filters (scoped - they depend on scoped AgentTaskContext) ──
        services.AddScoped<Microsoft.SemanticKernel.IFunctionInvocationFilter, FunctionInvocationLoggerFilter>();
        services.AddScoped<Microsoft.SemanticKernel.IPromptRenderFilter, PromptRenderLoggerFilter>();

        // ── HTTP Clients for AI Providers ──────────────────────────────────
        foreach (var conn in aiOptions.Connections)
        {
            services.AddHttpClient(conn.Id)
                .ConfigureAiProviderClient(conn, aiOptions.DefaultTimeoutMinutes, systemOptions.TraceLogsDirectory);
        }

        // ── HTTP Client for Optional HttpClient Agent Plugin (Non-redirecting & DNS Rebinding SSRF Protection) ──
        services.AddHttpClient("HttpClientAgentPlugin")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var host = context.DnsEndPoint.Host;
                    var port = context.DnsEndPoint.Port;
                    System.Net.IPAddress[] addresses;
                    if (System.Net.IPAddress.TryParse(host, out var directIp))
                    {
                        addresses = [directIp];
                    }
                    else
                    {
                        var entry = await System.Net.Dns.GetHostEntryAsync(host, cancellationToken);
                        addresses = entry.AddressList;
                    }

                    foreach (var ip in addresses)
                    {
                        if (SemanticAgentBroker.Shared.Utilities.NetworkSecurityUtils.IsPrivateOrLoopbackIp(ip))
                        {
                            throw new HttpRequestException($"HTTP request blocked by SSRF security policy: Target host '{host}' resolves to private/loopback IP address '{ip}'.", null, System.Net.HttpStatusCode.Forbidden);
                        }
                    }
                    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(addresses, port, cancellationToken);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            });

        return new AgentBrokerBuilder(services, aiOptions, systemOptions);
    }
}

/// <summary>
/// Fluent builder for additional customization after calling <see cref="ServiceCollectionExtensions.AddSemanticAgentBrokerCore"/>.
/// </summary>
public class AgentBrokerBuilder
{
    /// <summary>The underlying service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The resolved AI provider options.</summary>
    public AiProviderOptions AiOptions { get; }

    /// <summary>The resolved agent system options.</summary>
    public AgentSystemOptions SystemOptions { get; }

    internal AgentBrokerBuilder(IServiceCollection services, AiProviderOptions aiOptions, AgentSystemOptions systemOptions)
    {
        Services = services;
        AiOptions = aiOptions;
        SystemOptions = systemOptions;
    }
}
