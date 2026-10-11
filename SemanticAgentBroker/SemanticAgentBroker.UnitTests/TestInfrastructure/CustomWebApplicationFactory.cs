using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;
using System.Net.Http.Json;
using System.Text.Json;

namespace SemanticAgentBroker.UnitTests.TestInfrastructure;

/// <summary>
/// WebApplicationFactory that points all database connections to a dedicated
/// isolated test database directory (%CURRENT_USER%\SemanticAgentBrokerDataTests\test_{guid}\).
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<SemanticAgentBroker.Api.ApiEntryPoint>
{
    public TestDatabaseFixture DbFixture { get; }

    public CustomWebApplicationFactory()
    {
        DbFixture = new TestDatabaseFixture();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((context, config) =>
        {
            var testConfig = new Dictionary<string, string?>
            {
                ["AgentSystem:DatabaseConnectionString"] = $"Data Source={DbFixture.BrokerDbPath};Pooling=False",
                ["AgentSystem:AuthDatabaseConnectionString"] = $"Data Source={DbFixture.AuthDbPath};Pooling=False",
                ["AgentSystem:TokensDatabaseConnectionString"] = $"Data Source={DbFixture.TokensDbPath};Pooling=False",
                ["AgentSystem:TaskWorkspacesPath"] = Path.Combine(DbFixture.TestDirectory, "workspaces"),
                ["AgentSystem:RestApi:IsEnabled"] = "false",
                ["AiProviders:HealthCheck:Enabled"] = "false",
                ["AiProviders:Inference:MaxLlmRetries"] = "0"
            };

            config.AddInMemoryCollection(testConfig);
        });

        builder.ConfigureServices(services =>
        {
            // Remove background hosted services (health check, task queue loop, cleanup) to prevent unmocked background loops during integration tests
            var hostedServices = services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList();
            foreach (var hs in hostedServices)
            {
                services.Remove(hs);
            }

            // Remove existing DbContext registrations
            var appDbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (appDbDescriptor != null) services.Remove(appDbDescriptor);

            var authDbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AuthDbContext>));
            if (authDbDescriptor != null) services.Remove(authDbDescriptor);

            var tokenDbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<TokenDbContext>));
            if (tokenDbDescriptor != null) services.Remove(tokenDbDescriptor);

            // Remove existing AgentSystemOptions registrations
            var sysOptionsDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(AgentSystemOptions));
            if (sysOptionsDescriptor != null) services.Remove(sysOptionsDescriptor);

            var iOptionsDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Options.IOptions<AgentSystemOptions>));
            if (iOptionsDescriptor != null) services.Remove(iOptionsDescriptor);

            var sysOptions = DbFixture.CreateSystemOptions();
            services.AddSingleton(sysOptions);
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(sysOptions));

            // Re-add with isolated SQLite test paths
            services.AddDbContext<AppDbContext>(opts => opts.UseSqlite($"Data Source={DbFixture.BrokerDbPath};Pooling=False"));
            services.AddDbContext<AuthDbContext>(opts => opts.UseSqlite($"Data Source={DbFixture.AuthDbPath};Pooling=False"));
            services.AddDbContext<TokenDbContext>(opts => opts.UseSqlite($"Data Source={DbFixture.TokensDbPath};Pooling=False"));

            // Ensure test schemas are created directly via DbFixture
            using var appDb = DbFixture.CreateAppDbContext();
            using var authDb = DbFixture.CreateAuthDbContext();
            using var tokenDb = DbFixture.CreateTokenDbContext();
        });
    }

    /// <summary>
    /// Creates an authenticated HttpClient pre-configured with a valid bearer token and X-Browser-Id.
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(string username = "admin", string role = "Admin", string browserId = "test-browser")
    {
        using (var scope = Services.CreateScope())
        {
            var authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var user = await authDb.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user == null)
            {
                user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = username,
                    Role = role,
                    CreatedAt = DateTime.UtcNow
                };
                user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "TestPass123!");
                authDb.Users.Add(user);
                await authDb.SaveChangesAsync();
            }
        }

        var token = await GetAuthTokenAsync(username, role, browserId);
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Browser-Id", browserId);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Authenticates a user and returns their bearer token string.
    /// </summary>
    public async Task<string> GetAuthTokenAsync(string username = "admin", string role = "Admin", string browserId = "test-browser")
    {
        using (var scope = Services.CreateScope())
        {
            var authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var user = await authDb.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user == null)
            {
                user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = username,
                    Role = role,
                    CreatedAt = DateTime.UtcNow
                };
                user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "TestPass123!");
                authDb.Users.Add(user);
                await authDb.SaveChangesAsync();
            }
        }

        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Browser-Id", browserId);

        var loginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, "TestPass123!", browserId));
        if (!loginResp.IsSuccessStatusCode)
        {
            loginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, "admin", browserId));
        }

        var doc = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("token").GetString()!;
    }

    /// <summary>
    /// Creates an authenticated Microsoft.AspNetCore.SignalR.Client.HubConnection connected to /loghub.
    /// </summary>
    public async Task<Microsoft.AspNetCore.SignalR.Client.HubConnection> CreateSignalRConnectionAsync(
        string username = "admin", string role = "Admin", string browserId = "test-browser")
    {
        var token = await GetAuthTokenAsync(username, role, browserId);

        var connection = new Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, $"/loghub?browserId={Uri.EscapeDataString(browserId)}"), options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Headers["X-Browser-Id"] = browserId;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        return connection;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            DbFixture.Dispose();
        }
    }
}
