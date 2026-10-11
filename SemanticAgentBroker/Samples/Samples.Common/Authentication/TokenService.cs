using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Samples.Common.Models;

namespace Samples.Common.Authentication;

/// <summary>
/// Thread-safe service for acquiring and caching session authentication tokens from Semantic Agent Broker.
/// </summary>
public sealed class TokenService : IDisposable
{
    private readonly HttpClient _authClient;
    private readonly string _username;
    private readonly string _password;
    private readonly string _browserId;
    private readonly ILogger<TokenService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private string? _cachedToken;
    private bool _disposed;

    public string BrowserId => _browserId;

    public TokenService(
        HttpClient authClient,
        string username = "admin",
        string password = "admin",
        string browserId = "sample-integration-client",
        ILogger<TokenService>? logger = null)
    {
        _authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
        _username = username;
        _password = password;
        _browserId = browserId;
        _logger = logger ?? NullLogger<TokenService>.Instance;
    }

    /// <summary>
    /// Gets a valid Bearer token. If the cached token matches failedToken, forces acquisition of a fresh token.
    /// </summary>
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default, string? failedToken = null)
    {
        // Fast path: cached token is present and was not reported as failed
        if (failedToken == null && !string.IsNullOrEmpty(_cachedToken))
        {
            return _cachedToken;
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            // Double-check: another thread might have refreshed the token while waiting
            if (!string.IsNullOrEmpty(_cachedToken) && (failedToken == null || _cachedToken != failedToken))
            {
                return _cachedToken;
            }

            _logger.LogInformation("Authenticating with Semantic Agent Broker as '{Username}'...", _username);

            var loginRequest = new
            {
                Username = _username,
                Password = _password,
                BrowserId = _browserId
            };

            using var response = await _authClient.PostAsJsonAsync("/api/auth/login", loginRequest, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<LoginResultDto>(cancellationToken: cancellationToken);
                _cachedToken = result?.Token;
                _logger.LogInformation("Authentication successful. Role: {Role}", result?.Role);
                return _cachedToken;
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Authentication failed with HTTP {StatusCode}: {Error}", response.StatusCode, errorBody);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to broker auth endpoint at {BaseAddress}.", _authClient.BaseAddress);
            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Invalidate the current cached token so the next call forces re-authentication.
    /// </summary>
    public void InvalidateToken()
    {
        _cachedToken = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _semaphore.Dispose();
    }
}
