using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using SemanticAgentBroker.Client.Features.Auth;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class CustomAuthStateProviderTests
{
    private Mock<IJSRuntime> _jsRuntimeMock = null!;
    private HttpClient _httpClient = null!;

    [TestInitialize]
    public void Setup()
    {
        _jsRuntimeMock = new Mock<IJSRuntime>();
        _httpClient = new HttpClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _httpClient.Dispose();
    }

    [TestMethod]
    public async Task GetAuthenticationStateAsync_WhenLocalStorageHasValidToken_ReturnsAuthenticatedPrincipal()
    {
        // Arrange
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string[]>("window.authStorage.getAuthData", It.IsAny<object?[]>()))
            .ReturnsAsync(new[] { "test-jwt-token-123", "Admin", "alice" });

        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string>("localStorage.getItem", It.Is<object?[]>(args => args.Length > 0 && (string?)args[0] == "browserId")))
            .ReturnsAsync("browser-id-xyz");

        var provider = new CustomAuthStateProvider(_httpClient, _jsRuntimeMock.Object);

        // Act
        var state = await provider.GetAuthenticationStateAsync();

        // Assert
        Assert.IsNotNull(state);
        Assert.IsNotNull(state.User.Identity);
        Assert.IsTrue(state.User.Identity.IsAuthenticated);
        Assert.AreEqual("alice", state.User.Identity.Name);
        Assert.IsTrue(state.User.IsInRole("Admin"));
        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
        Assert.IsFalse(_httpClient.DefaultRequestHeaders.Contains("X-Browser-Id"));
    }

    [TestMethod]
    public async Task GetAuthenticationStateAsync_WhenStorageIsEmpty_ReturnsUnauthenticated()
    {
        // Arrange
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string[]>("window.authStorage.getAuthData", It.IsAny<object?[]>()))
            .ReturnsAsync(new[] { "", "", "" });

        var provider = new CustomAuthStateProvider(_httpClient, _jsRuntimeMock.Object);

        // Act
        var state = await provider.GetAuthenticationStateAsync();

        // Assert
        Assert.IsNotNull(state);
        Assert.IsNotNull(state.User.Identity);
        Assert.IsFalse(state.User.Identity.IsAuthenticated);
        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
    }

    [TestMethod]
    public async Task GetAuthenticationStateAsync_WhenJsExceptionThrown_ReturnsUnauthenticatedGracefully()
    {
        // Arrange
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string[]>("window.authStorage.getAuthData", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("localStorage inaccessible"));

        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string?>("localStorage.getItem", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("localStorage disabled"));

        var provider = new CustomAuthStateProvider(_httpClient, _jsRuntimeMock.Object);

        // Act
        var state = await provider.GetAuthenticationStateAsync();

        // Assert
        Assert.IsNotNull(state);
        Assert.IsNotNull(state.User.Identity);
        Assert.IsFalse(state.User.Identity.IsAuthenticated);
    }

    [TestMethod]
    public async Task MarkUserAsAuthenticated_PersistsDataAndNotifiesStateChange()
    {
        // Arrange
        var provider = new CustomAuthStateProvider(_httpClient, _jsRuntimeMock.Object);

        AuthenticationState? notifiedState = null;
        provider.AuthenticationStateChanged += task =>
        {
            notifiedState = task.Result;
        };

        // Act
        await provider.MarkUserAsAuthenticated("new-token-abc", "User", "bob");

        // Assert - Verify localStorage setItem was invoked for authToken, authRole, authUsername
        var setItemCalls = _jsRuntimeMock.Invocations
            .Where(inv => inv.Arguments.Count > 0 && (string)inv.Arguments[0] == "localStorage.setItem")
            .ToList();

        Assert.IsTrue(setItemCalls.Any(c => (string?)((object?[])c.Arguments[1])[0] == "authToken" && (string?)((object?[])c.Arguments[1])[1] == "new-token-abc"));
        Assert.IsTrue(setItemCalls.Any(c => (string?)((object?[])c.Arguments[1])[0] == "authRole" && (string?)((object?[])c.Arguments[1])[1] == "User"));
        Assert.IsTrue(setItemCalls.Any(c => (string?)((object?[])c.Arguments[1])[0] == "authUsername" && (string?)((object?[])c.Arguments[1])[1] == "bob"));

        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
        Assert.IsFalse(_httpClient.DefaultRequestHeaders.Contains("X-Browser-Id"));

        Assert.IsNotNull(notifiedState);
        Assert.IsTrue(notifiedState.User.Identity?.IsAuthenticated);
        Assert.AreEqual("bob", notifiedState.User.Identity?.Name);
        Assert.IsTrue(notifiedState.User.IsInRole("User"));
    }

    [TestMethod]
    public async Task MarkUserAsLoggedOut_ClearsStorageAndHeadersAndNotifiesUnauthenticated()
    {
        // Arrange
        var provider = new CustomAuthStateProvider(_httpClient, _jsRuntimeMock.Object);

        AuthenticationState? notifiedState = null;
        provider.AuthenticationStateChanged += task =>
        {
            notifiedState = task.Result;
        };

        // Act
        await provider.MarkUserAsLoggedOut();

        // Assert - Verify clearAuthData was invoked
        var clearCalls = _jsRuntimeMock.Invocations
            .Where(inv => inv.Arguments.Count > 0 && (string)inv.Arguments[0] == "window.authStorage.clearAuthData")
            .ToList();

        Assert.AreEqual(1, clearCalls.Count);
        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
        Assert.IsNotNull(notifiedState);
        Assert.IsFalse(notifiedState.User.Identity?.IsAuthenticated);
    }

    [TestMethod]
    public async Task GetBrowserIdAsync_GeneratesAndStoresGuidWhenEmpty()
    {
        // Arrange
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string>("localStorage.getItem", It.Is<object?[]>(args => (string?)args[0] == "browserId")))
            .ReturnsAsync(string.Empty);

        var provider = new CustomAuthStateProvider(_httpClient, _jsRuntimeMock.Object);

        // Act
        var browserId1 = await provider.GetBrowserIdAsync();
        var browserId2 = await provider.GetBrowserIdAsync(); // Should return cached value

        // Assert
        Assert.IsFalse(string.IsNullOrWhiteSpace(browserId1));
        Assert.AreEqual(browserId1, browserId2);
        Assert.IsTrue(Guid.TryParse(browserId1, out _));

        var setItemCalls = _jsRuntimeMock.Invocations
            .Where(inv => inv.Arguments.Count > 0 && (string)inv.Arguments[0] == "localStorage.setItem")
            .ToList();

        Assert.IsTrue(setItemCalls.Any(c => (string?)((object?[])c.Arguments[1])[0] == "browserId" && (string?)((object?[])c.Arguments[1])[1] == browserId1));
    }

    [TestMethod]
    public async Task GetAuthenticationStateAsync_WhenBatchJsThrows_FallsBackToIndividualLocalStorageKeys()
    {
        // Arrange - Batch method fails
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string[]>("window.authStorage.getAuthData", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("getAuthData undefined"));

        // Individual keys succeed
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string?>("localStorage.getItem", It.Is<object?[]>(args => (string?)args[0] == "authToken")))
            .ReturnsAsync("fallback-token-456");
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string?>("localStorage.getItem", It.Is<object?[]>(args => (string?)args[0] == "authRole")))
            .ReturnsAsync("Admin");
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string?>("localStorage.getItem", It.Is<object?[]>(args => (string?)args[0] == "authUsername")))
            .ReturnsAsync("carol");
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string>("localStorage.getItem", It.Is<object?[]>(args => (string?)args[0] == "browserId")))
            .ReturnsAsync("browser-id-carol");

        var provider = new CustomAuthStateProvider(_httpClient, _jsRuntimeMock.Object);

        // Act
        var state = await provider.GetAuthenticationStateAsync();

        // Assert
        Assert.IsNotNull(state);
        Assert.IsTrue(state.User.Identity?.IsAuthenticated);
        Assert.AreEqual("carol", state.User.Identity?.Name);
        Assert.IsTrue(state.User.IsInRole("Admin"));
        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
        Assert.IsFalse(_httpClient.DefaultRequestHeaders.Contains("X-Browser-Id"));
    }

    [TestMethod]
    public async Task MarkUserAsLoggedOut_WhenClearAuthDataThrows_FallsBackToIndividualLocalStorageRemovals()
    {
        // Arrange
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<IJSVoidResult>("window.authStorage.clearAuthData", It.IsAny<CancellationToken>(), It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("clearAuthData undefined"));
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<IJSVoidResult>("window.authStorage.clearAuthData", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("clearAuthData undefined"));

        var provider = new CustomAuthStateProvider(_httpClient, _jsRuntimeMock.Object);

        // Act
        await provider.MarkUserAsLoggedOut();

        // Assert - Verify individual localStorage.removeItem calls occurred
        var removeItemCalls = _jsRuntimeMock.Invocations
            .Where(inv => inv.Arguments.Count > 0 && (string)inv.Arguments[0] == "localStorage.removeItem")
            .ToList();

        Assert.AreEqual(3, removeItemCalls.Count);
        Assert.IsTrue(removeItemCalls.Any(c => (string?)((object?[])c.Arguments[1])[0] == "authToken"));
        Assert.IsTrue(removeItemCalls.Any(c => (string?)((object?[])c.Arguments[1])[0] == "authRole"));
        Assert.IsTrue(removeItemCalls.Any(c => (string?)((object?[])c.Arguments[1])[0] == "authUsername"));
        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
    }

    [TestMethod]
    public async Task MarkUserAsLoggedOut_ConcurrentInvocations_GuardsWithAtomicLock()
    {
        // Arrange
        var provider = new CustomAuthStateProvider(_httpClient, _jsRuntimeMock.Object);

        // Act - Invoke concurrent logouts simultaneously
        var t1 = provider.MarkUserAsLoggedOut();
        var t2 = provider.MarkUserAsLoggedOut();
        var t3 = provider.MarkUserAsLoggedOut();
        await Task.WhenAll(t1, t2, t3);

        // Assert - Headers cleared, state unauthenticated
        Assert.IsNull(_httpClient.DefaultRequestHeaders.Authorization);
        var state = await provider.GetAuthenticationStateAsync();
        Assert.IsFalse(state.User.Identity?.IsAuthenticated);
    }
}
