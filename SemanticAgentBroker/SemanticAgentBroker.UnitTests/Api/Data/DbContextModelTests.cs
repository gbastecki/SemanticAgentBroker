using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.Data;

[TestClass]
public class DbContextModelTests
{
    private TestDatabaseFixture _dbFixture = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbFixture.Dispose();
    }

    [TestMethod]
    public async Task AppDbContext_ModelConfiguration_EnforcesKeysCascadesAndJsonSerialization()
    {
        var sessionId = Guid.NewGuid();
        var pluginId = "CustomPlugin_" + Guid.NewGuid().ToString("N")[..8];
        var agentId = Guid.NewGuid();

        using (var db = _dbFixture.CreateAppDbContext())
        {
            // 1. TaskSession + Cascading children
            var session = new TaskSession
            {
                Id = sessionId,
                AgentName = "Orchestrator",
                UserPrompt = "Test Cascades",
                State = TaskState.InProgress,
                Events = [new TaskEvent { Id = Guid.NewGuid(), SessionId = sessionId, EventType = "TestEvent", Payload = "payload" }],
                ChatHistory = [new ChatMessageSnapshot { Id = Guid.NewGuid(), SessionId = sessionId, SequenceNumber = 1, Role = "user", Content = "Hi" }],
                Plans = [new ImplementationPlan { Id = Guid.NewGuid(), SessionId = sessionId, Version = 1, PlanMarkdown = "# Plan", Status = PlanStatus.Draft }]
            };
            db.TaskSessions.Add(session);

            // 2. PluginRegistration with JSON collections & ConfigValues
            var plugin = new PluginRegistration
            {
                Id = pluginId,
                DisplayName = "Test Plugin",
                Tags = ["ai", "tools", "utility"],
                RequiredPlugins = ["FileIO"],
                ConfigValues = [new PluginConfigValue { Id = Guid.NewGuid(), PluginId = pluginId, Key = "ApiKey", Value = "secret" }]
            };
            db.PluginRegistrations.Add(plugin);

            // 3. AgentDefinition with MethodBindings
            var agent = new AgentDefinition
            {
                Id = agentId,
                Name = "AgentWithBindings",
                MethodBindings = [new AgentMethodBinding { Id = Guid.NewGuid(), AgentId = agentId, PluginId = pluginId, MethodId = "Run" }]
            };
            db.AgentDefinitions.Add(agent);

            await db.SaveChangesAsync();
        }

        // Verify JSON deserialization & persistence
        using (var db = _dbFixture.CreateAppDbContext())
        {
            var plugin = await db.PluginRegistrations.Include(p => p.ConfigValues).FirstAsync(p => p.Id == pluginId);
            Assert.AreEqual(3, plugin.Tags.Count);
            CollectionAssert.Contains((System.Collections.ICollection)plugin.Tags, "tools");
            Assert.AreEqual(1, plugin.RequiredPlugins.Count);
            CollectionAssert.Contains((System.Collections.ICollection)plugin.RequiredPlugins, "FileIO");
            Assert.AreEqual(1, plugin.ConfigValues.Count);

            // Verify cascading delete on TaskSession
            var session = await db.TaskSessions.FirstAsync(s => s.Id == sessionId);
            db.TaskSessions.Remove(session);
            await db.SaveChangesAsync();

            Assert.IsFalse(await db.TaskEvents.AnyAsync(e => e.SessionId == sessionId));
            Assert.IsFalse(await db.ChatMessages.AnyAsync(m => m.SessionId == sessionId));
            Assert.IsFalse(await db.ImplementationPlans.AnyAsync(p => p.SessionId == sessionId));
        }
    }

    [TestMethod]
    public async Task AuthDbContext_ModelConfiguration_EnforcesKeysCascadesAndJsonSerialization()
    {
        var userId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        using (var db = _dbFixture.CreateAuthDbContext())
        {
            var user = new User
            {
                Id = userId,
                Username = "db_user_" + Guid.NewGuid().ToString("N")[..6],
                PasswordHash = "hash123",
                Role = "User",
                TaskPermissions = [
                    new UserTaskPermission
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        PluginId = "FileIO",
                        PluginTaskId = "ReadTask",
                        AllowedOptions = ["Read", "List"]
                    }
                ]
            };

            var group = new UserGroup
            {
                Id = groupId,
                Name = "db_group_" + Guid.NewGuid().ToString("N")[..6],
                TaskPermissions = [
                    new UserGroupTaskPermission
                    {
                        Id = Guid.NewGuid(),
                        UserGroupId = groupId,
                        PluginId = "PythonSandbox",
                        PluginTaskId = "ExecTask",
                        AllowedOptions = ["Run"]
                    }
                ]
            };

            user.Groups.Add(group);
            db.Users.Add(user);
            db.UserGroups.Add(group);

            db.FailedLoginAttempts.Add(new FailedLoginAttempt
            {
                Id = Guid.NewGuid(),
                IPAddress = "192.168.1.100",
                Attempts = 1,
                LockoutEnd = DateTime.UtcNow.AddMinutes(5)
            });

            await db.SaveChangesAsync();
        }

        // Verify JSON serialization and cascade deletes
        using (var db = _dbFixture.CreateAuthDbContext())
        {
            var user = await db.Users.Include(u => u.TaskPermissions).Include(u => u.Groups).FirstAsync(u => u.Id == userId);
            Assert.AreEqual(1, user.TaskPermissions.Count);
            CollectionAssert.Contains(user.TaskPermissions[0].AllowedOptions, "Read");
            Assert.AreEqual(1, user.Groups.Count);

            // Delete user - verify cascade deletes UserTaskPermissions
            db.Users.Remove(user);
            await db.SaveChangesAsync();

            Assert.IsFalse(await db.UserTaskPermissions.AnyAsync(p => p.UserId == userId));
        }
    }

    [TestMethod]
    public async Task TokenDbContext_ModelConfiguration_EnforcesKeysAndIndices()
    {
        string token = "test_token_" + Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();

        using (var db = _dbFixture.CreateTokenDbContext())
        {
            var session = new UserSessionRecord
            {
                Token = token,
                UserId = userId,
                Username = "session_user",
                Role = "User",
                IPAddress = "127.0.0.1",
                BrowserId = "browser-test",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(4)
            };

            db.Sessions.Add(session);
            await db.SaveChangesAsync();
        }

        using (var db = _dbFixture.CreateTokenDbContext())
        {
            var session = await db.Sessions.FindAsync(token);
            Assert.IsNotNull(session);
            Assert.AreEqual("session_user", session.Username);
            Assert.AreEqual(userId, session.UserId);
        }
    }

    [TestMethod]
    public async Task AuthDbContext_FailedLoginAttempt_AllowsMultipleFailedUsersFromSameIpAddress()
    {
        using (var db = _dbFixture.CreateAuthDbContext())
        {
            var attempt1 = new FailedLoginAttempt
            {
                Id = Guid.NewGuid(),
                IPAddress = "192.168.1.50",
                Username = "user_alice",
                Attempts = 1,
                LockoutEnd = null
            };

            var attempt2 = new FailedLoginAttempt
            {
                Id = Guid.NewGuid(),
                IPAddress = "192.168.1.50",
                Username = "user_bob",
                Attempts = 2,
                LockoutEnd = null
            };

            db.FailedLoginAttempts.Add(attempt1);
            db.FailedLoginAttempts.Add(attempt2);
            await db.SaveChangesAsync();
        }

        using (var db = _dbFixture.CreateAuthDbContext())
        {
            var count = await db.FailedLoginAttempts.CountAsync(f => f.IPAddress == "192.168.1.50");
            Assert.AreEqual(2, count);
        }
    }
}
