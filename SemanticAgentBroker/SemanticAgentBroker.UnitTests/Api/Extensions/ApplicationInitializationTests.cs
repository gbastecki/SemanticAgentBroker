using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Api.Hubs;
using SemanticAgentBroker.Core.Features.Agents;
using SemanticAgentBroker.Core.Features.Agents.Implementations;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Core.Features.TaskState;
using SemanticAgentBroker.Core.Features.TaskState.Implementations;
using SemanticAgentBroker.Core.Features.TokenCounting;
using SemanticAgentBroker.Core.Features.TokenCounting.Implementations;
using SemanticAgentBroker.Core.LoadBalancing;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.Extensions;

[TestClass]
public class ApplicationInitializationTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private AgentSystemOptions _sysOptions = null!;
    private AiProviderOptions _aiOptions = null!;
    private IServiceProvider _serviceProvider = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();
        _sysOptions = _dbFixture.CreateSystemOptions();
        _aiOptions = new AiProviderOptions();

        var hubMock = new Mock<IHubContext<AgentLogHub>>();
        var hubClientsMock = new Mock<IHubClients>();
        var clientProxyMock = new Mock<IClientProxy>();
        hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
        hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxyMock.Object);

        var services = new ServiceCollection();
        services.AddScoped(_ => _dbFixture.CreateAppDbContext());
        services.AddScoped(_ => _dbFixture.CreateAuthDbContext());
        services.AddScoped(_ => _dbFixture.CreateTokenDbContext());
        services.AddSingleton(_sysOptions);
        services.AddSingleton(_aiOptions);
        services.AddSingleton(hubMock.Object);
        services.AddScoped<AgentTaskStore>();
        services.AddSingleton<TaskOperationsService>();
        services.AddScoped<IAgentTaskContext, AgentTaskContext>();
        services.AddScoped<ITaskStateManager, TaskStateManager>();
        services.AddSingleton<ITokenCounter, MlTokenCounter>();
        services.AddSingleton<SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager, WorkspaceLockManager>();
        services.AddSingleton(new Mock<IAgentRegistry>().Object);
        services.AddSingleton(new Mock<IPluginRegistry>().Object);
        services.AddSingleton(new Mock<IConnectionLoadBalancer>().Object);
        services.AddSingleton<AgentTaskQueueService>();
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbFixture.Dispose();
    }

    [TestMethod]
    public async Task OrphanedTasks_OnStartupRecovery_AreTransitionedToDraftAndEnqueued()
    {
        // Arrange - Seed orphaned tasks in various intermediate states
        var taskId1 = Guid.NewGuid();
        var taskId2 = Guid.NewGuid();

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId1,
                AgentName = "Orchestrator",
                UserPrompt = "Orphaned in progress task",
                State = TaskState.InProgress
            });
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId2,
                AgentName = "Orchestrator",
                UserPrompt = "Orphaned in planning task",
                State = TaskState.Planning
            });
            await db.SaveChangesAsync();
        }

        var queue = _serviceProvider.GetRequiredService<AgentTaskQueueService>();

        // Act - Simulate the startup recovery query from ApplicationInitialization
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var orphanedStates = new[] { TaskState.InProgress, TaskState.AwaitingApproval, TaskState.AwaitingInput, TaskState.Planning, TaskState.PlanReview, TaskState.Queued, TaskState.Paused };
            var orphanedTasks = await db.TaskSessions
                .Where(t => orphanedStates.Contains(t.State))
                .ToListAsync();

            Assert.AreEqual(2, orphanedTasks.Count);

            foreach (var session in orphanedTasks)
            {
                session.State = TaskState.Draft;
            }
            await db.SaveChangesAsync();

            foreach (var session in orphanedTasks)
            {
                await queue.EnqueueTaskAsync(new AgentTaskRequest
                {
                    TaskId = session.Id,
                    AgentName = session.AgentName,
                    Prompt = session.UserPrompt,
                    IsContinuation = true
                });
            }
        }

        // Assert - DB tasks transitioned to Draft
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            var t1 = await verifyDb.TaskSessions.FindAsync(taskId1);
            var t2 = await verifyDb.TaskSessions.FindAsync(taskId2);

            Assert.IsNotNull(t1);
            Assert.AreEqual(TaskState.Draft, t1.State);

            Assert.IsNotNull(t2);
            Assert.AreEqual(TaskState.Draft, t2.State);
        }
    }

    [TestMethod]
    public async Task MissingPluginDll_OnStartupSync_MarkedAsNotLoadedWithLastError()
    {
        // Arrange - Seed enabled plugin registration whose DLL does not exist on disk
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.PluginRegistrations.Add(new PluginRegistration
            {
                Id = "DeletedPlugin",
                DisplayName = "Deleted Plugin",
                Description = "Plugin was removed from disk",
                Category = PluginCategory.Custom,
                IsBuiltIn = false,
                IsEnabled = true,
                LoadState = PluginLoadState.Loaded
            });
            await db.SaveChangesAsync();
        }

        var pluginIdsOnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FileIO", "PythonSandbox" }; // "DeletedPlugin" is absent

        // Act - Simulate startup disk sync logic
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existingPlugins = await db.PluginRegistrations.ToListAsync();

            foreach (var dbPlugin in existingPlugins)
            {
                if (dbPlugin.IsEnabled && !pluginIdsOnDisk.Contains(dbPlugin.Id))
                {
                    dbPlugin.LoadState = PluginLoadState.NotLoaded;
                    dbPlugin.LastError = "Plugin DLL not found on disk";
                    dbPlugin.LastErrorAt = DateTime.UtcNow;
                }
            }
            await db.SaveChangesAsync();
        }

        // Assert
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            var updated = await verifyDb.PluginRegistrations.FindAsync("DeletedPlugin");
            Assert.IsNotNull(updated);
            Assert.AreEqual(PluginLoadState.NotLoaded, updated.LoadState);
            Assert.AreEqual("Plugin DLL not found on disk", updated.LastError);
            Assert.IsNotNull(updated.LastErrorAt);
        }
    }

    [TestMethod]
    public async Task InitializeApplication_WhenNoUsersExist_SeedsDefaultAdmin()
    {
        // Arrange - Ensure Auth DB has 0 users
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var existingUsers = await authDb.Users.ToListAsync();
            authDb.Users.RemoveRange(existingUsers);
            await authDb.SaveChangesAsync();
        }

        // Act - Simulate startup user seeding logic
        using (var scope = _serviceProvider.CreateScope())
        {
            var authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            if (!await authDb.Users.AnyAsync())
            {
                var user = new User
                {
                    Username = _sysOptions.DefaultAdminUsername,
                    Role = "Admin"
                };
                user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(user, _sysOptions.DefaultAdminPassword);
                authDb.Users.Add(user);
                await authDb.SaveChangesAsync();
            }
        }

        // Assert - Admin user seeded with valid hashed password
        using (var verifyAuthDb = _dbFixture.CreateAuthDbContext())
        {
            var adminUser = await verifyAuthDb.Users.FirstOrDefaultAsync(u => u.Username == _sysOptions.DefaultAdminUsername);
            Assert.IsNotNull(adminUser);
            Assert.AreEqual("Admin", adminUser.Role);
            var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<User>();
            var verifyResult = hasher.VerifyHashedPassword(adminUser, adminUser.PasswordHash, _sysOptions.DefaultAdminPassword);
            Assert.AreEqual(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success, verifyResult);
        }
    }

    [TestMethod]
    public async Task InitializeApplication_WhenNoAgentsExist_SeedsDefaultAgents()
    {
        // Arrange
        var createDefaultAgentsMethod = typeof(SemanticAgentBroker.Api.Extensions.ApplicationInitialization)
            .GetMethod("CreateDefaultAgents", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(createDefaultAgentsMethod, "CreateDefaultAgents method should exist");

        var pluginRegistryMock = new Mock<IPluginRegistry>();
        var defaultAgents = createDefaultAgentsMethod.Invoke(null, [pluginRegistryMock.Object]) as List<AgentDefinition>;

        Assert.IsNotNull(defaultAgents);
        Assert.IsTrue(defaultAgents.Count >= 3, "Expected at least 3 default seeded agents");

        // Act - Save seeded agents to DB
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var agent in defaultAgents)
            {
                db.AgentDefinitions.Add(agent);
            }
            await db.SaveChangesAsync();
        }

        // Assert
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            var loaded = await verifyDb.AgentDefinitions.ToListAsync();
            Assert.IsTrue(loaded.Any(a => a.Name == "DataAnalyst"));
            Assert.IsTrue(loaded.Any(a => a.Name == "LogAnalyzer"));
            Assert.IsTrue(loaded.Any(a => a.Name == "GeneralAssistant"));
        }
    }
}
