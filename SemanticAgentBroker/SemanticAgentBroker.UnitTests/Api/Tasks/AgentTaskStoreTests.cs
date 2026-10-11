using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Api.Hubs;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.Tasks;

[TestClass]
public class AgentTaskStoreTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private Mock<IHubContext<AgentLogHub>> _hubContextMock = null!;
    private Mock<IHubClients> _hubClientsMock = null!;
    private Mock<IClientProxy> _clientProxyMock = null!;
    private AppDbContext _db = null!;
    private AgentTaskStore _taskStore = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();
        _db = _dbFixture.CreateAppDbContext();

        _hubContextMock = new Mock<IHubContext<AgentLogHub>>();
        _hubClientsMock = new Mock<IHubClients>();
        _clientProxyMock = new Mock<IClientProxy>();

        _hubContextMock.Setup(h => h.Clients).Returns(_hubClientsMock.Object);
        _hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxyMock.Object);
        _hubClientsMock.Setup(c => c.All).Returns(_clientProxyMock.Object);

        _taskStore = new AgentTaskStore(_db, _hubContextMock.Object, _dbFixture.CreateSystemOptions());
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _dbFixture.Dispose();
    }

    [TestMethod]
    public async Task UpdateStateAsync_UpdatesDatabaseAndBroadcastsEvent()
    {
        // Arrange - create task session
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Sample prompt",
            State = TaskState.Draft
        });
        await _db.SaveChangesAsync();

        // Act
        await _taskStore.UpdateStateAsync(taskId, TaskState.InProgress, "Starting task execution");

        // Assert
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var session = await verifyDb.TaskSessions.FindAsync(taskId);
        Assert.IsNotNull(session);
        Assert.AreEqual(TaskState.InProgress, session.State);

        var taskEvent = verifyDb.TaskEvents.FirstOrDefault(e => e.SessionId == taskId && e.EventType == "StateChanged");
        Assert.IsNotNull(taskEvent);
        StringAssert.Contains(taskEvent.Payload, "InProgress: Starting task execution");
    }

    [TestMethod]
    public async Task UpdateStateAsync_WhenTransitioningToPlanning_SetsStartedAt()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Planning prompt",
            State = TaskState.Draft
        });
        await _db.SaveChangesAsync();

        var before = DateTime.UtcNow.AddSeconds(-1);

        // Act
        await _taskStore.UpdateStateAsync(taskId, TaskState.Planning, "Generating implementation plan...");

        // Assert
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var session = await verifyDb.TaskSessions.FindAsync(taskId);
        Assert.IsNotNull(session);
        Assert.AreEqual(TaskState.Planning, session.State);
        Assert.IsNotNull(session.StartedAt, "StartedAt must be populated when task enters Planning.");
        Assert.IsTrue(session.StartedAt >= before && session.StartedAt <= DateTime.UtcNow.AddSeconds(1));
    }

    [TestMethod]
    public async Task UpdateStateAsync_WhenTransitioningToInProgress_SetsStartedAt()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Execution prompt",
            State = TaskState.Queued
        });
        await _db.SaveChangesAsync();

        var before = DateTime.UtcNow.AddSeconds(-1);

        // Act
        await _taskStore.UpdateStateAsync(taskId, TaskState.InProgress, "Executing task...");

        // Assert
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var session = await verifyDb.TaskSessions.FindAsync(taskId);
        Assert.IsNotNull(session);
        Assert.AreEqual(TaskState.InProgress, session.State);
        Assert.IsNotNull(session.StartedAt, "StartedAt must be populated when task enters InProgress.");
        Assert.IsTrue(session.StartedAt >= before && session.StartedAt <= DateTime.UtcNow.AddSeconds(1));
    }

    [TestMethod]
    public async Task UpdateStateAsync_WhenAlreadyStarted_PreservesOriginalStartedAt()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var originalStartedAt = DateTime.UtcNow.AddMinutes(-5);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Multi-phase prompt",
            State = TaskState.Planning,
            StartedAt = originalStartedAt
        });
        await _db.SaveChangesAsync();

        // Act - transition from Planning to InProgress
        await _taskStore.UpdateStateAsync(taskId, TaskState.InProgress, "Execution phase started");

        // Assert
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var session = await verifyDb.TaskSessions.FindAsync(taskId);
        Assert.IsNotNull(session);
        Assert.AreEqual(TaskState.InProgress, session.State);
        Assert.AreEqual(originalStartedAt, session.StartedAt, "Original StartedAt must be preserved during subsequent transitions.");
    }

    [TestMethod]
    public async Task UpdateStateAsync_DetachesSession_PreventsStaleEntityOverwrite()
    {
        // Arrange - create task session
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Initial prompt",
            State = TaskState.Draft
        });
        await _db.SaveChangesAsync();

        // Act 1: Update state via taskStore
        await _taskStore.UpdateStateAsync(taskId, TaskState.InProgress);

        // Modify task session externally in a different DbContext (e.g. user updates prompt or plan)
        using (var extDb = _dbFixture.CreateAppDbContext())
        {
            var extSession = await extDb.TaskSessions.FindAsync(taskId);
            Assert.IsNotNull(extSession);
            extSession.UserPrompt = "Modified externally";
            await extDb.SaveChangesAsync();
        }

        // Act 2: Update state again via taskStore
        await _taskStore.UpdateStateAsync(taskId, TaskState.Completed);

        // Assert: The session entity must be detached from change tracker
        var entry = _db.ChangeTracker.Entries<TaskSession>().FirstOrDefault(e => e.Entity.Id == taskId);
        Assert.IsTrue(entry == null || entry.State == EntityState.Detached, "TaskSession must be detached from ChangeTracker to prevent memory bloat and stale reads.");
    }

    [TestMethod]
    public async Task UpdateStateAsync_WhenTaskIsPublic_BroadcastsToAllClients()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Public task",
            State = TaskState.Draft,
            IsPrivate = false
        });
        await _db.SaveChangesAsync();

        var allProxyMock = new Mock<IClientProxy>();
        _hubClientsMock.Setup(c => c.All).Returns(allProxyMock.Object);

        // Act
        await _taskStore.UpdateStateAsync(taskId, TaskState.InProgress, "Running public task");

        // Assert - verify broadcast sent to All
        allProxyMock.Verify(p => p.SendCoreAsync(
            HubMethodNames.TaskStateChanged,
            It.Is<object?[]>(args => args != null && args.Length == 1 && ((TaskStateChangedEvent)args[0]!).TaskId == taskId),
            default), Times.Once);
    }

    [TestMethod]
    public async Task UpdateStateAsync_WhenTaskIsPrivate_BroadcastsToTaskGroupAndUserGroupOnly()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Private task",
            State = TaskState.Draft,
            IsPrivate = true,
            CreatedByUserId = userId
        });
        await _db.SaveChangesAsync();

        var taskGroupProxyMock = new Mock<IClientProxy>();
        var userGroupProxyMock = new Mock<IClientProxy>();
        var allProxyMock = new Mock<IClientProxy>();

        _hubClientsMock.Setup(c => c.Group(taskId.ToString())).Returns(taskGroupProxyMock.Object);
        _hubClientsMock.Setup(c => c.Group($"user_{userId}")).Returns(userGroupProxyMock.Object);
        _hubClientsMock.Setup(c => c.All).Returns(allProxyMock.Object);

        // Act
        await _taskStore.UpdateStateAsync(taskId, TaskState.InProgress, "Running private task");

        // Assert - verify broadcast sent to Task group and User group, but NOT All
        taskGroupProxyMock.Verify(p => p.SendCoreAsync(
            HubMethodNames.TaskStateChanged,
            It.Is<object?[]>(args => args != null && args.Length == 1 && ((TaskStateChangedEvent)args[0]!).TaskId == taskId),
            default), Times.Once);

        userGroupProxyMock.Verify(p => p.SendCoreAsync(
            HubMethodNames.TaskStateChanged,
            It.Is<object?[]>(args => args != null && args.Length == 1 && ((TaskStateChangedEvent)args[0]!).TaskId == taskId),
            default), Times.Once);

        allProxyMock.Verify(p => p.SendCoreAsync(
            HubMethodNames.TaskStateChanged,
            It.IsAny<object?[]>(),
            default), Times.Never);
    }

    [TestMethod]
    public async Task LogEventAsync_PersistsTaskEventRecord()
    {
        // Arrange - create task session parent
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Log test",
            State = TaskState.Draft
        });
        await _db.SaveChangesAsync();

        // Act
        await _taskStore.LogEventAsync(taskId, "ToolCall", "Information", "Invoked FileIO-SaveToWorkspace");

        // Assert
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var evt = verifyDb.TaskEvents.FirstOrDefault(e => e.SessionId == taskId && e.EventType == "ToolCall");
        Assert.IsNotNull(evt);
        Assert.AreEqual("Information", evt.Level);
        Assert.AreEqual("Invoked FileIO-SaveToWorkspace", evt.Payload);
    }

    [TestMethod]
    public async Task SaveChatMessageAsync_And_GetChatHistoryAsync_MaintainsSequence()
    {
        // Arrange - create task session parent
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Chat test",
            State = TaskState.Draft
        });
        await _db.SaveChangesAsync();

        // Act
        await _taskStore.SaveChatMessageAsync(taskId, sequenceNumber: 0, role: "user", content: "Calculate sum", userName: "admin");
        await _taskStore.SaveChatMessageAsync(taskId, sequenceNumber: 1, role: "assistant", content: "Result is 42", agentName: "MathAgent");

        // Assert
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var history = await _taskStore.GetChatHistoryAsync(taskId);
        Assert.AreEqual(2, history.Count);
        Assert.AreEqual("user", history[0].Role);
        Assert.AreEqual("Calculate sum", history[0].Content);
        Assert.AreEqual("assistant", history[1].Role);
        Assert.AreEqual("Result is 42", history[1].Content);
    }

    [TestMethod]
    public async Task SavePlanAsync_And_ApprovePlanAsync_UpdatesPlanStatus()
    {
        // Arrange - create task session parent
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Plan test",
            State = TaskState.Draft
        });
        await _db.SaveChangesAsync();

        string planMarkdown = "1. Step one\n2. Step two";

        // Act 1: Save plan
        await _taskStore.SavePlanAsync(taskId, version: 1, planMarkdown: planMarkdown, openQuestions: null);

        var latestPlan = await _taskStore.GetLatestPlanAsync(taskId);
        Assert.IsNotNull(latestPlan);
        Assert.AreEqual(PlanStatus.AwaitingReview, latestPlan.Status);

        // Act 2: Approve plan
        await _taskStore.ApprovePlanAsync(taskId, version: 1);

        // Assert
        var approvedPlan = await _taskStore.GetLatestPlanAsync(taskId);
        Assert.IsNotNull(approvedPlan);
        Assert.AreEqual(PlanStatus.Approved, approvedPlan.Status);
        Assert.IsNotNull(approvedPlan.ApprovedAt);
    }

    [TestMethod]
    public async Task Concurrency_ParallelLogEvents_DoesNotThrowConcurrencyException()
    {
        // Arrange - create task session
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Parallel tools test",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        const int parallelWorkers = 20;
        const int eventsPerWorker = 10;

        // Act - execute concurrent logging across multiple tasks simulating parallel tool execution
        var tasks = Enumerable.Range(0, parallelWorkers).Select(workerId => Task.Run(async () =>
        {
            for (int i = 0; i < eventsPerWorker; i++)
            {
                await _taskStore.LogEventAsync(taskId, "ToolCall", "Information", $"Worker {workerId} event {i}");
                await _taskStore.SaveChatMessageAsync(taskId, workerId * 100 + i, "assistant", $"Worker {workerId} msg {i}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert - all events and messages saved without DbContext concurrency collision
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var eventCount = verifyDb.TaskEvents.Count(e => e.SessionId == taskId);
        var msgCount = verifyDb.ChatMessages.Count(m => m.SessionId == taskId);

        Assert.AreEqual(parallelWorkers * eventsPerWorker, eventCount);
        Assert.AreEqual(parallelWorkers * eventsPerWorker, msgCount);
    }

    [TestMethod]
    public void AgentTaskStore_ImplementsIDisposable_AndDisposesSafely()
    {
        // Arrange
        using var store = new AgentTaskStore(_db, _hubContextMock.Object, _dbFixture.CreateSystemOptions());

        // Act & Assert (store should be IDisposable and not throw on dispose or multiple disposes)
        Assert.IsInstanceOfType<IDisposable>(store);
        store.Dispose();
        store.Dispose(); // Multiple dispose calls must be idempotent and safe
    }

    [TestMethod]
    public async Task LogAsync_ForwardsToLogEventAsync_AndPersistsLog()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Log test",
            State = TaskState.Draft
        });
        await _db.SaveChangesAsync();

        // Act
        await _taskStore.LogAsync(taskId, "Information", "Filter observed function completion");

        // Assert
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var log = verifyDb.TaskEvents.FirstOrDefault(e => e.SessionId == taskId && e.EventType == "Log");
        Assert.IsNotNull(log);
        Assert.AreEqual("Information", log.Level);
        Assert.AreEqual("Filter observed function completion", log.Payload);
    }

    [TestMethod]
    public async Task GetMaxSequenceNumberAsync_WhenNoMessages_ReturnsMinusOne_WhenMessagesExist_ReturnsMax()
    {
        // Arrange
        var emptyTaskId = Guid.NewGuid();
        var populatedTaskId = Guid.NewGuid();

        _db.TaskSessions.AddRange(
            new TaskSession { Id = emptyTaskId, AgentName = "AgentA", UserPrompt = "PromptA" },
            new TaskSession { Id = populatedTaskId, AgentName = "AgentB", UserPrompt = "PromptB" }
        );
        await _db.SaveChangesAsync();

        await _taskStore.SaveChatMessageAsync(populatedTaskId, 1, "user", "Hello");
        await _taskStore.SaveChatMessageAsync(populatedTaskId, 7, "assistant", "World");

        // Act & Assert 1: Empty task
        int emptyMax = await _taskStore.GetMaxSequenceNumberAsync(emptyTaskId);
        Assert.AreEqual(-1, emptyMax);

        // Act & Assert 2: Populated task
        int populatedMax = await _taskStore.GetMaxSequenceNumberAsync(populatedTaskId);
        Assert.AreEqual(7, populatedMax);
    }

    [TestMethod]
    public async Task SetFinalResultAsync_UpdatesFinalResultInDatabase()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Final answer test",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        // Act
        await _taskStore.SetFinalResultAsync(taskId, "Calculation completed: 42");

        // Assert
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var session = await verifyDb.TaskSessions.FindAsync(taskId);
        Assert.IsNotNull(session);
        Assert.AreEqual("Calculation completed: 42", session.FinalResult);
    }

    [TestMethod]
    public async Task GetExpiredTasksAsync_ReturnsTasksCreatedBeforeCutoff()
    {
        // Arrange
        var oldTaskId = Guid.NewGuid();
        var newTaskId = Guid.NewGuid();

        _db.TaskSessions.AddRange(
            new TaskSession { Id = oldTaskId, AgentName = "OldAgent", UserPrompt = "Old", CreatedAt = DateTime.UtcNow.AddDays(-20) },
            new TaskSession { Id = newTaskId, AgentName = "NewAgent", UserPrompt = "New", CreatedAt = DateTime.UtcNow.AddDays(-2) }
        );
        await _db.SaveChangesAsync();

        // Act
        var expired = await _taskStore.GetExpiredTasksAsync(DateTime.UtcNow.AddDays(-10));

        // Assert
        Assert.IsTrue(expired.Any(t => t.Id == oldTaskId));
        Assert.IsFalse(expired.Any(t => t.Id == newTaskId));
    }

    [TestMethod]
    public async Task BroadcastMethods_InvokeSignalRGroupsWithExpectedContracts()
    {
        // Arrange
        var taskId = Guid.NewGuid();

        // Act - Invoke all broadcast helpers
        await _taskStore.BroadcastThoughtAsync(taskId, "Thought msg", "AgentX", "Obs");
        await _taskStore.BroadcastPlanAsync(taskId, "Plan msg", true);
        await _taskStore.BroadcastPlanReviewAsync(taskId, "Plan review msg", "Questions?", 1);
        await _taskStore.BroadcastToolExecutionAsync(taskId, "ToolName", "args", "res", true, "Subagent1");
        await _taskStore.BroadcastActivityAsync(taskId, "Running tool", "Subagent1");
        await _taskStore.BroadcastContextCompactionAsync(taskId, 10, 5);
        await _taskStore.BroadcastWorkspaceFileChangedAsync(taskId, "file.txt", "Modified");
        await _taskStore.BroadcastTokenCountAsync(taskId, 500, 16384, "Subagent");

        // Assert - verify SignalR client proxy was invoked
        _clientProxyMock.Verify(c => c.SendCoreAsync(
            It.IsAny<string>(),
            It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.AtLeast(8));
    }

    [TestMethod]
    public async Task BroadcastPlanRevisionAsync_SendsPlanRevisionEventToSignalRGroup()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        string plan = "# Revised Plan";
        int version = 2;

        // Act
        await _taskStore.BroadcastPlanRevisionAsync(taskId, plan, version);

        // Assert
        _clientProxyMock.Verify(c => c.SendCoreAsync(
            HubMethodNames.PlanRevision,
            It.Is<object?[]>(args => args.Length == 1 && args[0] is PlanRevisionEvent && ((PlanRevisionEvent)args[0]!).Version == 2 && ((PlanRevisionEvent)args[0]!).PlanMarkdown == plan),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task LogEventAsync_WhenDbUpdateExceptionOccurs_ClearsChangeTrackerToPreventDbContextPoisoning()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Valid task",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        // Seed a pre-existing event with fixed Id to induce duplicate PK constraint failure
        var duplicateId = Guid.NewGuid();
        _db.TaskEvents.Add(new TaskEvent
        {
            Id = duplicateId,
            SessionId = taskId,
            EventType = "Initial",
            Level = "System",
            Payload = "First",
            Timestamp = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Manually stage a duplicate PK entity to force DbUpdateException inside next store operation
        _db.TaskEvents.Add(new TaskEvent
        {
            Id = duplicateId, // Duplicate PK in DB
            SessionId = taskId,
            EventType = "Conflict",
            Level = "System",
            Payload = "Conflict",
            Timestamp = DateTime.UtcNow
        });

        // Act 1: Call LogEventAsync - this will trigger DbUpdateException and enter catch (DbUpdateException)
        await _taskStore.LogEventAsync(taskId, "Event1", "Info", "First attempt");

        // Assert 1: ChangeTracker must NOT retain any Added or broken entities
        using var verifyDb = _dbFixture.CreateAppDbContext();
        
        // Act 2: Subsequent logging for valid task must succeed and NOT be poisoned by previous failed entity
        await _taskStore.LogEventAsync(taskId, "Event2", "Info", "Second attempt after failure");

        // Assert 2: Second event was persisted successfully
        using var verifyDb2 = _dbFixture.CreateAppDbContext();
        var secondEvt = verifyDb2.TaskEvents.FirstOrDefault(e => e.SessionId == taskId && e.Payload == "Second attempt after failure");
        Assert.IsNotNull(secondEvt, "Second event must be successfully persisted and not blocked by poisoned DbContext.");
    }

    [TestMethod]
    public async Task UpdateStateAsync_WhenDbUpdateExceptionOccurs_ClearsChangeTrackerToPreventDbContextPoisoning()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Valid task",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        // Seed a pre-existing event with fixed Id to induce duplicate PK constraint failure
        var duplicateId = Guid.NewGuid();
        _db.TaskEvents.Add(new TaskEvent
        {
            Id = duplicateId,
            SessionId = taskId,
            EventType = "Initial",
            Level = "System",
            Payload = "First",
            Timestamp = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Manually stage a duplicate PK entity to force DbUpdateException inside next store operation
        _db.TaskEvents.Add(new TaskEvent
        {
            Id = duplicateId, // Duplicate PK in DB
            SessionId = taskId,
            EventType = "Conflict",
            Level = "System",
            Payload = "Conflict",
            Timestamp = DateTime.UtcNow
        });

        // Act 1: Call UpdateStateAsync - this will trigger DbUpdateException and enter catch (DbUpdateException)
        await _taskStore.UpdateStateAsync(taskId, TaskState.Completed, "Finishing task");

        // Act 2: Subsequent store operation for valid task must succeed and NOT be poisoned by previous failed entity
        await _taskStore.LogEventAsync(taskId, "Event2", "Info", "Second attempt after state update failure");

        // Assert: Second event was persisted successfully
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var secondEvt = verifyDb.TaskEvents.FirstOrDefault(e => e.SessionId == taskId && e.Payload == "Second attempt after state update failure");
        Assert.IsNotNull(secondEvt, "Second event must be successfully persisted and not blocked by poisoned DbContext.");
    }

    [TestMethod]
    public async Task SetFinalResultAsync_WhenDbUpdateExceptionOccurs_ClearsChangeTrackerToPreventDbContextPoisoning()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Valid task",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        // Seed a pre-existing event with fixed Id to induce duplicate PK constraint failure
        var duplicateId = Guid.NewGuid();
        _db.TaskEvents.Add(new TaskEvent
        {
            Id = duplicateId,
            SessionId = taskId,
            EventType = "Initial",
            Level = "System",
            Payload = "First",
            Timestamp = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Manually stage a duplicate PK entity to force DbUpdateException inside next store operation
        _db.TaskEvents.Add(new TaskEvent
        {
            Id = duplicateId, // Duplicate PK in DB
            SessionId = taskId,
            EventType = "Conflict",
            Level = "System",
            Payload = "Conflict",
            Timestamp = DateTime.UtcNow
        });

        // Act 1: Call SetFinalResultAsync - this will trigger DbUpdateException and enter catch (DbUpdateException)
        await _taskStore.SetFinalResultAsync(taskId, "Completed computation");

        // Act 2: Subsequent store operation for valid task must succeed and NOT be poisoned by previous failed entity
        await _taskStore.LogEventAsync(taskId, "Event2", "Info", "Second attempt after SetFinalResult failure");

        // Assert: Second event was persisted successfully
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var secondEvt = verifyDb.TaskEvents.FirstOrDefault(e => e.SessionId == taskId && e.Payload == "Second attempt after SetFinalResult failure");
        Assert.IsNotNull(secondEvt, "Second event must be successfully persisted and not blocked by poisoned DbContext.");
    }

    [TestMethod]
    public async Task ApprovePlanAsync_WhenDbUpdateExceptionOccurs_ClearsChangeTrackerToPreventDbContextPoisoning()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Valid task",
            State = TaskState.InProgress
        });
        _db.ImplementationPlans.Add(new ImplementationPlan
        {
            Id = Guid.NewGuid(),
            SessionId = taskId,
            Version = 1,
            PlanMarkdown = "Test plan",
            Status = PlanStatus.AwaitingReview,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Seed a pre-existing event with fixed Id to induce duplicate PK constraint failure
        var duplicateId = Guid.NewGuid();
        _db.TaskEvents.Add(new TaskEvent
        {
            Id = duplicateId,
            SessionId = taskId,
            EventType = "Initial",
            Level = "System",
            Payload = "First",
            Timestamp = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Manually stage a duplicate PK entity to force DbUpdateException inside next store operation
        _db.TaskEvents.Add(new TaskEvent
        {
            Id = duplicateId, // Duplicate PK in DB
            SessionId = taskId,
            EventType = "Conflict",
            Level = "System",
            Payload = "Conflict",
            Timestamp = DateTime.UtcNow
        });

        // Act 1: Call ApprovePlanAsync - this will trigger DbUpdateException and enter catch (DbUpdateException)
        await _taskStore.ApprovePlanAsync(taskId, 1);

        // Act 2: Subsequent store operation for valid task must succeed and NOT be poisoned by previous failed entity
        await _taskStore.LogEventAsync(taskId, "Event2", "Info", "Second attempt after ApprovePlan failure");

        // Assert: Second event was persisted successfully
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var secondEvt = verifyDb.TaskEvents.FirstOrDefault(e => e.SessionId == taskId && e.Payload == "Second attempt after ApprovePlan failure");
        Assert.IsNotNull(secondEvt, "Second event must be successfully persisted and not blocked by poisoned DbContext.");
    }

    [TestMethod]
    public async Task DeleteTaskWithCleanupAsync_WhenSessionAlreadyNull_PersistsRelatedRecordDeletions()
    {
        // Arrange - Seed related child records (events, chat, plans) without a TaskSession entity (orphaned data)
        var orphanedTaskId = Guid.NewGuid();

        // Temporarily disable FK checks on an explicitly opened connection to seed orphaned child records
        await _db.Database.OpenConnectionAsync();
        try
        {
            await _db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");

            _db.TaskEvents.Add(new TaskEvent
            {
                Id = Guid.NewGuid(),
                SessionId = orphanedTaskId,
                EventType = "TestEvent",
                Level = "Info",
                Payload = "Orphaned event",
                Timestamp = DateTime.UtcNow
            });

            _db.ChatMessages.Add(new ChatMessageSnapshot
            {
                Id = Guid.NewGuid(),
                SessionId = orphanedTaskId,
                SequenceNumber = 1,
                Role = "user",
                Content = "Orphaned message",
                Timestamp = DateTime.UtcNow
            });

            _db.ImplementationPlans.Add(new ImplementationPlan
            {
                Id = Guid.NewGuid(),
                SessionId = orphanedTaskId,
                Version = 1,
                PlanMarkdown = "Orphaned plan",
                CreatedAt = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
            await _db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
        _db.ChangeTracker.Clear();

        // Act - Invoke DeleteTaskWithCleanupAsync when TaskSession is null in DB
        await _taskStore.DeleteTaskWithCleanupAsync(orphanedTaskId, cleanupWorkspace: false);

        // Assert - All related records must be deleted from DB even if TaskSession was null
        using var verifyDb = _dbFixture.CreateAppDbContext();
        var remainingEvents = verifyDb.TaskEvents.Where(e => e.SessionId == orphanedTaskId).ToList();
        var remainingChat = verifyDb.ChatMessages.Where(m => m.SessionId == orphanedTaskId).ToList();
        var remainingPlans = verifyDb.ImplementationPlans.Where(p => p.SessionId == orphanedTaskId).ToList();

        Assert.AreEqual(0, remainingEvents.Count, "TaskEvents must be deleted even if TaskSession was null.");
        Assert.AreEqual(0, remainingChat.Count, "ChatMessages must be deleted even if TaskSession was null.");
        Assert.AreEqual(0, remainingPlans.Count, "ImplementationPlans must be deleted even if TaskSession was null.");
    }

    [TestMethod]
    public async Task DeleteTaskWithCleanupAsync_DeletesSessionAndAllRelatedRecords()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Delete test",
            State = TaskState.Completed
        });
        _db.TaskEvents.Add(new TaskEvent { Id = Guid.NewGuid(), SessionId = taskId, EventType = "TestEvent", Payload = "payload" });
        _db.ChatMessages.Add(new ChatMessageSnapshot { Id = Guid.NewGuid(), SessionId = taskId, SequenceNumber = 1, Role = "user", Content = "Hi" });
        _db.ImplementationPlans.Add(new ImplementationPlan { Id = Guid.NewGuid(), SessionId = taskId, Version = 1, PlanMarkdown = "# Plan" });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Act
        await _taskStore.DeleteTaskWithCleanupAsync(taskId, cleanupWorkspace: false);

        // Assert
        using var verifyDb = _dbFixture.CreateAppDbContext();
        Assert.IsNull(await verifyDb.TaskSessions.FindAsync(taskId));
        Assert.AreEqual(0, await verifyDb.TaskEvents.CountAsync(e => e.SessionId == taskId));
        Assert.AreEqual(0, await verifyDb.ChatMessages.CountAsync(m => m.SessionId == taskId));
        Assert.AreEqual(0, await verifyDb.ImplementationPlans.CountAsync(p => p.SessionId == taskId));
    }
}

