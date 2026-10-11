using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Api.Hubs;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.Tasks;

[TestClass]
public class TasksCleanupServiceTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private IServiceProvider _serviceProvider = null!;
    private AgentSystemOptions _sysOptions = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();
        _sysOptions = _dbFixture.CreateSystemOptions();
        _sysOptions.IsTasksCleanupServiceEnabled = true;
        _sysOptions.TasksCleanupDays = 7;

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
        services.AddSingleton(hubMock.Object);
        services.AddScoped<AgentTaskStore>();
        services.AddSingleton<TaskOperationsService>();
        services.AddScoped<AuthService>();
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        _dbFixture.Dispose();
    }

    [TestMethod]
    public async Task CleanupOldTasksAsync_DeletesCompletedAndFailedTasksOlderThanCleanupDays()
    {
        // Arrange
        var oldCompletedId = Guid.NewGuid();
        var oldFailedId = Guid.NewGuid();
        var oldDate = DateTime.UtcNow.AddDays(-10);

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = oldCompletedId,
                AgentName = "TestAgent",
                UserPrompt = "Old completed task",
                State = TaskState.Completed,
                CreatedAt = oldDate,
                CompletedAt = oldDate
            });
            db.TaskSessions.Add(new TaskSession
            {
                Id = oldFailedId,
                AgentName = "TestAgent",
                UserPrompt = "Old failed task",
                State = TaskState.Failed,
                CreatedAt = oldDate,
                CompletedAt = oldDate
            });
            await db.SaveChangesAsync();
        }

        var cleanupService = new TasksCleanupService(
            _serviceProvider,
            _sysOptions,
            NullLogger<TasksCleanupService>.Instance);

        // Act
        await cleanupService.CleanupOldTasksAsync(CancellationToken.None);

        // Assert - Both old tasks should be deleted
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            Assert.IsNull(await verifyDb.TaskSessions.FindAsync(oldCompletedId));
            Assert.IsNull(await verifyDb.TaskSessions.FindAsync(oldFailedId));
        }
    }

    [TestMethod]
    public async Task CleanupOldTasksAsync_DeletesCancelledTasksOlderThanCleanupDays()
    {
        // Arrange
        var oldCancelledId = Guid.NewGuid();
        var oldDate = DateTime.UtcNow.AddDays(-10);

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = oldCancelledId,
                AgentName = "TestAgent",
                UserPrompt = "Old cancelled task",
                State = TaskState.Cancelled,
                CreatedAt = oldDate,
                CompletedAt = oldDate
            });
            await db.SaveChangesAsync();
        }

        var cleanupService = new TasksCleanupService(
            _serviceProvider,
            _sysOptions,
            NullLogger<TasksCleanupService>.Instance);

        // Act
        await cleanupService.CleanupOldTasksAsync(CancellationToken.None);

        // Assert - Cancelled task should be deleted
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            Assert.IsNull(await verifyDb.TaskSessions.FindAsync(oldCancelledId));
        }
    }

    [TestMethod]
    public async Task CleanupOldTasksAsync_IgnoresActiveAndRecentTasks()
    {
        // Arrange
        var oldActiveId = Guid.NewGuid();
        var recentCompletedId = Guid.NewGuid();
        var oldDate = DateTime.UtcNow.AddDays(-10);
        var recentDate = DateTime.UtcNow.AddDays(-1);

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = oldActiveId,
                AgentName = "TestAgent",
                UserPrompt = "Old active task",
                State = TaskState.InProgress,
                CreatedAt = oldDate
            });
            db.TaskSessions.Add(new TaskSession
            {
                Id = recentCompletedId,
                AgentName = "TestAgent",
                UserPrompt = "Recent completed task",
                State = TaskState.Completed,
                CreatedAt = recentDate,
                CompletedAt = recentDate
            });
            await db.SaveChangesAsync();
        }

        var cleanupService = new TasksCleanupService(
            _serviceProvider,
            _sysOptions,
            NullLogger<TasksCleanupService>.Instance);

        // Act
        await cleanupService.CleanupOldTasksAsync(CancellationToken.None);

        // Assert - Active task and recent completed task must remain
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            Assert.IsNotNull(await verifyDb.TaskSessions.FindAsync(oldActiveId));
            Assert.IsNotNull(await verifyDb.TaskSessions.FindAsync(recentCompletedId));
        }
    }

    [TestMethod]
    public async Task CleanupOldTasksAsync_RestartRaceCondition_ProtectsRestartedTask()
    {
        // Arrange
        var restartedTaskId = Guid.NewGuid();
        var oldDate = DateTime.UtcNow.AddDays(-10);

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = restartedTaskId,
                AgentName = "TestAgent",
                UserPrompt = "Restarted task",
                State = TaskState.Completed,
                CreatedAt = oldDate,
                CompletedAt = oldDate
            });
            await db.SaveChangesAsync();
        }

        // Register task as active in TaskOperationsService to simulate restart / continuation
        var taskOps = _serviceProvider.GetRequiredService<TaskOperationsService>();
        taskOps.RegisterTask(restartedTaskId);

        var cleanupService = new TasksCleanupService(
            _serviceProvider,
            _sysOptions,
            NullLogger<TasksCleanupService>.Instance);

        // Act
        await cleanupService.CleanupOldTasksAsync(CancellationToken.None);

        // Assert - Task was active, so cleanup skipped it
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            Assert.IsNotNull(await verifyDb.TaskSessions.FindAsync(restartedTaskId));
        }

        taskOps.UnregisterTask(restartedTaskId);
    }

    [TestMethod]
    public async Task PruneExpiredTokensAsync_RemovesExpiredSessions()
    {
        // Arrange
        var expiredToken = "expired-token-" + Guid.NewGuid().ToString("N");
        var validToken = "valid-token-" + Guid.NewGuid().ToString("N");

        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = expiredToken,
                UserId = Guid.NewGuid(),
                Username = "user1",
                Role = "User",
                IPAddress = "127.0.0.1",
                BrowserId = "b1",
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-10)
            });
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = validToken,
                UserId = Guid.NewGuid(),
                Username = "user2",
                Role = "User",
                IPAddress = "127.0.0.1",
                BrowserId = "b2",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(2)
            });
            await tokenDb.SaveChangesAsync();
        }

        // Act
        using (var scope = _serviceProvider.CreateScope())
        {
            var authService = scope.ServiceProvider.GetRequiredService<AuthService>();
            await authService.PruneExpiredTokensAsync(CancellationToken.None);
        }

        // Assert
        using (var verifyTokenDb = _dbFixture.CreateTokenDbContext())
        {
            Assert.IsNull(await verifyTokenDb.Sessions.FindAsync(expiredToken));
            Assert.IsNotNull(await verifyTokenDb.Sessions.FindAsync(validToken));
        }
    }

    [TestMethod]
    public async Task CleanupOldTasksAsync_WhenCleanupWorkspacesTrue_DeletesWorkspaceDirectoryFromDisk()
    {
        // Arrange
        var oldTaskId = Guid.NewGuid();
        var oldDate = DateTime.UtcNow.AddDays(-10);

        _sysOptions.CleanupWorkspacesOnTaskDelete = true;
        var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(oldTaskId, _sysOptions);
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "artifact.txt"), "old workspace artifact");

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = oldTaskId,
                AgentName = "TestAgent",
                UserPrompt = "Old completed task with workspace",
                State = TaskState.Completed,
                CreatedAt = oldDate,
                CompletedAt = oldDate
            });
            await db.SaveChangesAsync();
        }

        var cleanupService = new TasksCleanupService(
            _serviceProvider,
            _sysOptions,
            NullLogger<TasksCleanupService>.Instance);

        // Act
        await cleanupService.CleanupOldTasksAsync(CancellationToken.None);

        // Assert - Task deleted from DB and directory deleted from disk
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            Assert.IsNull(await verifyDb.TaskSessions.FindAsync(oldTaskId));
        }
        Assert.IsFalse(Directory.Exists(workspacePath));
    }

    [TestMethod]
    public async Task CleanupOldTasksAsync_WhenCleanupWorkspacesFalse_PreservesWorkspaceDirectoryOnDisk()
    {
        // Arrange
        var oldTaskId = Guid.NewGuid();
        var oldDate = DateTime.UtcNow.AddDays(-10);

        _sysOptions.CleanupWorkspacesOnTaskDelete = false;
        var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(oldTaskId, _sysOptions);
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "artifact.txt"), "preserved workspace artifact");

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = oldTaskId,
                AgentName = "TestAgent",
                UserPrompt = "Old completed task with workspace",
                State = TaskState.Completed,
                CreatedAt = oldDate,
                CompletedAt = oldDate
            });
            await db.SaveChangesAsync();
        }

        var cleanupService = new TasksCleanupService(
            _serviceProvider,
            _sysOptions,
            NullLogger<TasksCleanupService>.Instance);

        // Act
        await cleanupService.CleanupOldTasksAsync(CancellationToken.None);

        // Assert - Task deleted from DB but directory preserved on disk
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            Assert.IsNull(await verifyDb.TaskSessions.FindAsync(oldTaskId));
        }
        Assert.IsTrue(Directory.Exists(workspacePath), "Workspace directory must be preserved when CleanupWorkspacesOnTaskDelete is false.");
    }

    [TestMethod]
    public async Task CleanupOldTasksAsync_CleansUpTaskStateManagerAndWorkspaceLockManagerSessions()
    {
        // Arrange
        var oldTaskId = Guid.NewGuid();
        var oldDate = DateTime.UtcNow.AddDays(-10);

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = oldTaskId,
                AgentName = "TestAgent",
                UserPrompt = "Old task for lock cleanup",
                State = TaskState.Completed,
                CreatedAt = oldDate,
                CompletedAt = oldDate
            });
            await db.SaveChangesAsync();
        }

        var stateManagerMock = new Mock<SemanticAgentBroker.Core.Features.TaskState.ITaskStateManager>();
        var lockManagerMock = new Mock<SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager>();

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
        services.AddSingleton(hubMock.Object);
        services.AddScoped<AgentTaskStore>();
        services.AddSingleton<TaskOperationsService>();
        services.AddScoped(_ => stateManagerMock.Object);
        services.AddScoped(_ => lockManagerMock.Object);
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        var cleanupService = new TasksCleanupService(
            sp,
            _sysOptions,
            NullLogger<TasksCleanupService>.Instance);

        // Act
        await cleanupService.CleanupOldTasksAsync(CancellationToken.None);

        // Assert
        stateManagerMock.Verify(m => m.CleanupSession(oldTaskId), Times.Once);
        lockManagerMock.Verify(m => m.CleanupSession(oldTaskId), Times.Once);
    }

    [TestMethod]
    public async Task CleanupOldTasksAsync_MultipleTasks_CleansUpEachInIndependentScope()
    {
        // Arrange
        var oldDate = DateTime.UtcNow.AddDays(-10);
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = id1,
                AgentName = "TestAgent",
                UserPrompt = "Task 1",
                State = TaskState.Completed,
                CreatedAt = oldDate,
                CompletedAt = oldDate
            });
            db.TaskSessions.Add(new TaskSession
            {
                Id = id2,
                AgentName = "TestAgent",
                UserPrompt = "Task 2",
                State = TaskState.Completed,
                CreatedAt = oldDate,
                CompletedAt = oldDate
            });
            await db.SaveChangesAsync();
        }

        var cleanupService = new TasksCleanupService(
            _serviceProvider,
            _sysOptions,
            NullLogger<TasksCleanupService>.Instance);

        // Act
        await cleanupService.CleanupOldTasksAsync(CancellationToken.None);

        // Assert
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            Assert.IsNull(await verifyDb.TaskSessions.FindAsync(id1));
            Assert.IsNull(await verifyDb.TaskSessions.FindAsync(id2));
        }
    }

    [TestMethod]
    public async Task CleanupDoesNotDeleteRestartedTask()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var oldDate = DateTime.UtcNow.AddDays(-10);

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Task restarted to Draft",
                State = TaskState.Draft,
                CreatedAt = oldDate,
                CompletedAt = oldDate
            });
            await db.SaveChangesAsync();
        }

        var taskStore = _serviceProvider.GetRequiredService<AgentTaskStore>();

        // Act - Invoke DeleteTaskWithCleanupAsync on the restarted task
        await taskStore.DeleteTaskWithCleanupAsync(taskId, cleanupWorkspace: true);

        // Assert - TaskSession must NOT be deleted because it is not in a terminal state
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            var session = await verifyDb.TaskSessions.FindAsync(taskId);
            Assert.IsNotNull(session, "Restarted task session in Draft state must not be deleted.");
            Assert.AreEqual(TaskState.Draft, session.State);
        }
    }
}
