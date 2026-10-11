using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.Auth;

[TestClass]
public class SignalRAuthMiddlewareTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private IServiceProvider _serviceProvider = null!;
    private AuthService _authService = null!;

    private static string HashToken(string token) => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();
        var sysOptions = _dbFixture.CreateSystemOptions();

        var services = new ServiceCollection();
        services.AddScoped(_ => _dbFixture.CreateAuthDbContext());
        services.AddScoped(_ => _dbFixture.CreateTokenDbContext());
        services.AddSingleton(Options.Create(sysOptions));
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<AuthService>>(NullLogger<AuthService>.Instance);
        services.AddScoped<AuthService>();

        _serviceProvider = services.BuildServiceProvider();
        _authService = _serviceProvider.GetRequiredService<AuthService>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbFixture.Dispose();
    }

    [TestMethod]
    public async Task InvokeAsync_NonLogHubPath_PassesThroughWithoutValidating()
    {
        // Arrange
        bool nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new SignalRAuthMiddleware(next);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/tasks";

        // Act
        await middleware.InvokeAsync(context, _authService);

        // Assert
        Assert.IsTrue(nextCalled);
        Assert.IsFalse(context.Items.ContainsKey("SessionInfo"));
        Assert.AreEqual(200, context.Response.StatusCode); // Default unchanged status
    }

    [TestMethod]
    public async Task InvokeAsync_LogHubWithValidQueryTokenAndBrowserId_SetsSessionAndCallsNext()
    {
        // Arrange - Create valid session in TokenDbContext
        var token = "signalr-query-token-" + Guid.NewGuid().ToString("N");
        var browserId = "browser-test-hub-1";
        var user = new User { Id = Guid.NewGuid(), Username = "signalr_user", Role = "User" };

        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = HashToken(token),
                UserId = user.Id,
                Username = user.Username,
                Role = user.Role,
                IPAddress = "127.0.0.1",
                BrowserId = browserId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            await tokenDb.SaveChangesAsync();
        }

        bool nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new SignalRAuthMiddleware(next);
        var context = new DefaultHttpContext();
        context.Request.Path = "/loghub";
        context.Request.QueryString = new QueryString($"?access_token={token}&browserId={browserId}");
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");

        // Act
        await middleware.InvokeAsync(context, _authService);

        // Assert
        Assert.IsTrue(nextCalled);
        Assert.IsTrue(context.Items.ContainsKey("SessionInfo"));
        var session = context.Items["SessionInfo"] as SessionInfo;
        Assert.IsNotNull(session);
        Assert.AreEqual("signalr_user", session.Username);
    }

    [TestMethod]
    public async Task InvokeAsync_LogHubWithValidHeaderTokenAndBrowserId_SetsSessionAndCallsNext()
    {
        // Arrange
        var token = "signalr-header-token-" + Guid.NewGuid().ToString("N");
        var browserId = "browser-header-hub-2";
        var user = new User { Id = Guid.NewGuid(), Username = "header_user", Role = "Admin" };

        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = HashToken(token),
                UserId = user.Id,
                Username = user.Username,
                Role = user.Role,
                IPAddress = "127.0.0.1",
                BrowserId = browserId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            await tokenDb.SaveChangesAsync();
        }

        bool nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new SignalRAuthMiddleware(next);
        var context = new DefaultHttpContext();
        context.Request.Path = "/loghub/negotiate";
        context.Request.Headers["Authorization"] = $"Bearer {token}";
        context.Request.Headers["X-Browser-Id"] = browserId;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");

        // Act
        await middleware.InvokeAsync(context, _authService);

        // Assert
        Assert.IsTrue(nextCalled);
        Assert.IsTrue(context.Items.ContainsKey("SessionInfo"));
    }

    [TestMethod]
    public async Task InvokeAsync_LogHubWithInvalidOrMissingToken_Returns401WithoutCallingNext()
    {
        // Arrange
        bool nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new SignalRAuthMiddleware(next);
        var context = new DefaultHttpContext();
        context.Request.Path = "/loghub";
        context.Request.QueryString = new QueryString("?access_token=invalid_token_123&browserId=b1");

        // Act
        await middleware.InvokeAsync(context, _authService);

        // Assert
        Assert.IsFalse(nextCalled);
        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }
}
