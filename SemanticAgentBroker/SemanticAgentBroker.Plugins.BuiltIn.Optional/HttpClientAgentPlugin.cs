using System.Text;
using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.Plugins.BuiltIn.Optional;

/// <summary>
/// Built-in plugin providing HTTP request capabilities to agents.
/// Enables agents to fetch data from APIs, download content, etc.
/// NOTE: This plugin is provided for demonstration/example purposes only.
/// In production environments, network egress should be restricted via firewalls,
/// domain allowlists, or private network boundaries to mitigate SSRF risks.
/// </summary>
public class HttpClientAgentPlugin : PluginBase
{
    public override string Id => "HttpClient";
    public override string DisplayName => "HTTP Client";
    public override string Description => "Make HTTP requests (GET, POST) to external APIs and services. Network access required.";
    public override string Version => "1.0.0";
    public override string Author => "SemanticAgentBroker";
    public override string Icon => "cloud";
    public override IReadOnlyList<string> Tags => ["http", "api", "networking"];
    public override PluginCategory Category => PluginCategory.BuiltInOptional;

    public override IReadOnlyList<IPluginMethod> GetMethods() =>
    [
        new HttpGetMethod(),
        new HttpPostMethod()
    ];

    public override IReadOnlyList<PluginConfigEntry> GetConfigurationSchema() =>
    [
        new() { Key = "TimeoutSeconds", DisplayName = "Timeout (s)", Description = "HTTP request timeout.", DefaultValue = "30", IsRequired = false },
        new() { Key = "MaxResponseChars", DisplayName = "Max Response Characters", Description = "Maximum response body characters to return.", DefaultValue = "8000", IsRequired = false }
    ];
}

