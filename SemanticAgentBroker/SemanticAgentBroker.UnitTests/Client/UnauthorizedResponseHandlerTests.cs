using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using SemanticAgentBroker.Client.Features.Auth;
using System.Net;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class UnauthorizedResponseHandlerTests
{
    private class InnerHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }

    [TestMethod]
    public async Task SendAsync_On401ForProtectedEndpoint_TriggersLogout()
    {
        // Arrange
        var jsMock = new Mock<IJSRuntime>();
        var httpClient = new HttpClient();
        var authStateProvider = new CustomAuthStateProvider(httpClient, jsMock.Object);

        var services = new ServiceCollection();
        services.AddSingleton(authStateProvider);
        var serviceProvider = services.BuildServiceProvider();

        var inner = new InnerHandler
        {
            Handler = req => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };

        var handler = new UnauthorizedResponseHandler(serviceProvider)
        {
            InnerHandler = inner
        };

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:5001/") };
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "expired-token");

        // Act
        var response = await client.GetAsync("/api/agents");

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        // Verify logout was triggered by checking invocations on jsMock
        var clearCalls = jsMock.Invocations
            .Where(inv => inv.Arguments.Count > 0 && (string)inv.Arguments[0] == "window.authStorage.clearAuthData")
            .ToList();

        Assert.AreEqual(1, clearCalls.Count);
    }

    [TestMethod]
    public async Task SendAsync_On401ForValidateEndpoint_DoesNotTriggerRecursiveLogout()
    {
        // Arrange
        var jsMock = new Mock<IJSRuntime>();
        var httpClient = new HttpClient();
        var authStateProvider = new CustomAuthStateProvider(httpClient, jsMock.Object);

        var services = new ServiceCollection();
        services.AddSingleton(authStateProvider);
        var serviceProvider = services.BuildServiceProvider();

        var inner = new InnerHandler
        {
            Handler = req => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };

        var handler = new UnauthorizedResponseHandler(serviceProvider)
        {
            InnerHandler = inner
        };

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:5001/") };

        // Act
        var response = await client.GetAsync("/api/auth/validate");

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        // Verify window.authStorage.clearAuthData was NOT called
        var clearCalls = jsMock.Invocations
            .Where(inv => inv.Arguments.Count > 0 && (string)inv.Arguments[0] == "window.authStorage.clearAuthData")
            .ToList();

        Assert.AreEqual(0, clearCalls.Count);
    }

    [TestMethod]
    public async Task SendAsync_On401ForLoginEndpoint_DoesNotTriggerLogout()
    {
        // Arrange
        var jsMock = new Mock<IJSRuntime>();
        var httpClient = new HttpClient();
        var authStateProvider = new CustomAuthStateProvider(httpClient, jsMock.Object);

        var services = new ServiceCollection();
        services.AddSingleton(authStateProvider);
        var serviceProvider = services.BuildServiceProvider();

        var inner = new InnerHandler
        {
            Handler = req => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };

        var handler = new UnauthorizedResponseHandler(serviceProvider)
        {
            InnerHandler = inner
        };

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:5001/") };

        // Act
        var response = await client.PostAsync("/api/auth/login", new StringContent("{}"));

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        // Verify window.authStorage.clearAuthData was NOT called
        var clearCalls = jsMock.Invocations
            .Where(inv => inv.Arguments.Count > 0 && (string)inv.Arguments[0] == "window.authStorage.clearAuthData")
            .ToList();

        Assert.AreEqual(0, clearCalls.Count);
    }

    [TestMethod]
    public async Task SendAsync_On200OkResponse_PassesThroughWithoutTriggeringLogout()
    {
        // Arrange
        var jsMock = new Mock<IJSRuntime>();
        var httpClient = new HttpClient();
        var authStateProvider = new CustomAuthStateProvider(httpClient, jsMock.Object);

        var services = new ServiceCollection();
        services.AddSingleton(authStateProvider);
        var serviceProvider = services.BuildServiceProvider();

        var inner = new InnerHandler
        {
            Handler = req => new HttpResponseMessage(HttpStatusCode.OK)
        };

        var handler = new UnauthorizedResponseHandler(serviceProvider)
        {
            InnerHandler = inner
        };

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:5001/") };

        // Act
        var response = await client.GetAsync("/api/tasks");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(0, jsMock.Invocations.Count);
    }

    [TestMethod]
    public async Task SendAsync_OnHttpRequestException_RethrowsException()
    {
        // Arrange
        var jsMock = new Mock<IJSRuntime>();
        var httpClient = new HttpClient();
        var authStateProvider = new CustomAuthStateProvider(httpClient, jsMock.Object);

        var services = new ServiceCollection();
        services.AddSingleton(authStateProvider);
        var serviceProvider = services.BuildServiceProvider();

        var inner = new InnerHandler
        {
            Handler = req => throw new HttpRequestException("Connection refused")
        };

        var handler = new UnauthorizedResponseHandler(serviceProvider)
        {
            InnerHandler = inner
        };

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:5001/") };

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await client.GetAsync("/api/tasks");
        });
    }

    [TestMethod]
    public async Task SendAsync_On401WithNullRequestUri_TriggersLogoutWithoutNullReferenceException()
    {
        // Arrange
        var jsMock = new Mock<IJSRuntime>();
        var httpClient = new HttpClient();
        var authStateProvider = new CustomAuthStateProvider(httpClient, jsMock.Object);

        var services = new ServiceCollection();
        services.AddSingleton(authStateProvider);
        var serviceProvider = services.BuildServiceProvider();

        var inner = new InnerHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };

        var handler = new UnauthorizedResponseHandler(serviceProvider)
        {
            InnerHandler = inner
        };

        var invoker = new HttpMessageInvoker(handler);
        var requestWithoutUri = new HttpRequestMessage(HttpMethod.Get, (Uri?)null);

        // Act
        var response = await invoker.SendAsync(requestWithoutUri, CancellationToken.None);

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        var clearCalls = jsMock.Invocations
            .Where(inv => inv.Arguments.Count > 0 && (string)inv.Arguments[0] == "window.authStorage.clearAuthData")
            .ToList();
        Assert.AreEqual(1, clearCalls.Count);
    }
}
