using System.Net.Http.Headers;
using Microsoft.JSInterop;

namespace SemanticAgentBroker.Client.Features.Auth;

public class AuthHeaderHandler : DelegatingHandler
{
    private readonly IJSRuntime _jsRuntime;

    public AuthHeaderHandler(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization == null)
        {
            string? token = null;
            try
            {
                var authData = await _jsRuntime.InvokeAsync<string[]>("window.authStorage.getAuthData");
                if (authData != null && authData.Length > 0 && !string.IsNullOrWhiteSpace(authData[0]))
                {
                    token = authData[0];
                }
            }
            catch
            {
                try
                {
                    token = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", "authToken");
                }
                catch
                {
                    // JS interop unavailable
                }
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        if (!request.Headers.Contains("X-Browser-Id"))
        {
            try
            {
                var browserId = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", "browserId");
                if (!string.IsNullOrWhiteSpace(browserId))
                {
                    request.Headers.Add("X-Browser-Id", browserId);
                }
            }
            catch
            {
                // JS interop unavailable
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
