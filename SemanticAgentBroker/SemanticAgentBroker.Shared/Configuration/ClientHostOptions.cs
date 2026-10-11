namespace SemanticAgentBroker.Shared.Configuration;

/// <summary>
/// Root configuration options for the standalone Blazor WASM Kestrel ClientHost (Server A).
/// </summary>
public class ClientHostOptions
{
    /// <summary>
    /// Hostname or IP address to bind Kestrel listeners to (e.g., "0.0.0.0", "localhost", "127.0.0.1", "*").
    /// Default is "0.0.0.0" (all IPv4 and IPv6 interfaces).
    /// </summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>
    /// HTTP port for serving the Blazor WASM client assets. Default is null (disabled in HTTPS-only mode).
    /// Set to null or 0 to disable HTTP listening.
    /// </summary>
    public int? HttpPort { get; set; } = null;

    /// <summary>
    /// Optional HTTPS port for serving over TLS. Default is 7050. Set to null to disable HTTPS listener.
    /// </summary>
    public int? HttpsPort { get; set; } = 7050;

    /// <summary>
    /// When true, incoming HTTP requests are automatically redirected to HTTPS.
    /// Requires HttpsPort to be configured.
    /// </summary>
    public bool RedirectHttpToHttps { get; set; } = false;

    /// <summary>
    /// TLS certificate options for HTTPS binding (PFX file path or Windows Certificate Store).
    /// </summary>
    public CertificateOptions? Certificate { get; set; }

    /// <summary>
    /// Settings delivered dynamically to the Blazor WASM client (e.g. ApiBaseUrl on Server B).
    /// </summary>
    public ClientSettingsOptions ClientSettings { get; set; } = new();

    /// <summary>
    /// CORS options for controlling which origins are allowed to make cross-origin requests to this host.
    /// </summary>
    public ClientCorsOptions Cors { get; set; } = new();

    /// <summary>
    /// Forwarded headers options for running behind reverse proxies (Nginx, Traefik, ALB, Cloudflare, IIS).
    /// </summary>
    public ForwardedHeadersConfigurationOptions ForwardedHeaders { get; set; } = new();

    /// <summary>
    /// HTTP security response headers (HSTS, CSP, X-Frame-Options, etc.).
    /// </summary>
    public SecurityHeaderOptions SecurityHeaders { get; set; } = new();

    /// <summary>
    /// Response compression options (Brotli + Gzip for WASM, DLL, JS, CSS, JSON).
    /// </summary>
    public CompressionOptions Compression { get; set; } = new();

    /// <summary>
    /// Cache-control options for static and fingerprinted assets.
    /// </summary>
    public StaticFileCachingOptions StaticFiles { get; set; } = new();

    /// <summary>
    /// Health check options for load balancer monitoring.
    /// </summary>
    public ClientHealthCheckOptions HealthCheck { get; set; } = new();
}

/// <summary>
/// Configuration delivered dynamically to the Blazor WASM application when it requests /appsettings.json.
/// </summary>
public class ClientSettingsOptions
{
    /// <summary>
    /// Base URL of the Semantic Agent Broker backend API (Server B), e.g. "http://localhost:5100/" or "https://api.mycompany.com/".
    /// The Blazor WASM client uses this address for all REST API endpoints and SignalR WebSockets.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://localhost:5100/";

    /// <summary>
    /// Optional environment name passed to the client (e.g., "Development", "Staging", "Production").
    /// </summary>
    public string? EnvironmentName { get; set; } = "Production";

    /// <summary>
    /// Optional arbitrary key-value pairs passed directly to the Blazor WASM configuration provider.
    /// </summary>
    public Dictionary<string, string> CustomSettings { get; set; } = new();
}

/// <summary>
/// CORS policy options for the ClientHost.
/// </summary>
public class ClientCorsOptions
{
    /// <summary>
    /// When true, allows all origins. Default is true.
    /// </summary>
    public bool AllowAll { get; set; } = true;

    /// <summary>
    /// Explicit list of allowed origins (e.g., ["http://localhost:5050", "https://client.mycompany.com"]).
    /// Used when AllowAll is false.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = new();

    /// <summary>
    /// Allowed HTTP methods (e.g., ["GET", "POST", "OPTIONS"]). If empty, allows any method.
    /// </summary>
    public List<string> AllowedMethods { get; set; } = new();

