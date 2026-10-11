using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Net;

namespace SemanticAgentBroker.UnitTests.Api.Hubs;

[TestClass]
public class SignalRIntegrationTests
{
    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new CustomWebApplicationFactory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    [TestMethod]
    public async Task ConnectAsync_WithValidToken_SuccessfullyConnects()
    {
        // Arrange
        await using var connection = await _factory.CreateSignalRConnectionAsync("testadmin", "Admin", "browser-valid");

        // Act
        await connection.StartAsync();

        // Assert
        Assert.AreEqual(HubConnectionState.Connected, connection.State);
    }

    [TestMethod]
    public async Task ConnectAsync_WithoutToken_FailsHandshakeWithUnauthorized()
    {
        // Arrange - connection without access token
        var unauthenticatedConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/loghub?browserId=unauth-browser"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Headers["X-Browser-Id"] = "unauth-browser";
            })
            .Build();

        await using (unauthenticatedConnection)
        {
            // Act & Assert
            try
            {
                await unauthenticatedConnection.StartAsync();
                Assert.Fail("Connection without token should fail authentication.");
            }
            catch (Exception ex)
            {
                // Expected HTTP 401 Unauthorized during negotiation or connection
                Assert.IsTrue(ex.Message.Contains("401") ||
                              ex.Message.Contains("Unauthorized") ||
                              (ex is HttpRequestException hre && hre.StatusCode == HttpStatusCode.Unauthorized));
            }
        }
    }

    [TestMethod]
    public async Task ConnectAsync_WithMismatchedBrowserId_FailsHandshake()
    {
        // Arrange - login with browser-1, then attempt SignalR connection with browser-2
        var token = await _factory.GetAuthTokenAsync("testuser2", "User", "browser-1");

        var mismatchedConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/loghub?browserId=browser-2"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Headers["X-Browser-Id"] = "browser-2";
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        await using (mismatchedConnection)
        {
            // Act & Assert
            try
            {
                await mismatchedConnection.StartAsync();
                Assert.Fail("Connection with mismatched browser ID should fail.");
            }
            catch (Exception ex)
            {
                Assert.IsTrue(ex.Message.Contains("401") ||
                              ex.Message.Contains("Unauthorized") ||
                              (ex is HttpRequestException hre && hre.StatusCode == HttpStatusCode.Unauthorized));
            }
        }
    }

    [TestMethod]
    public async Task SubscribeToTask_And_BroadcastEvents_ClientReceivesRealTimeMessages()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Integration broadcast test",
                State = TaskState.InProgress
            });
            await db.SaveChangesAsync();
        }

        await using var connection = await _factory.CreateSignalRConnectionAsync("broadcaster", "Admin", "browser-broadcast");
        await connection.StartAsync();

        var logReceivedTcs = new TaskCompletionSource<AgentTaskLog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stateChangedTcs = new TaskCompletionSource<TaskStateChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolExecutionTcs = new TaskCompletionSource<ToolExecutionEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<AgentTaskLog>(HubMethodNames.ReceiveLog, log => logReceivedTcs.TrySetResult(log));
        connection.On<TaskStateChangedEvent>(HubMethodNames.TaskStateChanged, evt => stateChangedTcs.TrySetResult(evt));
        connection.On<ToolExecutionEvent>(HubMethodNames.ToolExecution, evt => toolExecutionTcs.TrySetResult(evt));

        // Act 1: Subscribe to task
        await connection.InvokeAsync(HubMethodNames.SubscribeToTask, taskId);

        // Act 2: Trigger broadcasts through AgentTaskStore
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<AgentTaskStore>();
            await store.LogAsync(taskId, "Information", "Hello from real SignalR test!");
            await store.UpdateStateAsync(taskId, TaskState.Completed, "Task completed successfully");
            await store.BroadcastToolExecutionAsync(taskId, "FileIO-SaveToWorkspace", "{}", "Success", false, "Orchestrator");
        }

        // Assert: Client received all events
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var log = await logReceivedTcs.Task.WaitAsync(cts.Token);
        Assert.AreEqual(taskId, log.TaskId);
        StringAssert.Contains(log.Message, "Hello from real SignalR test!");

        var stateEvt = await stateChangedTcs.Task.WaitAsync(cts.Token);
        Assert.AreEqual(taskId, stateEvt.TaskId);
        Assert.AreEqual(TaskState.Completed, stateEvt.NewState);

        var toolEvt = await toolExecutionTcs.Task.WaitAsync(cts.Token);
        Assert.AreEqual(taskId, toolEvt.TaskId);
        Assert.AreEqual("FileIO-SaveToWorkspace", toolEvt.ToolName);
    }

    [TestMethod]
    public async Task TaskStateChanged_WhenNotSubscribedToTaskGroup_IsReceivedByConnectedClient()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Public broadcast test",
                State = TaskState.Draft,
                IsPrivate = false
            });
            await db.SaveChangesAsync();
        }

        await using var connection = await _factory.CreateSignalRConnectionAsync("dashboard-watcher", "Admin", "browser-watcher");
        await connection.StartAsync();

        var stateChangedTcs = new TaskCompletionSource<TaskStateChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<TaskStateChangedEvent>(HubMethodNames.TaskStateChanged, evt => stateChangedTcs.TrySetResult(evt));

        // Act: Do NOT subscribe to task group (simulating Home.razor or TaskQueue.razor)
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<AgentTaskStore>();
            await store.UpdateStateAsync(taskId, TaskState.InProgress, "Running public task");
        }

        // Assert: Client still receives TaskStateChangedEvent
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stateEvt = await stateChangedTcs.Task.WaitAsync(cts.Token);
        Assert.AreEqual(taskId, stateEvt.TaskId);
        Assert.AreEqual(TaskState.InProgress, stateEvt.NewState);
    }

    [TestMethod]
    public async Task SendInterruption_FromConnectedClient_DeliveredToTaskOperationsService()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Interruption test",
                State = TaskState.InProgress
            });
            await db.SaveChangesAsync();
        }

        var taskOps = _factory.Services.GetRequiredService<TaskOperationsService>();
        taskOps.RegisterTask(taskId);

        try
        {
            await using var connection = await _factory.CreateSignalRConnectionAsync("interrupter", "Admin", "browser-interrupter");
            await connection.StartAsync();

            // Act
            bool delivered = await connection.InvokeAsync<bool>(HubMethodNames.SendInterruption, taskId, "Please adjust the output directory.");

            // Assert
            Assert.IsTrue(delivered);
            var interruption = taskOps.TryReadInterruption(taskId);
            Assert.IsNotNull(interruption);
            Assert.AreEqual("Please adjust the output directory.", interruption.Message);
            Assert.AreEqual("interrupter", interruption.UserName);
        }
        finally
        {
            taskOps.UnregisterTask(taskId);
        }
    }

    [TestMethod]
    public async Task ApproveStep_And_RejectStep_FromConnectedClient_ResolvesApprovalGates()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Step approval test",
                State = TaskState.AwaitingApproval
            });
            await db.SaveChangesAsync();
        }

        var taskOps = _factory.Services.GetRequiredService<TaskOperationsService>();
        taskOps.RegisterTask(taskId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await using var connection = await _factory.CreateSignalRConnectionAsync("stepapprover", "Admin", "browser-stepapprover");
            await connection.StartAsync(cts.Token);

            // Act 1: Approve Step
            var approvalWaitTask = taskOps.WaitForApprovalAsync(taskId, cts.Token);
            await connection.InvokeAsync(HubMethodNames.ApproveStep, taskId, cts.Token);
            bool approved = await approvalWaitTask;

            Assert.IsTrue(approved);

            // Act 2: Reject Step with feedback
            var rejectionWaitTask = taskOps.WaitForApprovalAsync(taskId, cts.Token);
            await connection.InvokeAsync(HubMethodNames.RejectStep, taskId, "Do not delete that folder", cts.Token);
            bool rejectedApproved = await rejectionWaitTask;

            Assert.IsFalse(rejectedApproved);
            var feedback = taskOps.TryReadStepRejectionFeedback(taskId);
            Assert.IsNotNull(feedback);
            Assert.AreEqual("Do not delete that folder", feedback.Value.Feedback);
        }
        finally
        {
            taskOps.UnregisterTask(taskId);
        }
    }

    [TestMethod]
    public async Task ApprovePlan_And_ModifyPlan_FromConnectedClient_ResolvesPlanDecisionGate()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Plan decision test",
                State = TaskState.PlanReview
            });
            await db.SaveChangesAsync();
        }

        var taskOps = _factory.Services.GetRequiredService<TaskOperationsService>();
        taskOps.RegisterTask(taskId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await using var connection = await _factory.CreateSignalRConnectionAsync("planreviewer", "Admin", "browser-planreviewer");
            await connection.StartAsync(cts.Token);

            // Act 1: Approve Plan
            var planWaitTask1 = taskOps.WaitForPlanDecisionAsync(taskId, cts.Token);
            await connection.InvokeAsync(HubMethodNames.ApprovePlan, taskId, cts.Token);
            var decision1 = await planWaitTask1;

            Assert.AreEqual(PlanAction.Approve, decision1.Action);

            // Act 2: Modify Plan
            var planWaitTask2 = taskOps.WaitForPlanDecisionAsync(taskId, cts.Token);
            await connection.InvokeAsync(HubMethodNames.ModifyPlan, taskId, "# Revised Plan\n1. New Step", cts.Token);
            var decision2 = await planWaitTask2;

            Assert.AreEqual(PlanAction.Modify, decision2.Action);
            Assert.AreEqual("# Revised Plan\n1. New Step", decision2.ModifiedPlan);

            // Act 3: Reject Plan with feedback
            var planWaitTask3 = taskOps.WaitForPlanDecisionAsync(taskId, cts.Token);
            await connection.InvokeAsync(HubMethodNames.RejectPlan, taskId, "Include unit tests in step 2", cts.Token);
            var decision3 = await planWaitTask3;

            Assert.AreEqual(PlanAction.Reject, decision3.Action);
            Assert.AreEqual("Include unit tests in step 2", decision3.Feedback);
        }
        finally
        {
            taskOps.UnregisterTask(taskId);
        }
    }

    [TestMethod]
    public async Task AskPlanQuestion_FromConnectedClient_ResolvesPlanDecisionGateWithQuestion()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Plan question integration test",
                State = TaskState.PlanReview
            });
            await db.SaveChangesAsync();
        }

        var taskOps = _factory.Services.GetRequiredService<TaskOperationsService>();
        taskOps.RegisterTask(taskId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await using var connection = await _factory.CreateSignalRConnectionAsync("questionasker", "Admin", "browser-questionasker");
            await connection.StartAsync(cts.Token);

            // Act - Send AskPlanQuestion
            var questionWaitTask = taskOps.WaitForPlanDecisionAsync(taskId, cts.Token);
            await connection.InvokeAsync(HubMethodNames.AskPlanQuestion, taskId, "What is the expected latency?", cts.Token);
            var decision = await questionWaitTask;

            // Assert
            Assert.AreEqual(PlanAction.AskQuestion, decision.Action);
            Assert.AreEqual("What is the expected latency?", decision.Feedback);
        }
        finally
        {
            taskOps.UnregisterTask(taskId);
        }
    }
}
