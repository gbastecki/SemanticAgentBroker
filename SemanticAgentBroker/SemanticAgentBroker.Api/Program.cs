using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Extensions;
using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.Api.Features.ReAct.Services;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Api.Hubs;
using SemanticAgentBroker.Core.DependencyInjection;
using SemanticAgentBroker.Core.Filters;
using SemanticAgentBroker.Shared.Configuration;

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// ── System Diagnostics Logging (Windows Event Log & Linux systemd journal) ──
if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog(settings =>
    {
#pragma warning disable CA1416
        settings.SourceName = "SemanticAgentBroker";
#pragma warning restore CA1416
    });
}
else if (OperatingSystem.IsLinux())
{
    // On Linux (systemd services / containers), format logs with syslog priority prefixes for journald (journalctl)
    builder.Logging.AddSystemdConsole(options =>
    {
        options.IncludeScopes = true;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    });
}

// ── Core Agent Framework (registers all Core services, config, LB, health checks, plugin host) ──
builder.Services.AddSemanticAgentBrokerCore(builder.Configuration);

// ── Databases (AppDbContext, AuthDbContext, TokenDbContext) ─────────────────
var systemOptions = builder.Configuration.GetSection("AgentSystem").Get<AgentSystemOptions>() ?? new AgentSystemOptions();

builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseSqlite(PathResolutionHelper.ResolveConnectionString(systemOptions.DatabaseConnectionString)));

builder.Services.AddDbContext<AuthDbContext>(opts =>
    opts.UseSqlite(PathResolutionHelper.ResolveConnectionString(systemOptions.AuthDatabaseConnectionString)));

builder.Services.AddDbContext<TokenDbContext>(opts =>
    opts.UseSqlite(PathResolutionHelper.ResolveConnectionString(systemOptions.TokensDatabaseConnectionString)));


// ── Configure Kestrel Server & TLS Endpoints ──────────────────────────────
if (systemOptions.RestApi.IsEnabled)
{
    builder.WebHost.ConfigureKestrel(kestrelOptions =>
    {
        var restApi = systemOptions.RestApi;
        var host = restApi.Host;
        bool isAnyIp = string.IsNullOrWhiteSpace(host) || host == "0.0.0.0" || host == "*";
        bool isLocalhost = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1";

        // 1. Optional HTTP Listener (disabled by default in HTTPS-only mode)
        if (restApi.HttpPort.HasValue && restApi.HttpPort.Value > 0)
        {
            var httpPort = restApi.HttpPort.Value;
            if (isAnyIp)
            {
                kestrelOptions.ListenAnyIP(httpPort);
            }
            else if (isLocalhost)
            {
                kestrelOptions.ListenLocalhost(httpPort);
            }
            else if (System.Net.IPAddress.TryParse(host, out var ip))
            {
                kestrelOptions.Listen(ip, httpPort);
            }
            else
            {
                kestrelOptions.ListenAnyIP(httpPort);
            }
        }

        // 2. HTTPS Listener (default port 5100, uses dotnet dev-certs / configured cert)
        var httpsPort = restApi.HttpsPort ?? (restApi.HttpPort == null ? restApi.Port : null);
        if (httpsPort.HasValue && httpsPort.Value > 0)
        {
            var port = httpsPort.Value;
            Action<Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions> configureListen = listenOptions =>
            {
                var certConfig = systemOptions.Certificate;
                if (certConfig != null)
                {
                    if (!string.IsNullOrEmpty(certConfig.Path) && !string.IsNullOrEmpty(certConfig.Password))
                    {
                        listenOptions.UseHttps(certConfig.Path, certConfig.Password);
                    }
                    else if (!string.IsNullOrEmpty(certConfig.StoreName) &&
                             !string.IsNullOrEmpty(certConfig.StoreLocation) &&
                             !string.IsNullOrEmpty(certConfig.Thumbprint))
                    {
                        if (Enum.TryParse<System.Security.Cryptography.X509Certificates.StoreName>(certConfig.StoreName, out var storeName) &&
                            Enum.TryParse<System.Security.Cryptography.X509Certificates.StoreLocation>(certConfig.StoreLocation, out var storeLocation))
                        {
                            using var store = new System.Security.Cryptography.X509Certificates.X509Store(storeName, storeLocation);
                            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                            var certs = store.Certificates.Find(System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, certConfig.Thumbprint, false);
                            if (certs.Count > 0)
                            {
                                listenOptions.UseHttps(certs[0]);
                            }
                            else
                            {
                                Console.Error.WriteLine($"[WARNING] Certificate with thumbprint '{certConfig.Thumbprint}' not found in store {certConfig.StoreName}/{certConfig.StoreLocation}. Falling back to standard dev cert.");
                                listenOptions.UseHttps();
                            }
                        }
                        else
                        {
                            listenOptions.UseHttps();
                        }
                    }
                    else
                    {
                        listenOptions.UseHttps();
                    }
                }
                else
                {
                    // Uses standard ASP.NET Core developer certificate (trusted via dotnet dev-certs https)
                    listenOptions.UseHttps();
                }
            };

            if (isAnyIp)
            {
                kestrelOptions.ListenAnyIP(port, configureListen);
            }
            else if (isLocalhost)
            {
                kestrelOptions.ListenLocalhost(port, configureListen);
            }
            else if (System.Net.IPAddress.TryParse(host, out var ip))
            {
                kestrelOptions.Listen(ip, port, configureListen);
            }
            else
            {
                kestrelOptions.ListenAnyIP(port, configureListen);
            }
        }
    });
}

