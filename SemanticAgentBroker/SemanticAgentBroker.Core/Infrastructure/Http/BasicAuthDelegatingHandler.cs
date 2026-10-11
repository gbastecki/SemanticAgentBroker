using System.Net.Http.Headers;
using System.Text;

namespace SemanticAgentBroker.Core.Infrastructure.Http;

/// <summary>
/// A delegating handler that forces HTTP Basic Authentication if configured.
/// This prevents downstream connectors (like Semantic Kernel's OpenAI connector)
/// from overriding the authorization header with a Bearer token when Basic Auth is required.
/// </summary>
public class BasicAuthDelegatingHandler : DelegatingHandler
{
    private readonly string _username;
    private readonly string _password;

    /// <summary>
    /// Initializes a new instance of <see cref="BasicAuthDelegatingHandler"/>.
    /// </summary>
    /// <param name="username">Basic Auth username.</param>
    /// <param name="password">Basic Auth password.</param>
    public BasicAuthDelegatingHandler(string username, string password)
    {
        _username = username ?? string.Empty;
        _password = password ?? string.Empty;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_username))
        {
            var authString = $"{_username}:{_password}";
            var base64String = Convert.ToBase64String(Encoding.UTF8.GetBytes(authString));
            
            // Forcibly overwrite the Authorization header right before the request goes out.
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64String);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