    /// <summary>
    /// Allowed HTTP headers. If empty, allows any header.
    /// </summary>
    public List<string> AllowedHeaders { get; set; } = new();

    /// <summary>
    /// Whether credentials (cookies, authorization headers) are allowed in CORS requests.
    /// </summary>
    public bool AllowCredentials { get; set; } = true;
}

/// <summary>
/// Reverse proxy and forwarded header configuration for ClientHost.
/// </summary>
public class ForwardedHeadersConfigurationOptions
{
    /// <summary>
    /// When true, enables ASP.NET Core ForwardedHeaders middleware.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, trusts all upstream proxies. Use only in secure, isolated internal networks or containers.
    /// </summary>
    public bool TrustAllProxies { get; set; } = false;

    /// <summary>
    /// Explicit list of trusted proxy IP addresses (e.g. ["127.0.0.1", "10.0.0.1"]).
    /// </summary>
    public List<string> KnownProxies { get; set; } = new();

    /// <summary>
    /// Explicit list of trusted IP networks in CIDR format (e.g. ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]).
    /// </summary>
    public List<string> KnownNetworks { get; set; } = new();
}

/// <summary>
/// HTTP security response headers applied by ClientHost.
/// </summary>
public class SecurityHeaderOptions
{
    /// <summary>
    /// When true, sends Strict-Transport-Security header on HTTPS responses.
    /// </summary>
    public bool EnableHsts { get; set; } = false;

    /// <summary>
    /// HSTS max-age in days. Default is 365 days.
    /// </summary>
    public int HstsMaxAgeDays { get; set; } = 365;

    /// <summary>
    /// Include subdomains in HSTS policy. Default is true.
    /// </summary>
    public bool HstsIncludeSubdomains { get; set; } = true;

    /// <summary>
    /// Enable HSTS preload flag. Default is false.
    /// </summary>
    public bool HstsPreload { get; set; } = false;

    /// <summary>
    /// X-Content-Type-Options header value. Default is "nosniff".
    /// </summary>
    public string XContentTypeOptions { get; set; } = "nosniff";

    /// <summary>
    /// X-Frame-Options header value (e.g. "SAMEORIGIN", "DENY"). Default is "SAMEORIGIN".
    /// </summary>
    public string XFrameOptions { get; set; } = "SAMEORIGIN";

    /// <summary>
    /// Referrer-Policy header value. Default is "strict-origin-when-cross-origin".
    /// </summary>
    public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

    /// <summary>
    /// Optional Content-Security-Policy header. If null, no CSP header is added.
    /// </summary>
    public string? ContentSecurityPolicy { get; set; } = null;

    /// <summary>
    /// Optional Permissions-Policy header. If null, no Permissions-Policy header is added.
    /// </summary>
    public string? PermissionsPolicy { get; set; } = null;
}

/// <summary>
/// Response compression options for WASM and static assets.
/// </summary>
public class CompressionOptions
{
    /// <summary>
    /// When true, enables Brotli and Gzip response compression. Default is true.
    /// </summary>
    public bool EnableCompression { get; set; } = true;

    /// <summary>
    /// When true, enables compression for HTTPS connections. Default is true.
    /// </summary>
    public bool EnableForHttps { get; set; } = true;
}

/// <summary>
/// Cache-control headers for static files and Blazor WASM assets.
/// </summary>
public class StaticFileCachingOptions
{
    /// <summary>
    /// Max-Age in seconds for fingerprinted framework files (_framework/*, _content/*). Default is 31536000 (1 year / immutable).
    /// </summary>
    public int FrameworkCacheMaxAgeSeconds { get; set; } = 31536000;

    /// <summary>
    /// Max-Age in seconds for HTML documents and configuration files (index.html, appsettings.json).
    /// Default is 0 (no-cache, ensures fresh deployments).
    /// </summary>
    public int HtmlCacheMaxAgeSeconds { get; set; } = 0;
}

/// <summary>
/// Health check endpoint options for ClientHost.
/// </summary>
public class ClientHealthCheckOptions
{
    /// <summary>
    /// Whether the /health endpoint is enabled. Default is true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Endpoint path for the health check. Default is "/health".
    /// </summary>
    public string Path { get; set; } = "/health";
}
