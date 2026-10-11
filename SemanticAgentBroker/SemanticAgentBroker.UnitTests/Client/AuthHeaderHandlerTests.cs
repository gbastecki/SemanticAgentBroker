using Microsoft.JSInterop;
using Moq;
using SemanticAgentBroker.Client.Features.Auth;
using System.Net;
using System.Net.Http.Headers;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class AuthHeaderHandlerTests
{
    private class TestInnerHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private Mock<IJSRuntime> _jsRuntimeMock = null!;
    private TestInnerHandler _innerHandler = null!;
    private HttpMessageInvoker _invoker = null!;
    private AuthHeaderHandler _authHeaderHandler = null!;

    [TestInitialize]
    public void Setup()
    {
        _jsRuntimeMock = new Mock<IJSRuntime>();
        _innerHandler = new TestInnerHandler();
        _authHeaderHandler = new AuthHeaderHandler(_jsRuntimeMock.Object)
        {
            InnerHandler = _innerHandler
        };
        _invoker = new HttpMessageInvoker(_authHeaderHandler);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _invoker.Dispose();
    }

    [TestMethod]
    public async Task SendAsync_WhenTokenAndBrowserIdExistInAuthStorage_AttachesHeadersToRequest()
    {
        // Arrange
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string[]>("window.authStorage.getAuthData", It.IsAny<object?[]>()))
            .ReturnsAsync(new[] { "mock-token-xyz", "Admin", "user1" });

        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string?>("localStorage.getItem", It.Is<object?[]>(args => args.Length > 0 && (string?)args[0] == "browserId")))
            .ReturnsAsync("mock-browser-123");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:5001/api/tasks");

        // Act
        var response = await _invoker.SendAsync(request, CancellationToken.None);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(_innerHandler.LastRequest);
        Assert.AreEqual("Bearer", _innerHandler.LastRequest.Headers.Authorization?.Scheme);
        Assert.AreEqual("mock-token-xyz", _innerHandler.LastRequest.Headers.Authorization?.Parameter);
        Assert.IsTrue(_innerHandler.LastRequest.Headers.Contains("X-Browser-Id"));
        Assert.AreEqual("mock-browser-123", _innerHandler.LastRequest.Headers.GetValues("X-Browser-Id").First());
    }

    [TestMethod]
    public async Task SendAsync_WhenAuthStorageFails_FallsBackToLocalStorageAuthToken()
    {
        // Arrange
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string[]>("window.authStorage.getAuthData", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("authStorage missing"));

        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string?>("localStorage.getItem", It.Is<object?[]>(args => args.Length > 0 && (string?)args[0] == "authToken")))
            .ReturnsAsync("fallback-token-456");

        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string?>("localStorage.getItem", It.Is<object?[]>(args => args.Length > 0 && (string?)args[0] == "browserId")))
            .ReturnsAsync("browser-fallback");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:5001/api/tasks");

        // Act
        var response = await _invoker.SendAsync(request, CancellationToken.None);

        // Assert
        Assert.IsNotNull(_innerHandler.LastRequest);
        Assert.AreEqual("Bearer", _innerHandler.LastRequest.Headers.Authorization?.Scheme);
        Assert.AreEqual("fallback-token-456", _innerHandler.LastRequest.Headers.Authorization?.Parameter);
        Assert.AreEqual("browser-fallback", _innerHandler.LastRequest.Headers.GetValues("X-Browser-Id").First());
    }

    [TestMethod]
    public async Task SendAsync_WhenRequestAlreadyHasAuthorization_DoesNotOverwriteExistingHeader()
    {
        // Arrange
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string[]>("window.authStorage.getAuthData", It.IsAny<object?[]>()))
            .ReturnsAsync(new[] { "stored-token", "User", "user2" });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:5001/api/tasks");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "existing-custom-token");

        // Act
        await _invoker.SendAsync(request, CancellationToken.None);

        // Assert
        Assert.IsNotNull(_innerHandler.LastRequest);
        Assert.AreEqual("existing-custom-token", _innerHandler.LastRequest.Headers.Authorization?.Parameter);
    }

    [TestMethod]
    public async Task SendAsync_WhenJsRuntimeThrows_ProceedsWithoutThrowingException()
    {
        // Arrange
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string[]>("window.authStorage.getAuthData", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("DOMException"));
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string?>("localStorage.getItem", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("DOMException"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:5001/api/tasks");

        // Act
        var response = await _invoker.SendAsync(request, CancellationToken.None);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(_innerHandler.LastRequest);
        Assert.IsNull(_innerHandler.LastRequest.Headers.Authorization);
        Assert.IsFalse(_innerHandler.LastRequest.Headers.Contains("X-Browser-Id"));
    }

    [TestMethod]
    public async Task SendAsync_WhenNoTokenExists_DoesNotAttachAuthorizationHeader()
    {
        // Arrange
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string[]>("window.authStorage.getAuthData", It.IsAny<object?[]>()))
            .ReturnsAsync(new[] { "", "", "" });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:5001/api/tasks");

        // Act
        var response = await _invoker.SendAsync(request, CancellationToken.None);

        // Assert
        Assert.IsNotNull(_innerHandler.LastRequest);
        Assert.IsNull(_innerHandler.LastRequest.Headers.Authorization);
    }
}
