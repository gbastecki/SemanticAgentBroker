using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SemanticAgentBroker.UnitTests.Api.Auth;

[TestClass]
public class AuthEndpointsTests
{
    private CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task Login_WithValidAdminCredentials_ReturnsTokenAndRole()
    {
        // Arrange
        var request = new LoginRequest("admin", "admin", "test-browser-1");

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/login", request);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(json.TryGetProperty("token", out var tokenProp));
        Assert.IsFalse(string.IsNullOrEmpty(tokenProp.GetString()));
        Assert.AreEqual("Admin", json.GetProperty("role").GetString());
    }

    [TestMethod]
    public async Task Login_WithInvalidPassword_ReturnsUnauthorized()
    {
        // Arrange
        var request = new LoginRequest("admin", "incorrect_password", "test-browser-2");

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/login", request);

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Validate_WithValidToken_ReturnsSessionInfo()
    {
        // Arrange - Log in first
        var loginReq = new LoginRequest("admin", "admin", "test-browser-validate");
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginReq);
        Assert.AreEqual(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = loginJson.GetProperty("token").GetString();

        var validateClient = _factory.CreateClient();
        validateClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        validateClient.DefaultRequestHeaders.Add("X-Browser-Id", "test-browser-validate");

        // Act
        var validateResponse = await validateClient.GetAsync("/api/auth/validate");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, validateResponse.StatusCode);
        var validateJson = await validateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("admin", validateJson.GetProperty("username").GetString());
        Assert.AreEqual("Admin", validateJson.GetProperty("role").GetString());
    }

    [TestMethod]
    public async Task Validate_WithoutBearerToken_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/auth/validate");

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Logout_WithValidToken_InvalidatesSession()
    {
        // Arrange - Log in first
        var loginReq = new LoginRequest("admin", "admin", "test-browser-logout");
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginReq);
        Assert.AreEqual(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = loginJson.GetProperty("token").GetString();

        var authClient = _factory.CreateClient();
        authClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        authClient.DefaultRequestHeaders.Add("X-Browser-Id", "test-browser-logout");

        // Act - Logout
        var logoutResponse = await authClient.PostAsJsonAsync("/api/auth/logout", new LogoutRequest("test-browser-logout"));
        Assert.AreEqual(HttpStatusCode.OK, logoutResponse.StatusCode);

        // Assert - Subsequent validate should return 401
        var validateResponse = await authClient.GetAsync("/api/auth/validate");
        Assert.AreEqual(HttpStatusCode.Unauthorized, validateResponse.StatusCode);
    }

    [TestMethod]
    public async Task Logout_WithoutAuthorizationHeader_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest("browser-1"));

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Logout_WithInvalidToken_ReturnsUnauthorized()
    {
        // Arrange
        var requestClient = _factory.CreateClient();
        requestClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "non-existent-token");
        requestClient.DefaultRequestHeaders.Add("X-Browser-Id", "browser-invalid");

        // Act
        var response = await requestClient.PostAsJsonAsync("/api/auth/logout", new LogoutRequest("browser-invalid"));

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Validate_WithInvalidOrExpiredToken_ReturnsUnauthorized()
    {
        // Arrange
        var requestClient = _factory.CreateClient();
        requestClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "expired-or-invalid-token");
        requestClient.DefaultRequestHeaders.Add("X-Browser-Id", "browser-val");

        // Act
        var response = await requestClient.GetAsync("/api/auth/validate");

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Login_WhenAccountLocked_ReturnsSafeErrorMessage()
    {
        // Arrange - Trigger multiple failed logins to lock out the IP / username
        for (int i = 0; i < 5; i++)
        {
            await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "wrongpassword", "browser-lockout"));
        }

        // Act - Attempt login when locked
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "wrongpassword", "browser-lockout"));

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(json.TryGetProperty("error", out var errorProp));
        Assert.AreEqual("Account temporarily locked. Try again later.", errorProp.GetString());
    }

    [TestMethod]
    public async Task ProtectedEndpoint_WithLowerCaseBearerScheme_ReturnsOk()
    {
        // Arrange - Log in first
        var loginReq = new LoginRequest("admin", "admin", "browser-case-test");
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginReq);
        Assert.AreEqual(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = loginJson.GetProperty("token").GetString();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/agents");
        request.Headers.Add("Authorization", $"bearer {token}");
        request.Headers.Add("X-Browser-Id", "browser-case-test");

        // Act
        var response = await _client.SendAsync(request);

        // Assert - Case-insensitive bearer token scheme per RFC 7235 / RFC 6750
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