// ── Api-layer DI ───────────────────────────────────────────────────────────
builder.Services.AddAuthentication("CustomBearer")
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SemanticAgentBroker.Api.Features.Auth.CustomAuthSchemeHandler>("CustomBearer", _ => { });
builder.Services.AddSingleton<SemanticAgentBroker.Api.Features.Auth.AuthService>();
builder.Services.AddSingleton<TaskOperationsService>();
builder.Services.AddScoped<AgentTaskStore>();
builder.Services.AddScoped<IAgentTaskStore>(sp => sp.GetRequiredService<AgentTaskStore>());
builder.Services.AddTransient<IReactExecutionEngine, ReactExecutionEngine>();
builder.Services.AddTransient<ISubagentExecutor, SubagentExecutor>();
builder.Services.AddTransient<LlmCallService>();
builder.Services.AddTransient<ToolExecutionService>();
builder.Services.AddTransient<PlanningEngine>();
builder.Services.AddSingleton<AgentTaskQueueService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentTaskQueueService>());
builder.Services.AddHostedService<TasksCleanupService>();

// ── Rate Limiting ─────────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("AuthPolicy", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 15,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });
});

// ── Reverse Proxy & Forwarded Headers ──────────────────────────────────────
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    if (systemOptions.RestApi.TrustAllProxies)
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        Console.Error.WriteLine("[SECURITY WARNING] RestApi:TrustAllProxies is enabled. All upstream proxies and X-Forwarded-* headers are trusted.");
    }
    else
    {
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        // Default: Loopback IPv4 and IPv6
        options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
        options.KnownProxies.Add(System.Net.IPAddress.Loopback);

        // Add explicit known proxies
        if (systemOptions.RestApi.KnownProxies != null)
        {
            foreach (var proxy in systemOptions.RestApi.KnownProxies)
            {
                if (System.Net.IPAddress.TryParse(proxy, out var ip))
                {
                    options.KnownProxies.Add(ip);
                }
            }
        }

        // Add explicit known networks (CIDR)
        if (systemOptions.RestApi.KnownNetworks != null)
        {
            foreach (var net in systemOptions.RestApi.KnownNetworks)
            {
                if (System.Net.IPNetwork.TryParse(net, out var network))
                {
                    options.KnownIPNetworks.Add(network);
                }
            }
        }
    }
});

// ── SignalR & CORS ─────────────────────────────────────────────────────────
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", b =>
    {
        var corsOptions = systemOptions.Cors;
        if (corsOptions == null || corsOptions.AllowAll)
        {
            b.SetIsOriginAllowed(_ => true).AllowAnyMethod().AllowAnyHeader().AllowCredentials();
        }
        else
        {
            b.WithOrigins(corsOptions.AllowedOrigins.ToArray())
             .AllowAnyMethod().AllowAnyHeader().AllowCredentials();
        }
    });
});

var app = builder.Build();

// ── Global Exception Handler ───────────────────────────────────────────────
app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var exceptionHandlerPathFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        var ex = exceptionHandlerPathFeature?.Error;
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Unhandled exception processing request {Method} {Path}", context.Request.Method, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An error occurred while processing your request.",
            Detail = app.Environment.IsDevelopment() ? ex?.Message : "An unexpected server error occurred."
        };
        await context.Response.WriteAsJsonAsync(problem);
    });
});

app.UseForwardedHeaders();
app.UseCors("AllowAll");

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
if (systemOptions.RestApi.RedirectHttpToHttps && systemOptions.RestApi.HttpsPort.HasValue)
{
    app.UseHttpsRedirection();
}

app.UseRateLimiter();

// ── Dedicated SignalR Auth Middleware ──────────────────────────────────────
app.UseSignalRAuth();

app.MapHub<AgentLogHub>("/loghub");

await app.InitializeApplicationAsync();
app.MapApiEndpoints();
await app.RunAsync();

public partial class Program { }

namespace SemanticAgentBroker.Api
{
    public class ApiEntryPoint { }
}

