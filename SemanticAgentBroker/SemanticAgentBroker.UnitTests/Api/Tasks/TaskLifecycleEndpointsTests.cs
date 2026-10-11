using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Shared.Constants;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Net;
using System.Net.Http.Json;

namespace SemanticAgentBroker.UnitTests.Api.Tasks;

[TestClass]
public class TaskLifecycleEndpointsTests
{
    private CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _client = await _factory.CreateAuthenticatedClientAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task PauseTask_UpdatesTaskStateToPaused()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Pause Test",
                State = TaskState.InProgress
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskPause.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.TaskSessions.FindAsync(taskId);
            Assert.IsNotNull(session);
            Assert.AreEqual(TaskState.Paused, session.State);
        }
    }

    [TestMethod]
    public async Task PauseTask_WhenTaskQueued_SetsPauseGateInTaskOperationsService()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Queued Pause Test",
                State = TaskState.Queued
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskPause.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var taskOps = _factory.Services.GetRequiredService<TaskOperationsService>();
        Assert.IsTrue(await taskOps.IsPausedAsync(taskId, CancellationToken.None), "Pause gate must be registered in TaskOperationsService even when task was only queued.");
    }

    [TestMethod]
    [DataRow(TaskState.Completed)]
    [DataRow(TaskState.Failed)]
    [DataRow(TaskState.Cancelled)]
    public async Task PauseTask_WhenTaskInTerminalState_ReturnsBadRequest(TaskState terminalState)
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Terminal Pause Test",
                State = terminalState
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskPause.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.TaskSessions.FindAsync(taskId);
            Assert.IsNotNull(session);
            Assert.AreEqual(terminalState, session.State, "Task state must not be overwritten to Paused when in terminal state.");
        }
    }

    [TestMethod]
    [DataRow(TaskState.AwaitingApproval)]
    [DataRow(TaskState.PlanReview)]
    [DataRow(TaskState.AwaitingInput)]
    public async Task PauseTask_WhenTaskAwaitingUserInteraction_ReturnsBadRequest(TaskState awaitingState)
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Awaiting Pause Test",
                State = awaitingState
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskPause.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.TaskSessions.FindAsync(taskId);
            Assert.IsNotNull(session);
            Assert.AreEqual(awaitingState, session.State, "Task state must not be overwritten to Paused when awaiting user decision.");
        }
    }

    [TestMethod]
    public async Task PauseTask_WhenTaskHasActiveApprovalGate_ReturnsBadRequest()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Gate Pause Test",
                State = TaskState.InProgress
            });
            await db.SaveChangesAsync();
        }
        var taskOps = _factory.Services.GetRequiredService<TaskOperationsService>();
        taskOps.RegisterTask(taskId);
        taskOps.PrepareApprovalGate(taskId);

        try
        {
            // Act
            var url = ApiRoutes.TaskPause.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
            var response = await _client.PostAsync(url, null);

            // Assert
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            taskOps.UnregisterTask(taskId);
        }
    }

    [TestMethod]
    [DataRow(TaskState.AwaitingApproval)]
    [DataRow(TaskState.PlanReview)]
    public async Task ResumeTask_WhenTaskWasAwaitingApprovalOrReview_PreservesAwaitingState(TaskState awaitingState)
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Resume Awaiting Test",
                State = awaitingState
            });
            await db.SaveChangesAsync();
        }
        var taskOps = _factory.Services.GetRequiredService<TaskOperationsService>();
        taskOps.RegisterTask(taskId);
        if (awaitingState == TaskState.AwaitingApproval)
            taskOps.PrepareApprovalGate(taskId);

        try
        {
            // Act
            var url = ApiRoutes.TaskResume.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
            var response = await _client.PostAsync(url, null);

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var session = await db.TaskSessions.FindAsync(taskId);
                Assert.IsNotNull(session);
                Assert.AreEqual(awaitingState, session.State, "ResumeTask must NOT overwrite awaiting approval or plan review state with InProgress.");
            }
        }
        finally
        {
            taskOps.UnregisterTask(taskId);
        }
    }

    [TestMethod]
    public async Task RestartTask_ResetsStateAndClearsOldEvents()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Restart Test",
                State = TaskState.Completed,
                FinalResult = "Old Result"
            });
            db.TaskEvents.Add(new TaskEvent
            {
                Id = Guid.NewGuid(),
                SessionId = taskId,
                EventType = "Log",
                Level = "Info",
                Payload = "Old log"
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskRestart.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.TaskSessions.FindAsync(taskId);
            Assert.IsNotNull(session);
            Assert.IsTrue(session.State is TaskState.Draft or TaskState.Queued or TaskState.InProgress or TaskState.Planning,
                $"Expected reset state but was {session.State}");
            Assert.IsNull(session.FinalResult);

            var oldEvents = db.TaskEvents.Where(e => e.SessionId == taskId).ToList();
            Assert.IsFalse(oldEvents.Any(e => e.Payload == "Old log"));
        }
    }

    [TestMethod]
    public async Task RestartTask_ReInsertsInitialUserPromptInChatHistory()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Initial user prompt before restart",
                State = TaskState.Completed,
                FinalResult = "Done"
            });
            db.ChatMessages.Add(new ChatMessageSnapshot
            {
                SessionId = taskId,
                SequenceNumber = 0,
                Role = "user",
                Content = "Initial user prompt before restart",
                Timestamp = DateTime.UtcNow.AddMinutes(-5)
            });
            db.ChatMessages.Add(new ChatMessageSnapshot
            {
                SessionId = taskId,
                SequenceNumber = 1,
                Role = "assistant",
                Content = "Previous agent thought",
                Timestamp = DateTime.UtcNow.AddMinutes(-4)
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskRestart.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var messages = db.ChatMessages.Where(m => m.SessionId == taskId).OrderBy(m => m.SequenceNumber).ToList();
            Assert.AreEqual(1, messages.Count, "Restarted task should contain exactly 1 message (the initial user prompt).");
            Assert.AreEqual(0, messages[0].SequenceNumber);
            Assert.AreEqual("user", messages[0].Role);
            Assert.AreEqual("Initial user prompt before restart", messages[0].Content);
        }
    }

    [TestMethod]
    public async Task TaskRestart_WithExistingWorkspaceDirectory_CleansWorkspaceFiles()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        string workspacePath;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sysOptions = scope.ServiceProvider.GetRequiredService<SemanticAgentBroker.Shared.Configuration.AgentSystemOptions>();
            workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            Directory.CreateDirectory(workspacePath);
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "_task_state.json"), "{\"Objective\":\"Old Objective\"}");
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "stale_output.txt"), "old output");

            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Restart workspace test",
                State = TaskState.Failed,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.IsTrue(Directory.Exists(workspacePath));

        // Act
        var url = ApiRoutes.TaskRestart.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(File.Exists(Path.Combine(workspacePath, "stale_output.txt")), "Stale files from previous run must be deleted on restart.");
        if (File.Exists(Path.Combine(workspacePath, "_task_state.json")))
        {
            var content = await File.ReadAllTextAsync(Path.Combine(workspacePath, "_task_state.json"));
            StringAssert.DoesNotMatch(content, new System.Text.RegularExpressions.Regex("Old Objective"));
        }
    }

    [TestMethod]
    public async Task TaskRestart_WhenTaskPausedAndActive_CancelsAndCompletesPreviousActiveTask()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var taskOps = _factory.Services.GetRequiredService<TaskOperationsService>();
        var taskCt = taskOps.RegisterTask(taskId);
        taskCt.Register(() => taskOps.UnregisterTask(taskId));
        taskOps.PauseTask(taskId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Restart test",
                State = TaskState.Paused,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.IsTrue(taskOps.IsTaskActive(taskId));

        // Act
        var url = ApiRoutes.TaskRestart.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(taskCt.IsCancellationRequested, "Active paused task must be cancelled on restart.");
    }

    [TestMethod]
    public async Task TaskProceed_WhenTaskPausedAndActive_CancelsAndCompletesPreviousActiveTask()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var taskOps = _factory.Services.GetRequiredService<TaskOperationsService>();
        var taskCt = taskOps.RegisterTask(taskId);
        taskCt.Register(() => taskOps.UnregisterTask(taskId));
        taskOps.PauseTask(taskId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Proceed test",
                State = TaskState.Paused,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.IsTrue(taskOps.IsTaskActive(taskId));

        // Act
        var url = ApiRoutes.TaskProceed.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsJsonAsync(url, "Follow-up question");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(taskCt.IsCancellationRequested, "Active paused task must be cancelled on proceed.");
    }

    [TestMethod]
    public async Task PlanApprove_SignalsApprovalSuccessfully()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Plan Test",
                State = TaskState.PlanReview
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskPlanApprove.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task SendInterruption_InactiveTask_ReturnsBadRequest()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Interruption Test",
                State = TaskState.Completed
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskSendInterruption.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsJsonAsync(url, "Hello Agent");

        // Assert - Task is not active, should return BadRequest
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task ProceedTask_AppendsFollowUpUserPromptToChatMessages_AndResetsStateToDraft()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Initial job",
                State = TaskState.Completed,
                FinalResult = "Initial result"
            });
            db.ChatMessages.Add(new ChatMessageSnapshot
            {
                SessionId = taskId,
                SequenceNumber = 0,
                Role = "user",
                Content = "Initial job",
                Timestamp = DateTime.UtcNow.AddMinutes(-5)
            });
            db.ChatMessages.Add(new ChatMessageSnapshot
            {
                SessionId = taskId,
                SequenceNumber = 1,
                Role = "assistant",
                Content = "FinalAnswer: Initial result",
                Timestamp = DateTime.UtcNow.AddMinutes(-4)
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskProceed.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsJsonAsync(url, "Follow-up job instruction");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.TaskSessions.FindAsync(taskId);
            Assert.IsNotNull(session);
            Assert.IsTrue(session.State is TaskState.Draft or TaskState.Queued or TaskState.InProgress or TaskState.Planning,
                $"Expected reset state but was {session.State}");
            Assert.IsNull(session.FinalResult);

            var messages = db.ChatMessages.Where(m => m.SessionId == taskId).OrderBy(m => m.SequenceNumber).ToList();
            Assert.AreEqual(3, messages.Count);
            Assert.AreEqual("Initial job", messages[0].Content);
            Assert.AreEqual("FinalAnswer: Initial result", messages[1].Content);
            Assert.AreEqual("Follow-up job instruction", messages[2].Content);
            Assert.AreEqual("user", messages[2].Role);
            Assert.AreEqual(2, messages[2].SequenceNumber);
        }
    }

    [TestMethod]
    public async Task PlanModify_SignalsPlanModificationSuccessfully()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Plan Test",
                State = TaskState.PlanReview
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskPlanModify.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsJsonAsync(url, "## Modified Plan\n1. Custom step");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task PlanReject_SignalsPlanRejectionSuccessfully()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Plan Test",
                State = TaskState.PlanReview
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskPlanReject.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsJsonAsync(url, "Please add more detailed steps");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task PlanQuestion_SignalsQuestionSuccessfully()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Plan Test",
                State = TaskState.PlanReview
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskPlanQuestion.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsJsonAsync(url, "Which database engine should be used?");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task ApproveStep_SignalsStepApprovalSuccessfully()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Approval Test",
                State = TaskState.AwaitingApproval
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskApproveStep.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task RejectStep_SignalsStepRejectionSuccessfully()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Approval Test",
                State = TaskState.AwaitingApproval
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskRejectStep.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsJsonAsync(url, "Action denied due to security policy");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task ResumeTask_WhenTaskIsOrphaned_ReEnqueuesAsContinuation()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Orphaned Task",
                State = TaskState.Paused
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskResume.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.TaskSessions.FindAsync(taskId);
            Assert.IsNotNull(session);
            Assert.IsTrue(session.State is TaskState.Draft or TaskState.Queued or TaskState.InProgress or TaskState.Planning);
        }
    }

    [TestMethod]
    [DataRow(TaskState.Completed)]
    [DataRow(TaskState.Failed)]
    [DataRow(TaskState.Cancelled)]
    public async Task ResumeTask_WhenTaskIsInTerminalState_ReturnsBadRequest(TaskState terminalState)
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Terminal Task",
                State = terminalState
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskResume.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task TaskProceed_WhenAuthenticated_SetsUserNameOnEnqueuedRequest()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Initial prompt",
                State = TaskState.Completed,
                FinalResult = "Initial result"
            });
            await db.SaveChangesAsync();
        }

        // Act
        using var userClient = await _factory.CreateAuthenticatedClientAsync("custom-user", "Admin", "user-browser");
        var url = ApiRoutes.TaskProceed.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await userClient.PostAsJsonAsync(url, "Continue with follow-up");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var req = await response.Content.ReadFromJsonAsync<AgentTaskRequest>();
        Assert.IsNotNull(req);
        Assert.AreEqual(taskId, req.TaskId);
        Assert.AreEqual("custom-user", req.UserName);
        Assert.IsTrue(req.IsContinuation);
    }

    [TestMethod]
    public async Task TaskRestart_WhenActiveLoopDoesNotStop_ReturnsConflict()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Stuck Task",
                State = TaskState.Paused
            });
            await db.SaveChangesAsync();
        }

        var taskOps = _factory.Services.GetRequiredService<TaskOperationsService>();
        taskOps.RegisterTask(taskId);

        try
        {
            // Act
            var url = ApiRoutes.TaskRestart.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
            var response = await _client.PostAsync(url, null);

            // Assert
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            taskOps.UnregisterTask(taskId);
        }
    }

    [TestMethod]
    public async Task TaskProceed_WhenActiveLoopDoesNotStop_ReturnsConflict()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Stuck Task",
                State = TaskState.Paused
            });
            await db.SaveChangesAsync();
        }

        var taskOps = _factory.Services.GetRequiredService<TaskOperationsService>();
        taskOps.RegisterTask(taskId);

        try
        {
            // Act
            var url = ApiRoutes.TaskProceed.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
            var response = await _client.PostAsJsonAsync(url, "Proceed prompt");

            // Assert
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            taskOps.UnregisterTask(taskId);
        }
    }

    [TestMethod]
    public async Task ResumeTask_WhenTaskPausedWhileQueued_ClearsPauseGateAndSetsStateToQueued()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Queued Task To Resume",
                State = TaskState.Queued
            });
            await db.SaveChangesAsync();
        }

        // Pause the task while it is in the queue (registers pause gate, sets state to Paused)
        var pauseUrl = ApiRoutes.TaskPause.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var pauseResponse = await _client.PostAsync(pauseUrl, null);
        Assert.AreEqual(HttpStatusCode.OK, pauseResponse.StatusCode);

        var taskOps = _factory.Services.GetRequiredService<TaskOperationsService>();
        Assert.IsTrue(await taskOps.IsPausedAsync(taskId, CancellationToken.None), "Pause gate should be registered.");

        // Act: Resume the task while it is still queued (not yet active in RunTaskAsync)
        var resumeUrl = ApiRoutes.TaskResume.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var resumeResponse = await _client.PostAsync(resumeUrl, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, resumeResponse.StatusCode);
        // The pause gate MUST be cleared so the worker loop does not hang indefinitely
        Assert.IsFalse(await taskOps.IsPausedAsync(taskId, CancellationToken.None), "Pause gate must be removed and resolved upon resume.");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.TaskSessions.FindAsync(taskId);
            Assert.IsNotNull(session);
            Assert.AreNotEqual(TaskState.Draft, session.State, "Task should not be re-enqueued as a draft orphan continuation.");
            Assert.IsTrue(session.State is TaskState.Queued or TaskState.InProgress);
        }
    }
}
