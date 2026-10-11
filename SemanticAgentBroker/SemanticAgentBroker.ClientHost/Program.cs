using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Net.Http.Headers;
using SemanticAgentBroker.Shared.Configuration;
using System.Net;
using System.Security.Cryptography.X509Certificates;

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// ── System Diagnostics Logging (Windows Event Log & Linux systemd journal) ──
if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog(settings =>
    {
#pragma warning disable CA1416
        settings.SourceName = "SemanticAgentBroker.ClientHost";
#pragma warning restore CA1416
    });
}
else if (OperatingSystem.IsLinux())
{
    builder.Logging.AddSystemdConsole(options =>
    {
        options.IncludeScopes = true;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    });
}

// ── Load ClientHost Configuration ─────────────────────────────────────────
var clientHostOptions = builder.Configuration.GetSection("ClientHost").Get<ClientHostOptions>() ?? new ClientHostOptions();

// ── Configure Kestrel Server & TLS Endpoints ──────────────────────────────
builder.WebHost.ConfigureKestrel(kestrelOptions =>
{
    // Resolve listen IP address
    var host = clientHostOptions.Host;
    bool isAnyIp = string.IsNullOrWhiteSpace(host) || host == "0.0.0.0" || host == "*";
    bool isLocalhost = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1";

    // 1. HTTP Listener
    if (clientHostOptions.HttpPort.HasValue && clientHostOptions.HttpPort.Value > 0)
    {
        var httpPort = clientHostOptions.HttpPort.Value;
        if (isAnyIp)
        {
            kestrelOptions.ListenAnyIP(httpPort);
        }
        else if (isLocalhost)
        {
            kestrelOptions.ListenLocalhost(httpPort);
        }
        else if (IPAddress.TryParse(host, out var ip))
        {
            kestrelOptions.Listen(ip, httpPort);
        }
        else
        {
            kestrelOptions.ListenAnyIP(httpPort);
        }
    }

    // 2. HTTPS Listener
    if (clientHostOptions.HttpsPort.HasValue && clientHostOptions.HttpsPort.Value > 0)
    {
        var httpsPort = clientHostOptions.HttpsPort.Value;
        Action<Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions> configureListen = listenOptions =>
        {
            var certConfig = clientHostOptions.Certificate;
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
                    if (Enum.TryParse<StoreName>(certConfig.StoreName, out var storeName) &&
                        Enum.TryParse<StoreLocation>(certConfig.StoreLocation, out var storeLocation))
                    {
                        using var store = new X509Store(storeName, storeLocation);
                        store.Open(OpenFlags.ReadOnly);
                        var certs = store.Certificates.Find(X509FindType.FindByThumbprint, certConfig.Thumbprint, false);
                        if (certs.Count > 0)
                        {
                            listenOptions.UseHttps(certs[0]);
                        }
                        else
                        {
                            throw new InvalidOperationException($"Certificate with thumbprint '{certConfig.Thumbprint}' not found in store {certConfig.StoreName}/{certConfig.StoreLocation}. Cannot start HTTPS listener securely.");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Invalid certificate store configuration: StoreName='{certConfig.StoreName}', StoreLocation='{certConfig.StoreLocation}'.");
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
        };

        if (isAnyIp)
        {
            kestrelOptions.ListenAnyIP(httpsPort, configureListen);
        }
        else if (isLocalhost)
        {
            kestrelOptions.ListenLocalhost(httpsPort, configureListen);
        }
        else if (IPAddress.TryParse(host, out var ip))
        {
            kestrelOptions.Listen(ip, httpsPort, configureListen);
        }
        else
        {
            kestrelOptions.ListenAnyIP(httpsPort, configureListen);
        }
    }
});

// ── Response Compression (Brotli + Gzip) ──────────────────────────────────
// Note: MapStaticAssets handles static asset compression. Dynamic runtime compression is disabled in Development.
if (clientHostOptions.Compression.EnableCompression && !builder.Environment.IsDevelopment())
{
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = clientHostOptions.Compression.EnableForHttps;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
        {
            "application/wasm",
            "application/octet-stream",
            "application/javascript",
            "text/javascript",
            "text/css",
            "text/html",
            "application/json",
            "image/svg+xml",
            "font/woff",
            "font/woff2",
            "font/ttf",
            "application/font-woff",
            "application/font-woff2"
        });
    });
}