public class HttpGetMethod : PluginMethodBase
{
    public override string Id => "HttpGet";
    public override string DisplayName => "HTTP GET";
    public override string Description => "Sends an HTTP GET request to the specified URL and returns the response body.";
    public override string ReturnDescription => "HTTP response body as text.";
    public override bool IsDangerous => true; // Marked dangerous to require confirmation / approval

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "url", Description = "The full URL to send the GET request to.", ParameterType = typeof(string), IsRequired = true },
        new() { Name = "headers", Description = "Optional JSON object of HTTP headers (e.g. {\"Authorization\": \"Bearer xxx\"}).", ParameterType = typeof(string), IsRequired = false }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var url = GetRequiredString(arguments, "url");
        if (!ValidateUrlSecurity(url, context, out var securityError))
            return securityError;

        var headersJson = GetOptionalString(arguments, "headers");
        var timeout = int.TryParse(context.Configuration.GetValueOrDefault("TimeoutSeconds", "30"), out var t) ? t : 30;
        var maxChars = int.TryParse(context.Configuration.GetValueOrDefault("MaxResponseChars", "8000"), out var m) ? m : 8000;

        var factory = context.GetService<System.Net.Http.IHttpClientFactory>();
        if (factory == null)
            return "HTTP GET failed: IHttpClientFactory is not available in plugin context.";

        var client = factory.CreateClient("HttpClientAgentPlugin") ?? factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeout);
        ApplyHeaders(client, headersJson);

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            var result = $"HTTP {(int)response.StatusCode} {response.StatusCode}\n";
            if (response.Headers.Location != null)
            {
                result += $"Location: {response.Headers.Location}\n";
            }
            result += "\n";

            var (body, isTruncated) = await ReadBoundedStringAsync(response.Content, maxChars, ct);

            if (isTruncated)
            {
                var lengthText = response.Content.Headers.ContentLength.HasValue
                    ? $"{response.Content.Headers.ContentLength.Value} chars"
                    : $"exceeded {maxChars} chars";
                result += body + $"\n\n... [TRUNCATED - response was {lengthText}]";
            }
            else
            {
                result += body;
            }

            return result;
        }
        catch (Exception ex)
        {
            return $"HTTP GET failed: {ex.Message}";
        }
    }

    internal static bool ValidateUrlSecurity(string url, IPluginContext context, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            errorMessage = $"HTTP request blocked: Invalid URL '{url}'.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            errorMessage = $"HTTP request blocked: Unsupported URL scheme '{uri.Scheme}'. Only HTTP and HTTPS are allowed.";
            return false;
        }

        var sysOptions = context.GetService<SemanticAgentBroker.Shared.Configuration.AgentSystemOptions>();
        var security = sysOptions?.HttpClientSecurity ?? new SemanticAgentBroker.Shared.Configuration.HttpClientSecurityOptions();

        var host = uri.DnsSafeHost;

        // 1. Check Allowlist (if enabled)
        if (security.EnableAllowlist)
        {
            bool isAllowed = false;
            foreach (var pattern in security.AllowedPatterns)
            {
                if (IsPatternMatch(pattern, url, host))
                {
                    isAllowed = true;
                    break;
                }
            }

            if (!isAllowed)
            {
                errorMessage = $"HTTP request blocked: URL '{url}' is not in the allowed URLs list.";
                return false;
            }
        }

        // 2. Check Blocklist (if enabled)
        if (security.EnableBlocklist)
        {
            // Check direct hostname matches
            foreach (var pattern in security.BlockedPatterns)
            {
                if (IsPatternMatch(pattern, url, host))
                {
                    errorMessage = $"HTTP request blocked by SSRF security policy: URL '{url}' matches blocked pattern '{pattern}'.";
                    return false;
                }
            }

            // Resolve host IP addresses to detect internal/private/loopback ranges
            if (System.Net.IPAddress.TryParse(host, out var parsedIp))
            {
                if (IsPrivateOrLoopbackIp(parsedIp))
                {
                    errorMessage = $"HTTP request blocked by SSRF security policy: Target host '{host}' is a private/loopback IP address.";
                    return false;
                }
            }
            else
            {
                try
                {
                    var addresses = System.Net.Dns.GetHostAddresses(host);
                    foreach (var ip in addresses)
                    {
                        if (IsPrivateOrLoopbackIp(ip))
                        {
                            errorMessage = $"HTTP request blocked by SSRF security policy: Target host '{host}' resolves to private/loopback IP address '{ip}'.";
                            return false;
                        }

                        var ipStr = ip.ToString();
                        foreach (var pattern in security.BlockedPatterns)
                        {
                            if (IsPatternMatch(pattern, ipStr, ipStr))
                            {
                                errorMessage = $"HTTP request blocked by SSRF security policy: Resolved IP '{ipStr}' matches blocked pattern '{pattern}'.";
                                return false;
                            }
                        }
                    }
                }
                catch (System.Net.Sockets.SocketException)
                {
                    // If DNS resolution fails here (e.g. test mock domain or disconnected network),
                    // allow HttpClient to attempt connection and report network failure appropriately.
                }
                catch (Exception ex)
                {
                    errorMessage = $"HTTP request blocked: Error checking host '{host}': {ex.Message}";
                    return false;
                }
            }
        }

        return true;
    }

    internal static async Task<(string Content, bool IsTruncated)> ReadBoundedStringAsync(
        HttpContent content, int maxChars, CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var buffer = new char[maxChars];
        int totalRead = 0;
        while (totalRead < maxChars)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(totalRead, maxChars - totalRead), ct);
            if (read == 0) break;
            totalRead += read;
        }

        var peek = new char[1];
        int extra = await reader.ReadAsync(peek.AsMemory(0, 1), ct);
        bool isTruncated = extra > 0;

        return (new string(buffer, 0, totalRead), isTruncated);
    }

    internal static bool IsPrivateOrLoopbackIp(System.Net.IPAddress ip)
    {
        return SemanticAgentBroker.Shared.Utilities.NetworkSecurityUtils.IsPrivateOrLoopbackIp(ip);
    }

    private static bool IsPatternMatch(string pattern, string url, string host)
    {
        pattern = pattern.Trim();
        if (string.Equals(pattern, "*", StringComparison.OrdinalIgnoreCase)) return true;

        // 1. Full URL pattern (starts with http:// or https://)
        if (pattern.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            pattern.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (pattern.Contains('*'))
            {
                var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                return System.Text.RegularExpressions.Regex.IsMatch(url, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return url.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
        }

        // 2. Pattern contains path without scheme (e.g. "api.github.com/v1/*")
        if (pattern.Contains('/'))
        {
            return IsPatternMatch($"http://{pattern}", url, host) || IsPatternMatch($"https://{pattern}", url, host);
        }

        // 3. Host / Domain / IP pattern - strictly matched against host (never full URL)
        if (pattern.Contains('*'))
        {
            if (pattern.StartsWith("*.", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(host, pattern[2..], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(host, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        if (pattern.StartsWith('.'))
        {
            return string.Equals(host, pattern.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ApplyHeaders(HttpClient client, string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson) || headersJson == "{}") return;

        try
        {
            var headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
            if (headers == null) return;
            foreach (var kvp in headers)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(kvp.Key, kvp.Value);
            }
        }
        catch { /* Ignore invalid header JSON */ }
    }
}

public class HttpPostMethod : PluginMethodBase
{
    public override string Id => "HttpPost";
    public override string DisplayName => "HTTP POST";
    public override string Description => "Sends an HTTP POST request with a body to the specified URL and returns the response.";
    public override string ReturnDescription => "HTTP response body as text.";
    public override bool IsDangerous => true;

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "url", Description = "The full URL to send the POST request to.", ParameterType = typeof(string), IsRequired = true },
        new() { Name = "body", Description = "The request body content.", ParameterType = typeof(string), IsRequired = true },
        new() { Name = "contentType", Description = "Content-Type header (default: application/json).", ParameterType = typeof(string), IsRequired = false, DefaultValue = "application/json" },
        new() { Name = "headers", Description = "Optional JSON object of HTTP headers.", ParameterType = typeof(string), IsRequired = false }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var url = GetRequiredString(arguments, "url");
        if (!HttpGetMethod.ValidateUrlSecurity(url, context, out var securityError))
            return securityError;

        var body = GetRequiredString(arguments, "body");
        var contentType = GetOptionalString(arguments, "contentType", "application/json");
        var headersJson = GetOptionalString(arguments, "headers");
        var timeout = int.TryParse(context.Configuration.GetValueOrDefault("TimeoutSeconds", "30"), out var t) ? t : 30;
        var maxChars = int.TryParse(context.Configuration.GetValueOrDefault("MaxResponseChars", "8000"), out var m) ? m : 8000;

        var factory = context.GetService<System.Net.Http.IHttpClientFactory>();
        if (factory == null)
            return "HTTP POST failed: IHttpClientFactory is not available in plugin context.";

        var client = factory.CreateClient("HttpClientAgentPlugin") ?? factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeout);
        HttpGetMethod.ApplyHeaders(client, headersJson);

        try
        {
            var content = new StringContent(body, Encoding.UTF8, contentType);
            using var response = await client.PostAsync(url, content, ct);

            var result = $"HTTP {(int)response.StatusCode} {response.StatusCode}\n";
            if (response.Headers.Location != null)
            {
                result += $"Location: {response.Headers.Location}\n";
            }
            result += "\n";

            var (responseBody, isTruncated) = await HttpGetMethod.ReadBoundedStringAsync(response.Content, maxChars, ct);

            if (isTruncated)
            {
                var lengthText = response.Content.Headers.ContentLength.HasValue
                    ? $"{response.Content.Headers.ContentLength.Value} chars"
                    : $"exceeded {maxChars} chars";
                result += responseBody + $"\n\n... [TRUNCATED - response was {lengthText}]";
            }
            else
            {
                result += responseBody;
            }

            return result;
        }
        catch (Exception ex)
        {
            return $"HTTP POST failed: {ex.Message}";
        }
    }
}
