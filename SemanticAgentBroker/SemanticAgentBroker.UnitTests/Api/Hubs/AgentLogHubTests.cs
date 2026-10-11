using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Api.Hubs;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.Hubs;

[TestClass]
public class AgentLogHubTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private TaskOperationsService _taskOps = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private Mock<HubCallerContext> _hubContextMock = null!;
    private Mock<IGroupManager> _groupsMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();

        var services = new ServiceCollection();
        services.AddScoped(_ => _dbFixture.CreateAppDbContext());
        services.AddScoped(_ => _dbFixture.CreateAuthDbContext());
        services.AddScoped(_ => _dbFixture.CreateTokenDbContext());
        services.AddScoped<AuthService>();
        services.AddSingleton(_dbFixture.CreateSystemOptions());
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        _scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        _taskOps = new TaskOperationsService();

        _hubContextMock = new Mock<HubCallerContext>();
        _groupsMock = new Mock<IGroupManager>();
        _hubContextMock.Setup(c => c.ConnectionId).Returns("conn-123");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbFixture.Dispose();
    }

    [TestMethod]
    public async Task SubscribeToTask_AdminUser_AllowsSubscription()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Prompt",
                IsPrivate = true,
                CreatedByUserId = Guid.NewGuid() // Different owner
            });
            await db.SaveChangesAsync();
        }

        var items = new Dictionary<object, object?>
        {
            ["SessionInfo"] = new SessionInfo
            {
                UserId = Guid.NewGuid(),
                Username = "adminuser",
                Role = "Admin"
            }
        };
        _hubContextMock.Setup(c => c.Items).Returns(items);

        var hub = new AgentLogHub(_taskOps, null!, _scopeFactory)
        {
            Context = _hubContextMock.Object,
            Groups = _groupsMock.Object
        };

        // Act
        await hub.SubscribeToTask(taskId);

        // Assert - Admin should be added to SignalR group
        _groupsMock.Verify(g => g.AddToGroupAsync("conn-123", taskId.ToString(), default), Times.Once);
    }

    [TestMethod]
    public async Task SubscribeToTask_NonOwnerPrivateTask_ForbidsSubscription()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Prompt",
                IsPrivate = true,
                CreatedByUserId = ownerId
            });
            await db.SaveChangesAsync();
        }

        var items = new Dictionary<object, object?>
        {
            ["SessionInfo"] = new SessionInfo
            {
                UserId = callerUserId,
                Username = "otheruser",
                Role = "User"
            }
        };
        _hubContextMock.Setup(c => c.Items).Returns(items);

        var hub = new AgentLogHub(_taskOps, null!, _scopeFactory)
        {
            Context = _hubContextMock.Object,
            Groups = _groupsMock.Object
        };

        // Act
        await hub.SubscribeToTask(taskId);

        // Assert - Non-owner must not be added to SignalR group
        _groupsMock.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [TestMethod]
    public async Task SubscribeToTask_NonOwnerPublicNonPluginTask_AllowsSubscription()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Prompt",
                IsPrivate = false, // Public non-plugin task
                CreatedByUserId = ownerId
            });
            await db.SaveChangesAsync();
        }

        var items = new Dictionary<object, object?>
        {
            ["SessionInfo"] = new SessionInfo
            {
                UserId = Guid.NewGuid(), // Non-owner
                Username = "otheruser",
                Role = "User"
            }
        };
        _hubContextMock.Setup(c => c.Items).Returns(items);

        var hub = new AgentLogHub(_taskOps, null!, _scopeFactory)
        {
            Context = _hubContextMock.Object,
            Groups = _groupsMock.Object
        };

        // Act
        await hub.SubscribeToTask(taskId);

        // Assert - Non-owner should be allowed to subscribe to public non-plugin task
        _groupsMock.Verify(g => g.AddToGroupAsync("conn-123", taskId.ToString(), default), Times.Once);
    }

    [TestMethod]
    public async Task SendInterruption_AuthorizedUser_ForwardsToTaskOperationsService()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Prompt",
                IsPrivate = false,
                CreatedByUserId = userId
            });
            await db.SaveChangesAsync();
        }

        _taskOps.RegisterTask(taskId);

        var items = new Dictionary<object, object?>
        {
            ["SessionInfo"] = new SessionInfo
            {
                UserId = userId,
                Username = "taskowner",
                Role = "User"
            }
        };
        _hubContextMock.Setup(c => c.Items).Returns(items);

        var hub = new AgentLogHub(_taskOps, null!, _scopeFactory)
        {
            Context = _hubContextMock.Object,
            Groups = _groupsMock.Object
        };

        // Act
        bool sent = await hub.SendInterruption(taskId, "Please stop and change directory.");

        // Assert
        Assert.IsTrue(sent);
        var interruption = _taskOps.TryReadInterruption(taskId);
        Assert.IsNotNull(interruption);
        Assert.AreEqual("Please stop and change directory.", interruption.Message);
        Assert.AreEqual("taskowner", interruption.UserName);

        _taskOps.UnregisterTask(taskId);
    }

    [TestMethod]
    public async Task SendInterruption_NonOwnerPublicNonPluginTask_ForbidsAction()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Prompt",
                IsPrivate = false,
                CreatedByUserId = ownerId
            });
            await db.SaveChangesAsync();
        }

        _taskOps.RegisterTask(taskId);

        var items = new Dictionary<object, object?>
        {
            ["SessionInfo"] = new SessionInfo
            {
                UserId = callerUserId,
                Username = "otheruser",
                Role = "User"
            }
        };
        _hubContextMock.Setup(c => c.Items).Returns(items);

        var hub = new AgentLogHub(_taskOps, null!, _scopeFactory)
        {
            Context = _hubContextMock.Object,
            Groups = _groupsMock.Object
        };

        // Act
        bool sent = await hub.SendInterruption(taskId, "Malicious interruption");

        // Assert - Non-owner must not be allowed to interrupt another user's task
        Assert.IsFalse(sent, "Non-owner must not be authorized to interrupt public custom tasks.");
        var interruption = _taskOps.TryReadInterruption(taskId);
        Assert.IsNull(interruption, "Interruption should not have been registered in TaskOperationsService.");

        _taskOps.UnregisterTask(taskId);
    }

    [TestMethod]
    public async Task ApproveStep_NonOwnerPublicNonPluginTask_ForbidsAction()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Prompt",
                IsPrivate = false,
                CreatedByUserId = ownerId
            });
            await db.SaveChangesAsync();
        }

        _taskOps.RegisterTask(taskId);
        _taskOps.PrepareApprovalGate(taskId);

        var items = new Dictionary<object, object?>
        {
            ["SessionInfo"] = new SessionInfo
            {
                UserId = callerUserId,
                Username = "otheruser",
                Role = "User"
            }
        };
        _hubContextMock.Setup(c => c.Items).Returns(items);

        var hub = new AgentLogHub(_taskOps, null!, _scopeFactory)
        {
            Context = _hubContextMock.Object,
            Groups = _groupsMock.Object
        };

        // Act - Non-owner attempts to approve step
        await hub.ApproveStep(taskId);

        // Assert - Approval gate should still be pending / not approved
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        bool approved = false;
        try
        {
            approved = await _taskOps.WaitForApprovalAsync(taskId, cts.Token);
        }
        catch (OperationCanceledException)
        {
            approved = false;
        }

        Assert.IsFalse(approved, "Non-owner must not be authorized to approve steps on public custom tasks.");
        _taskOps.UnregisterTask(taskId);
    }

    [TestMethod]
    public async Task ModifyPlan_NonOwnerPublicNonPluginTask_ForbidsAction()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Prompt",
                IsPrivate = false,
                CreatedByUserId = ownerId
            });
            await db.SaveChangesAsync();
        }

        _taskOps.RegisterTask(taskId);

        var items = new Dictionary<object, object?>
        {
            ["SessionInfo"] = new SessionInfo
            {
                UserId = callerUserId,
                Username = "otheruser",
                Role = "User"
            }
        };
        _hubContextMock.Setup(c => c.Items).Returns(items);

        var hub = new AgentLogHub(_taskOps, null!, _scopeFactory)
        {
            Context = _hubContextMock.Object,
            Groups = _groupsMock.Object
        };

        // Act - Non-owner attempts to modify plan
        await hub.ModifyPlan(taskId, "# Hacked Plan");

        // Assert - Plan decision should still be pending / not resolved
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        bool decisionReceived = false;
        try
        {
            await _taskOps.WaitForPlanDecisionAsync(taskId, cts.Token);
            decisionReceived = true;
        }
        catch (OperationCanceledException)
        {
            decisionReceived = false;
        }

        Assert.IsFalse(decisionReceived, "Non-owner must not be authorized to modify plans on public custom tasks.");
        _taskOps.UnregisterTask(taskId);
    }

    [TestMethod]
    public async Task ApproveStep_And_RejectStep_DelegatesToTaskOperationsService()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Prompt",
                CreatedByUserId = userId
            });
            await db.SaveChangesAsync();
        }

        _taskOps.RegisterTask(taskId);

        var items = new Dictionary<object, object?>
        {
            ["SessionInfo"] = new SessionInfo
            {
                UserId = userId,
                Username = "taskowner",
                Role = "User"
            }
        };
        _hubContextMock.Setup(c => c.Items).Returns(items);

        var hub = new AgentLogHub(_taskOps, null!, _scopeFactory)
        {
            Context = _hubContextMock.Object,
            Groups = _groupsMock.Object
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act 1: Approve Step
        var approvalTask = Task.Run(() => hub.ApproveStep(taskId));
        var approved = await _taskOps.WaitForApprovalAsync(taskId, cts.Token);
        await approvalTask;

        // Assert
        Assert.IsTrue(approved);

        // Act 2: Reject Step
        var rejectionTask = Task.Run(() => hub.RejectStep(taskId, "Invalid command"));
        var rejected = await _taskOps.WaitForApprovalAsync(taskId, cts.Token);
        await rejectionTask;

        // Assert
        Assert.IsFalse(rejected);
        var rejection = _taskOps.TryReadStepRejectionFeedback(taskId);
        Assert.IsNotNull(rejection);
        Assert.AreEqual("Invalid command", rejection.Value.Feedback);

        _taskOps.UnregisterTask(taskId);
    }

    [TestMethod]
    public async Task UnsubscribeFromTask_RemovesConnectionFromSignalRGroup()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var hub = new AgentLogHub(_taskOps, null!, _scopeFactory)
        {
            Context = _hubContextMock.Object,
            Groups = _groupsMock.Object
        };

        // Act
        await hub.UnsubscribeFromTask(taskId);

        // Assert
        _groupsMock.Verify(g => g.RemoveFromGroupAsync("conn-123", taskId.ToString(), default), Times.Once);
    }

    [TestMethod]
    public async Task PlanReviewActions_ForwardToTaskOperationsService()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Plan review hub test",
                CreatedByUserId = userId
            });
            await db.SaveChangesAsync();
        }

        _taskOps.RegisterTask(taskId);

        var items = new Dictionary<object, object?>
        {
            ["SessionInfo"] = new SessionInfo
            {
                UserId = userId,
                Username = "taskowner",
                Role = "User"
            }
        };
        _hubContextMock.Setup(c => c.Items).Returns(items);

        var hub = new AgentLogHub(_taskOps, null!, _scopeFactory)
        {
            Context = _hubContextMock.Object,
            Groups = _groupsMock.Object
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act 1: Approve Plan
        var planWait1 = _taskOps.WaitForPlanDecisionAsync(taskId, cts.Token);
        await hub.ApprovePlan(taskId);
        var res1 = await planWait1;
        Assert.AreEqual(PlanAction.Approve, res1.Action);

        // Act 2: Modify Plan
        var planWait2 = _taskOps.WaitForPlanDecisionAsync(taskId, cts.Token);
        await hub.ModifyPlan(taskId, "# Modified Content");
        var res2 = await planWait2;
        Assert.AreEqual(PlanAction.Modify, res2.Action);
        Assert.AreEqual("# Modified Content", res2.ModifiedPlan);

        // Act 3: Reject Plan
        var planWait3 = _taskOps.WaitForPlanDecisionAsync(taskId, cts.Token);
        await hub.RejectPlan(taskId, "Too risky");
        var res3 = await planWait3;
        Assert.AreEqual(PlanAction.Reject, res3.Action);
        Assert.AreEqual("Too risky", res3.Feedback);

        // Act 4: Ask Question
        var planWait4 = _taskOps.WaitForPlanDecisionAsync(taskId, cts.Token);
        await hub.AskPlanQuestion(taskId, "Can we run this locally?");
        var res4 = await planWait4;
        Assert.AreEqual(PlanAction.AskQuestion, res4.Action);
        Assert.AreEqual("Can we run this locally?", res4.Feedback);

        _taskOps.UnregisterTask(taskId);
    }
}
