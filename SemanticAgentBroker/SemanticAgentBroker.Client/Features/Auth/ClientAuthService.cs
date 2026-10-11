using System.Net.Http.Json;

namespace SemanticAgentBroker.Client.Features.Auth;

public class ClientAuthService
{
    private readonly HttpClient _httpClient;
    private readonly CustomAuthStateProvider _authStateProvider;

    public ClientAuthService(HttpClient httpClient, CustomAuthStateProvider authStateProvider)
    {
        _httpClient = httpClient;
        _authStateProvider = authStateProvider;
    }

    public async Task<string?> LoginAsync(string username, string password)
    {
        var browserId = await _authStateProvider.GetBrowserIdAsync();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password, BrowserId = browserId });
        }
        catch (HttpRequestException)
        {
            return "Could not connect to the server. Please check if the server is running.";
        }

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
            if (result != null && !string.IsNullOrEmpty(result.Token))
            {
                // Authenticate with token and assigned role from login response.
                await _authStateProvider.MarkUserAsAuthenticated(result.Token, result.Role ?? "User", username);
                return null;
            }
        }
        else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
            return error?.Error ?? "Invalid credentials or lockout.";
        }
        
        return "Login failed.";
    }

    public async Task LogoutAsync()
    {
        var browserId = await _authStateProvider.GetBrowserIdAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            if (!string.IsNullOrEmpty(browserId))
            {
                request.Headers.Add("X-Browser-Id", browserId);
            }
            await _httpClient.SendAsync(request);
        }
        catch (Exception)
        {
            // Ignore network errors when logging out to ensure local auth state is forcibly cleared.
        }
        
        await _authStateProvider.MarkUserAsLoggedOut();
    }
}

public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
}
