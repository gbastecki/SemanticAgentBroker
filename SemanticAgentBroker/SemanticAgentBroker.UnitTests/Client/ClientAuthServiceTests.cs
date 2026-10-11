using Microsoft.JSInterop;
using Moq;
using SemanticAgentBroker.Client.Features.Auth;
using System.Net;
using System.Net.Http.Json;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class ClientAuthServiceTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }

    private Mock<IJSRuntime> _jsRuntimeMock = null!;
    private MockHttpMessageHandler _httpHandler = null!;
    private HttpClient _httpClient = null!;
    private CustomAuthStateProvider _authStateProvider = null!;

    [TestInitialize]
    public void Setup()
    {
        _jsRuntimeMock = new Mock<IJSRuntime>();
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string>("localStorage.getItem", It.Is<object?[]>(args => (string?)args[0] == "browserId")))
            .ReturnsAsync("browser-test-123");

        _httpHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("https://localhost:5001/") };
        _authStateProvider = new CustomAuthStateProvider(_httpClient, _jsRuntimeMock.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _httpClient.Dispose();
    }

    [TestMethod]
    public async Task LoginAsync_ValidCredentials_AuthenticatesAndReturnsNullError()
    {
        // Arrange
        _httpHandler.Handler = req =>
        {
            Assert.AreEqual("/api/auth/login", req.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new LoginResponse { Token = "valid-token-777", Role = "Admin" })
            };
        };

        var authService = new ClientAuthService(_httpClient, _authStateProvider);

        // Act
        var error = await authService.LoginAsync("admin", "adminpassword");

        // Assert
        Assert.IsNull(error);
        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
        Assert.IsFalse(_httpClient.DefaultRequestHeaders.Contains("X-Browser-Id"));
    }

    [TestMethod]
    public async Task LoginAsync_InvalidCredentials401_ReturnsErrorMessage()
    {
        // Arrange
        _httpHandler.Handler = req =>
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = JsonContent.Create(new ErrorResponse { Error = "Invalid username or password" })
            };
        };

        var authService = new ClientAuthService(_httpClient, _authStateProvider);

        // Act
        var error = await authService.LoginAsync("admin", "wrongpassword");

        // Assert
        Assert.AreEqual("Invalid username or password", error);
        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
    }

    [TestMethod]
    public async Task LoginAsync_NetworkFailure_ReturnsConnectionErrorMessage()
    {
        // Arrange
        _httpHandler.Handler = _ => throw new HttpRequestException("Server unreachable");

        var authService = new ClientAuthService(_httpClient, _authStateProvider);

        // Act
        var error = await authService.LoginAsync("admin", "anypassword");

        // Assert
        Assert.AreEqual("Could not connect to the server. Please check if the server is running.", error);
    }

    [TestMethod]
    public async Task LoginAsync_NonSuccessStatusCode_ReturnsLoginFailed()
    {
        // Arrange
        _httpHandler.Handler = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var authService = new ClientAuthService(_httpClient, _authStateProvider);

        // Act
        var error = await authService.LoginAsync("admin", "password");

        // Assert
        Assert.AreEqual("Login failed.", error);
    }

    [TestMethod]
    public async Task LogoutAsync_SendsLogoutRequestAndClearsAuthState()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        _httpHandler.Handler = req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var authService = new ClientAuthService(_httpClient, _authStateProvider);

        // Act
        await authService.LogoutAsync();

        // Assert
        Assert.IsNotNull(capturedRequest);
        Assert.AreEqual("/api/auth/logout", capturedRequest.RequestUri?.AbsolutePath);
        Assert.IsTrue(capturedRequest.Headers.Contains("X-Browser-Id"));
        Assert.AreEqual("browser-test-123", capturedRequest.Headers.GetValues("X-Browser-Id").First());

        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
        Assert.IsFalse(_httpClient.DefaultRequestHeaders.Contains("X-Browser-Id"));
    }

    [TestMethod]
    public async Task LogoutAsync_WhenNetworkThrows_StillClearsLocalAuthState()
    {
        // Arrange
        _httpHandler.Handler = _ => throw new HttpRequestException("Server disconnected during logout");

        var authService = new ClientAuthService(_httpClient, _authStateProvider);

        // Act & Assert (should not throw)
        await authService.LogoutAsync();

        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
        Assert.IsFalse(_httpClient.DefaultRequestHeaders.Contains("X-Browser-Id"));
    }

    [TestMethod]
    public async Task LoginAsync_EmptyTokenInSuccessResponse_ReturnsLoginFailed()
    {
        // Arrange - Server returns 200 OK but with empty token string
        _httpHandler.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new LoginResponse { Token = "", Role = "User" })
        };

        var authService = new ClientAuthService(_httpClient, _authStateProvider);

        // Act
        var error = await authService.LoginAsync("user", "password");

        // Assert
        Assert.AreEqual("Login failed.", error);
        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.BadRequest)]
    [DataRow(HttpStatusCode.BadGateway)]
    public async Task LoginAsync_OnNonUnauthorizedError_ReturnsDefaultLoginFailedMessage(HttpStatusCode statusCode)
    {
        // Arrange
        _httpHandler.Handler = _ => new HttpResponseMessage(statusCode);
        var authService = new ClientAuthService(_httpClient, _authStateProvider);

        // Act
        var error = await authService.LoginAsync("user", "password");

        // Assert
        Assert.AreEqual("Login failed.", error);
        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
    }
}