// ── CORS Policy ───────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("ClientHostCorsPolicy", policy =>
    {
        var cors = clientHostOptions.Cors;
        if (cors.AllowAll)
        {
            policy.SetIsOriginAllowed(_ => true)
                  .AllowAnyMethod()
                  .AllowAnyHeader();

            if (cors.AllowCredentials)
            {
                policy.AllowCredentials();
            }
        }
        else
        {
            if (cors.AllowedOrigins.Count > 0)
            {
                policy.WithOrigins(cors.AllowedOrigins.ToArray());
            }

            if (cors.AllowedMethods.Count > 0)
            {
                policy.WithMethods(cors.AllowedMethods.ToArray());
            }
            else
            {
                policy.AllowAnyMethod();
            }

            if (cors.AllowedHeaders.Count > 0)
            {
                policy.WithHeaders(cors.AllowedHeaders.ToArray());
            }
            else
            {
                policy.AllowAnyHeader();
            }

            if (cors.AllowCredentials)
            {
                policy.AllowCredentials();
            }
        }
    });
});

// ── Forwarded Headers (Reverse Proxy) ─────────────────────────────────────
if (clientHostOptions.ForwardedHeaders.Enabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

        if (clientHostOptions.ForwardedHeaders.TrustAllProxies)
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
        }
        else
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            // Default: loopback addresses
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
            options.KnownProxies.Add(IPAddress.Loopback);

            // Add configured proxies
            if (clientHostOptions.ForwardedHeaders.KnownProxies != null)
            {
                foreach (var proxy in clientHostOptions.ForwardedHeaders.KnownProxies)
                {
                    if (IPAddress.TryParse(proxy, out var ip))
                    {
                        options.KnownProxies.Add(ip);
                    }
                }
            }

            // Add configured CIDR networks
            if (clientHostOptions.ForwardedHeaders.KnownNetworks != null)
            {
                foreach (var net in clientHostOptions.ForwardedHeaders.KnownNetworks)
                {
                    if (System.Net.IPNetwork.TryParse(net, out var network))
                    {
                        options.KnownIPNetworks.Add(network);
                    }
                }
            }
        }
    });
}

var app = builder.Build();

// ── Diagnostics / Global Exception Logging ────────────────────────────────
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Unhandled error serving request {Method} {Path}", context.Request.Method, context.Request.Path);
        throw;
    }
});

// ── Apply Forwarded Headers ───────────────────────────────────────────────
if (clientHostOptions.ForwardedHeaders.Enabled)
{
    app.UseForwardedHeaders();
}

// ── Apply Security Response Headers ───────────────────────────────────────
app.Use(async (context, next) =>
{
    var sec = clientHostOptions.SecurityHeaders;
    var headers = context.Response.Headers;

    if (!string.IsNullOrEmpty(sec.XContentTypeOptions))
    {
        headers["X-Content-Type-Options"] = sec.XContentTypeOptions;
    }

    if (!string.IsNullOrEmpty(sec.XFrameOptions))
    {
        headers["X-Frame-Options"] = sec.XFrameOptions;
    }

    if (!string.IsNullOrEmpty(sec.ReferrerPolicy))
    {
        headers["Referrer-Policy"] = sec.ReferrerPolicy;
    }

    if (!string.IsNullOrEmpty(sec.ContentSecurityPolicy))
    {
        headers["Content-Security-Policy"] = sec.ContentSecurityPolicy;
    }

    if (!string.IsNullOrEmpty(sec.PermissionsPolicy))
    {
        headers["Permissions-Policy"] = sec.PermissionsPolicy;
    }

    if (sec.EnableHsts && context.Request.IsHttps)
    {
        var maxAge = (long)sec.HstsMaxAgeDays * 24 * 3600;
        var hstsVal = $"max-age={maxAge}";
        if (sec.HstsIncludeSubdomains) hstsVal += "; includeSubDomains";
        if (sec.HstsPreload) hstsVal += "; preload";
        headers["Strict-Transport-Security"] = hstsVal;
    }

    await next();
});

// ── HTTPS Redirection ─────────────────────────────────────────────────────
if (clientHostOptions.RedirectHttpToHttps && clientHostOptions.HttpsPort.HasValue)
{
    app.UseHttpsRedirection();
}

// ── Response Compression ──────────────────────────────────────────────────
if (clientHostOptions.Compression.EnableCompression && !app.Environment.IsDevelopment())
{
    app.UseResponseCompression();
}

