using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Shared.Configuration;
namespace SemanticAgentBroker.UnitTests.TestInfrastructure;

/// <summary>
/// Manages isolated SQLite test databases under %CURRENT_USER%\SemanticAgentBrokerDataTests\test_{guid}\
/// to ensure test runs NEVER touch or pollute production database files.
/// Automatically cleans up test databases on disposal.
/// </summary>
public class TestDatabaseFixture : IDisposable
{
    public string TestDirectory { get; }
    public string BrokerDbPath { get; }
    public string AuthDbPath { get; }
    public string TokensDbPath { get; }

    public TestDatabaseFixture()
    {
        var testId = Guid.NewGuid().ToString("N");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        TestDirectory = Path.Combine(userProfile, "SemanticAgentBrokerDataTests", $"test_{testId}");
        Directory.CreateDirectory(TestDirectory);

        BrokerDbPath = Path.Combine(TestDirectory, "broker.db");
        AuthDbPath = Path.Combine(TestDirectory, "auth.db");
        TokensDbPath = Path.Combine(TestDirectory, "tokens.db");
    }

    public AppDbContext CreateAppDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={BrokerDbPath};Pooling=False")
            .Options;

        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    public AuthDbContext CreateAuthDbContext()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlite($"Data Source={AuthDbPath};Pooling=False")
            .Options;

        var context = new AuthDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    public TokenDbContext CreateTokenDbContext()
    {
        var options = new DbContextOptionsBuilder<TokenDbContext>()
            .UseSqlite($"Data Source={TokensDbPath};Pooling=False")
            .Options;

        var context = new TokenDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    public AgentSystemOptions CreateSystemOptions()
    {
        return new AgentSystemOptions
        {
            DatabaseConnectionString = $"Data Source={BrokerDbPath};Pooling=False",
            AuthDatabaseConnectionString = $"Data Source={AuthDbPath};Pooling=False",
            TokensDatabaseConnectionString = $"Data Source={TokensDbPath};Pooling=False",
            TaskWorkspacesPath = Path.Combine(TestDirectory, "workspaces"),
            MaxConcurrentAgents = 2,
            MaxFailedAttempts = 3,
            LockoutMinutes = 15,
            SessionDays = 1,
            MaxUploadSizeMb = 10
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(TestDirectory))
            {
                Directory.Delete(TestDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore file lock release delays during test cleanup
        }
    }
}
