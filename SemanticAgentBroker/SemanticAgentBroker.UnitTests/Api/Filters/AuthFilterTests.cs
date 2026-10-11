using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.Api.Filters;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Net;

namespace SemanticAgentBroker.UnitTests.Api.Filters;

[TestClass]
public class AuthFilterTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private IServiceProvider _serviceProvider = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();

        var services = new ServiceCollection();
        services.AddScoped(_ => _dbFixture.CreateAuthDbContext());
        services.AddScoped(_ => _dbFixture.CreateTokenDbContext());
        services.AddSingleton(_dbFixture.CreateSystemOptions());
        services.AddScoped<AuthService>();
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbFixture.Dispose();
    }

    [TestMethod]
    public async Task InvokeAsync_WithMissingOrInvalidAuthorizationHeader_ReturnsUnauthorized()
    {
        // Arrange
        var filter = new AuthFilter();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = _serviceProvider
        };

        var context = EndpointFilterInvocationContext.Create(httpContext);
        bool nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>("success");
        };

        // Act 1: No auth header
        var result1 = await filter.InvokeAsync(context, next);
        Assert.IsInstanceOfType<UnauthorizedHttpResult>(result1);
        Assert.IsFalse(nextCalled);

        // Act 2: Non-Bearer header
        httpContext.Request.Headers["Authorization"] = "Basic 12345";
        var result2 = await filter.InvokeAsync(context, next);
        Assert.IsInstanceOfType<UnauthorizedHttpResult>(result2);
        Assert.IsFalse(nextCalled);
    }

    private static string HashToken(string token) => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    [TestMethod]
    public async Task InvokeAsync_WithValidBearerTokenAndBrowserId_SetsSessionInfoAndInvokesNext()
    {
        // Arrange
        var filter = new AuthFilter();
        string token = "test-jwt-token-" + Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        var browserId = "browser-123";
        var ip = "192.168.1.100";

        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = HashToken(token),
                UserId = userId,
                Username = "testuser",
                Role = "Admin",
                IPAddress = ip,
                BrowserId = browserId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(2)
            });
            await tokenDb.SaveChangesAsync();
        }

        var httpContext = new DefaultHttpContext
        {
            RequestServices = _serviceProvider
        };
        httpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        httpContext.Request.Headers["X-Browser-Id"] = browserId;
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(ip);

        var context = EndpointFilterInvocationContext.Create(httpContext);
        bool nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>("success");
        };

        // Act
        var result = await filter.InvokeAsync(context, next);

        // Assert
        Assert.IsTrue(nextCalled);
        Assert.AreEqual("success", result);
        Assert.IsTrue(httpContext.Items.ContainsKey("SessionInfo"));
        var session = httpContext.Items["SessionInfo"] as SessionInfo;
        Assert.IsNotNull(session);
        Assert.AreEqual("testuser", session.Username);
        Assert.AreEqual("Admin", session.Role);
        Assert.AreEqual(userId, session.UserId);
    }

    [TestMethod]
    public async Task InvokeAsync_WithExpiredOrInvalidToken_ReturnsUnauthorized()
    {
        // Arrange
        var filter = new AuthFilter();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = _serviceProvider
        };
        httpContext.Request.Headers["Authorization"] = "Bearer invalid-token-xyz";
        httpContext.Request.Headers["X-Browser-Id"] = "browser-1";
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");

        var context = EndpointFilterInvocationContext.Create(httpContext);
        bool nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>("success");
        };

        // Act
        var result = await filter.InvokeAsync(context, next);

        // Assert
        Assert.IsFalse(nextCalled);
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task InvokeAsync_WithMissingAuthHeader_ReturnsUnauthorized()
    {
        // Arrange
        var filter = new AuthFilter();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = _serviceProvider
        };
        httpContext.Request.Headers["X-Browser-Id"] = "browser-1";
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");

        var context = EndpointFilterInvocationContext.Create(httpContext);
        bool nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>("success");
        };

        // Act
        var result = await filter.InvokeAsync(context, next);

        // Assert
        Assert.IsFalse(nextCalled);
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task InvokeAsync_WithNullRemoteIpAddress_DefaultsToUnknownIpAndSucceeds()
    {
        // Arrange
        var filter = new AuthFilter();
        string token = "test-token-null-ip-" + Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        var browserId = "browser-null-ip";

        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = HashToken(token),
                UserId = userId,
                Username = "ipuser",
                Role = "User",
                IPAddress = "unknown",
                BrowserId = browserId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(2)
            });
            await tokenDb.SaveChangesAsync();
        }

        var httpContext = new DefaultHttpContext
        {
            RequestServices = _serviceProvider
        };
        httpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        httpContext.Request.Headers["X-Browser-Id"] = browserId;
        httpContext.Connection.RemoteIpAddress = null; // Explicitly null IP

        var context = EndpointFilterInvocationContext.Create(httpContext);
        bool nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>("success");
        };

        // Act
        var result = await filter.InvokeAsync(context, next);

        // Assert
        Assert.IsTrue(nextCalled);
        Assert.AreEqual("success", result);
        var session = httpContext.Items["SessionInfo"] as SessionInfo;
        Assert.IsNotNull(session);
        Assert.AreEqual("ipuser", session.Username);
    }
}
