using System.Net;

namespace SemanticAgentBroker.Client.Features.Auth;

public class UnauthorizedResponseHandler : DelegatingHandler
{
    private readonly IServiceProvider _serviceProvider;

    public UnauthorizedResponseHandler(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Network error (server down). Not marked as logged out; rethrow or return a 503.
            throw;
        }
        
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (!path.EndsWith("/auth/validate", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("/auth/login", StringComparison.OrdinalIgnoreCase))
            {
                var authStateProvider = _serviceProvider.GetRequiredService<CustomAuthStateProvider>();
                _ = authStateProvider.MarkUserAsLoggedOut();
            }
        }
        return response;
    }
}
