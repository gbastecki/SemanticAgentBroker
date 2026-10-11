using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Samples.Common.Authentication;

/// <summary>
/// HTTP delegating handler that injects Bearer authentication tokens and X-Browser-Id headers.
/// Automatically handles 401 Unauthorized responses by forcing a token refresh and retrying the request.
/// </summary>
public class AuthHandler : DelegatingHandler
{
    private readonly TokenService _tokenService;
    private readonly ILogger<AuthHandler> _logger;

    public AuthHandler(TokenService tokenService, ILogger<AuthHandler>? logger = null)
    {
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _logger = logger ?? NullLogger<AuthHandler>.Instance;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 1. Attach browser identification header
        if (!request.Headers.Contains("X-Browser-Id"))
        {
            request.Headers.TryAddWithoutValidation("X-Browser-Id", _tokenService.BrowserId);
        }

        // 2. Attach Bearer token
        var token = await _tokenService.GetTokenAsync(cancellationToken);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // 3. Send request
        var response = await base.SendAsync(request, cancellationToken);

        // 4. If 401 Unauthorized occurs, force token refresh and retry once
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Received 401 Unauthorized from {Uri}. Refreshing authentication token...", request.RequestUri);

            var freshToken = await _tokenService.GetTokenAsync(cancellationToken, failedToken: token);
            if (!string.IsNullOrEmpty(freshToken))
            {
                using var clonedRequest = await CloneRequestAsync(request);
                clonedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", freshToken);

                response.Dispose();
                response = await base.SendAsync(clonedRequest, cancellationToken);
            }
        }

        return response;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version
        };

        if (request.Content != null)
        {
            var contentBytes = await request.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(contentBytes);
            if (request.Content.Headers != null)
            {
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        return clone;
    }
}