// ── CORS ──────────────────────────────────────────────────────────────────
app.UseCors("ClientHostCorsPolicy");

// ── Health Check Endpoint ─────────────────────────────────────────────────
if (clientHostOptions.HealthCheck.Enabled)
{
    app.MapGet(clientHostOptions.HealthCheck.Path, () => Results.Ok(new
    {
        status = "Healthy",
        service = "SemanticAgentBroker.ClientHost",
        apiBaseUrl = clientHostOptions.ClientSettings.ApiBaseUrl,
        timestamp = DateTimeOffset.UtcNow
    }));
}

// ── Dynamic Client Configuration Endpoint (/appsettings.json) ─────────────
// Delivers the target API address (Server B) to the Blazor WASM client at runtime
var dynamicConfigHandler = (HttpContext context) =>
{
    var configDict = new Dictionary<string, object>
    {
        ["ApiBaseUrl"] = clientHostOptions.ClientSettings.ApiBaseUrl
    };

    if (!string.IsNullOrEmpty(clientHostOptions.ClientSettings.EnvironmentName))
    {
        configDict["Environment"] = clientHostOptions.ClientSettings.EnvironmentName;
    }

    if (clientHostOptions.ClientSettings.CustomSettings != null)
    {
        foreach (var kvp in clientHostOptions.ClientSettings.CustomSettings)
        {
            configDict[kvp.Key] = kvp.Value;
        }
    }

    context.Response.ContentType = "application/json; charset=utf-8";
    context.Response.Headers[HeaderNames.CacheControl] = "no-cache, no-store, must-revalidate";
    context.Response.Headers[HeaderNames.Pragma] = "no-cache";
    context.Response.Headers[HeaderNames.Expires] = "0";

    return Results.Json(configDict);
};

// Intercept dynamic client configuration before UseStaticFiles can serve physical wwwroot/appsettings.json
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (HttpMethods.IsGet(context.Request.Method) &&
        (string.Equals(path, "/appsettings.json", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(path, "/config", StringComparison.OrdinalIgnoreCase) ||
         (path.StartsWith("/appsettings.", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))))
    {
        var result = dynamicConfigHandler(context);
        await result.ExecuteAsync(context);
        return;
    }

    await next();
});

app.MapGet("/appsettings.json", dynamicConfigHandler);
app.MapGet("/appsettings.{environment}.json", dynamicConfigHandler);
app.MapGet("/config", dynamicConfigHandler);

// ── Blazor WASM Static Web Assets & Framework Files ───────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.MapStaticAssets();

var frameworkMaxAge = clientHostOptions.StaticFiles.FrameworkCacheMaxAgeSeconds;
var htmlMaxAge = clientHostOptions.StaticFiles.HtmlCacheMaxAgeSeconds;

var staticFileOptions = new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? string.Empty;

        // Long-term immutable caching for fingerprinted framework and static assets
        if (path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers[HeaderNames.CacheControl] =
                $"public, max-age={frameworkMaxAge}, immutable";
        }
        // Zero caching for dynamic configuration and root index.html to guarantee instantaneous deployment updates
        else if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                 string.IsNullOrEmpty(path) || path == "/")
        {
            ctx.Context.Response.Headers[HeaderNames.CacheControl] =
                $"public, max-age={htmlMaxAge}, must-revalidate";
        }
    }
};

app.UseStaticFiles(staticFileOptions);

// ── Fallback SPA Routing ──────────────────────────────────────────────────
app.MapFallbackToFile("index.html", staticFileOptions);

Console.WriteLine($"[SemanticAgentBroker.ClientHost] Starting Kestrel server...");
Console.WriteLine($"  - Host: {clientHostOptions.Host}");
if (clientHostOptions.HttpPort.HasValue)
{
    Console.WriteLine($"  - Listening HTTP : http://{clientHostOptions.Host}:{clientHostOptions.HttpPort.Value}");
}
if (clientHostOptions.HttpsPort.HasValue)
{
    Console.WriteLine($"  - Listening HTTPS: https://{clientHostOptions.Host}:{clientHostOptions.HttpsPort.Value}");
}
Console.WriteLine($"  - Configured Backend ApiBaseUrl: {clientHostOptions.ClientSettings.ApiBaseUrl}");

await app.RunAsync();
