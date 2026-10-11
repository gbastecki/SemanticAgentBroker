using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using System.Security.Claims;

namespace SemanticAgentBroker.Client.Features.Auth;

public class CustomAuthStateProvider : AuthenticationStateProvider
{
    private readonly HttpClient _httpClient;
    private readonly IJSRuntime _jsRuntime;

    public CustomAuthStateProvider(HttpClient httpClient, IJSRuntime jsRuntime)
    {
        _httpClient = httpClient;
        _jsRuntime = jsRuntime;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            // Batch localStorage reads into a single JS interop call to reduce overhead (CSP compliant)
            string[]? authData = null;
            try
            {
                authData = await _jsRuntime.InvokeAsync<string[]>("window.authStorage.getAuthData");
            }
            catch
            {
                var tokenVal = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", "authToken");
                var roleVal = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", "authRole");
                var usernameVal = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", "authUsername");
                authData = [tokenVal ?? "", roleVal ?? "", usernameVal ?? ""];
            }

            var token = authData?.Length > 0 ? authData[0] : null;
            var role = authData?.Length > 1 ? authData[1] : null;
            var username = authData?.Length > 2 ? authData[2] : null;

            if (string.IsNullOrWhiteSpace(token))
            {
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            }

            var browserId = await GetBrowserIdAsync();

            // Create claims based on stored values
            var claims = new List<Claim>();
            if (!string.IsNullOrEmpty(username)) claims.Add(new Claim(ClaimTypes.Name, username));
            if (!string.IsNullOrEmpty(role)) claims.Add(new Claim(ClaimTypes.Role, role));

            var identity = new ClaimsIdentity(claims, "jwt");
            var user = new ClaimsPrincipal(identity);

            return new AuthenticationState(user);
        }
        catch
        {
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }
    }

    public async Task MarkUserAsAuthenticated(string token, string role, string username)
    {
        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "authToken", token);
        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "authRole", role);
        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "authUsername", username);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role)
        };
        var identity = new ClaimsIdentity(claims, "jwt");
        var user = new ClaimsPrincipal(identity);

        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(user)));
    }

    private int _isLoggingOut;

    public async Task MarkUserAsLoggedOut()
    {
        // Guard against multiple concurrent 401 responses all triggering logout
        if (Interlocked.CompareExchange(ref _isLoggingOut, 1, 0) != 0) return;

        try
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("window.authStorage.clearAuthData");
            }
            catch
            {
                await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "authToken");
                await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "authRole");
                await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "authUsername");
            }

            var user = new ClaimsPrincipal(new ClaimsIdentity());
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(user)));
        }
        finally
        {
            Interlocked.Exchange(ref _isLoggingOut, 0);
        }
    }
    
    private string? _cachedBrowserId;

    public async Task<string> GetBrowserIdAsync()
    {
        if (_cachedBrowserId != null) return _cachedBrowserId;

        var bid = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "browserId");
        if (string.IsNullOrEmpty(bid))
        {
            bid = Guid.NewGuid().ToString();
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "browserId", bid);
        }
        _cachedBrowserId = bid;
        return bid;
    }
}
