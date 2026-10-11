using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.Tasks.Models;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Constants;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SemanticAgentBroker.UnitTests.Api.Tasks;

[TestClass]
public class TaskEndpointsCrudTests
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
    public async Task CreateTask_WithValidPayload_PersistsSessionAndReturnsOk()
    {
        // Arrange
        var request = new AgentTaskRequest
        {
            AgentName = "GeneralAssistant",
            Prompt = "Process incoming sales report",
            Mode = ExecutionMode.Planned,
            DebugMode = true
        };

        // Act
        var response = await _client.PostAsJsonAsync(ApiRoutes.TasksBase, request);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AgentTaskRequest>();
        Assert.IsNotNull(created);
        Assert.AreNotEqual(Guid.Empty, created.TaskId);

        // Verify persisted directly in test broker.db
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.TaskSessions.FindAsync(created.TaskId);
        Assert.IsNotNull(session);
        Assert.IsTrue(session.State is TaskState.Draft or TaskState.Queued or TaskState.InProgress or TaskState.Planning,
            $"Expected initial task state (Draft/Queued/InProgress/Planning), but got {session.State}");

        var initialChat = db.ChatMessages.FirstOrDefault(m => m.SessionId == created.TaskId && m.SequenceNumber == 0);
        Assert.IsNotNull(initialChat);
        Assert.AreEqual("user", initialChat.Role);
        Assert.AreEqual("Process incoming sales report", initialChat.Content);
    }

    [TestMethod]
    public async Task CreateTask_WithMissingPrompt_ReturnsBadRequest()
    {
        // Arrange
        var request = new AgentTaskRequest
        {
            AgentName = "GeneralAssistant",
            Prompt = ""
        };

        // Act
        var response = await _client.PostAsJsonAsync(ApiRoutes.TasksBase, request);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task GetTasks_ReturnsPaginatedList()
    {
        // Arrange - seed 2 tasks in test db
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = Guid.NewGuid(), AgentName = "GeneralAssistant", UserPrompt = "Prompt 1", State = TaskState.Draft });
            db.TaskSessions.Add(new TaskSession { Id = Guid.NewGuid(), AgentName = "GeneralAssistant", UserPrompt = "Prompt 2", State = TaskState.Draft });
            await db.SaveChangesAsync();
        }

        // Act
        var response = await _client.GetAsync($"{ApiRoutes.TasksBase}?page=1&pageSize=10");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        Assert.AreEqual(JsonValueKind.Object, root.ValueKind);
        Assert.IsTrue(root.GetProperty("totalCount").GetInt32() >= 2);
    }

    [TestMethod]
    public async Task GetTaskById_WithExistingTask_ReturnsSessionDetails()
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
                UserPrompt = "Target Prompt",
                State = TaskState.InProgress
            });
            await db.SaveChangesAsync();
        }

        // Act
        var response = await _client.GetAsync(ApiRoutes.TaskById.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString()));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        Assert.AreEqual(taskId.ToString(), doc.RootElement.GetProperty("id").GetString());
        Assert.AreEqual("Target Prompt", doc.RootElement.GetProperty("userPrompt").GetString());
    }

    [TestMethod]
    public async Task GetTaskById_WithNonExistentTask_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync(ApiRoutes.TaskById.Replace("{taskId:guid}", Guid.NewGuid().ToString()).Replace("{taskId}", Guid.NewGuid().ToString()));

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task DeleteTask_CancelsAndDeletesSessionAndCleansWorkspace()
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
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "test.txt"), "hello");

            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Task to delete",
                State = TaskState.Completed
            });
            await db.SaveChangesAsync();
        }

        // Act
        var response = await _client.DeleteAsync(ApiRoutes.TaskById.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString()));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.TaskSessions.FindAsync(taskId);
            Assert.IsNotNull(session);
            Assert.AreEqual(TaskState.Cancelled, session.State);
            Assert.IsFalse(Directory.Exists(workspacePath));
        }
    }

    [TestMethod]
    public async Task BulkDeleteTasks_AsAdmin_DeletesSessionsAndEvents()
    {
        // Arrange
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = id1, AgentName = "GeneralAssistant", UserPrompt = "P1", State = TaskState.Draft });
            db.TaskSessions.Add(new TaskSession { Id = id2, AgentName = "GeneralAssistant", UserPrompt = "P2", State = TaskState.Draft });
            db.TaskEvents.Add(new TaskEvent { Id = Guid.NewGuid(), SessionId = id1, EventType = "Log", Level = "Info", Payload = "E1" });
            await db.SaveChangesAsync();
        }

        // Act
        var response = await _client.PostAsJsonAsync(ApiRoutes.TasksBulkDelete, new BulkDeleteRequest([id1, id2]));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.IsNull(await db.TaskSessions.FindAsync(id1));
            Assert.IsNull(await db.TaskSessions.FindAsync(id2));
            Assert.IsFalse(await db.TaskEvents.AnyAsync(e => e.SessionId == id1));
        }
    }

    [TestMethod]
    public async Task BulkDeleteTasks_WhenCountExceeds500_ReturnsBadRequest()
    {
        // Arrange
        var taskIds = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList();

        // Act
        var response = await _client.PostAsJsonAsync(ApiRoutes.TasksBulkDelete, new BulkDeleteRequest(taskIds));

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "Cannot delete more than 500 tasks in a single batch request.");
    }

    [TestMethod]
    public async Task DeleteTask_WhenCleanupWorkspacesFalse_PreservesWorkspaceDirectory()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        string workspacePath;
        var sysOptions = _factory.Services.GetRequiredService<SemanticAgentBroker.Shared.Configuration.AgentSystemOptions>();
        bool originalCleanup = sysOptions.CleanupWorkspacesOnTaskDelete;
        sysOptions.CleanupWorkspacesOnTaskDelete = false;

        try
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
                Directory.CreateDirectory(workspacePath);
                await File.WriteAllTextAsync(Path.Combine(workspacePath, "preserved.txt"), "hello");

                db.TaskSessions.Add(new TaskSession
                {
                    Id = taskId,
                    AgentName = "GeneralAssistant",
                    UserPrompt = "Task to preserve",
                    State = TaskState.Completed
                });
                await db.SaveChangesAsync();
            }

            // Act
            var response = await _client.DeleteAsync(ApiRoutes.TaskById.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString()));

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(Directory.Exists(workspacePath), "Workspace directory must be preserved when CleanupWorkspacesOnTaskDelete is false.");
        }
        finally
        {
            sysOptions.CleanupWorkspacesOnTaskDelete = originalCleanup;
        }
    }

    [TestMethod]
    public async Task BulkDeleteTasks_WhenCleanupWorkspacesFalse_PreservesWorkspaceDirectories()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        string workspacePath;
        var sysOptions = _factory.Services.GetRequiredService<SemanticAgentBroker.Shared.Configuration.AgentSystemOptions>();
        bool originalCleanup = sysOptions.CleanupWorkspacesOnTaskDelete;
        sysOptions.CleanupWorkspacesOnTaskDelete = false;

        try
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
                Directory.CreateDirectory(workspacePath);
                await File.WriteAllTextAsync(Path.Combine(workspacePath, "bulk_preserved.txt"), "hello");

                db.TaskSessions.Add(new TaskSession
                {
                    Id = taskId,
                    AgentName = "GeneralAssistant",
                    UserPrompt = "Bulk preserve",
                    State = TaskState.Draft
                });
                await db.SaveChangesAsync();
            }

            // Act
            var response = await _client.PostAsJsonAsync(ApiRoutes.TasksBulkDelete, new BulkDeleteRequest([taskId]));

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(Directory.Exists(workspacePath), "Workspace directory must be preserved on bulk delete when CleanupWorkspacesOnTaskDelete is false.");
        }
        finally
        {
            sysOptions.CleanupWorkspacesOnTaskDelete = originalCleanup;
        }
    }

    [TestMethod]
    public async Task CreateTask_WithMultipleFiles_WhenSecondFileIsInvalid_DoesNotCreateWorkspaceDirectoryOrOrphanedFiles()
    {
        // Arrange
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("GeneralAssistant"), "agentName");
        form.Add(new StringContent("Test file staging"), "prompt");

        // 1st file: valid
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("valid content")), "files", "valid_file_test.txt");
        // 2nd file: invalid characters
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("evil content")), "files", "evil*invalid?.txt");

        // Act
        var response = await _client.PostAsync(ApiRoutes.TasksBase, form);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        // Verify valid_file_test.txt was NOT written to disk
        using var scope = _factory.Services.CreateScope();
        var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
        var parentWorkspaces = Path.GetFullPath(sysOptions.TaskWorkspacesPath);
        if (Directory.Exists(parentWorkspaces))
        {
            var testFiles = Directory.GetFiles(parentWorkspaces, "valid_file_test.txt", SearchOption.AllDirectories);
            Assert.AreEqual(0, testFiles.Length, "Invalid upload batch must not write any files before full validation.");
        }
    }

    [TestMethod]
    public async Task CreateTask_WhenDbInsertFails_CleansUpOrphanedWorkspaceFiles()
    {
        // Arrange
        var duplicateId = Guid.NewGuid();
        string workspacePath;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(duplicateId, sysOptions);

            // Pre-insert a task with duplicateId so SaveChangesAsync in endpoint throws a PK conflict
            db.TaskSessions.Add(new TaskSession
            {
                Id = duplicateId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Pre-existing task to cause PK conflict"
            });
            await db.SaveChangesAsync();
        }

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(duplicateId.ToString()), "taskId");
        form.Add(new StringContent("GeneralAssistant"), "agentName");
        form.Add(new StringContent("Test orphan cleanup"), "prompt");
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("test content")), "files", "uploaded_orphan_check.txt");

        // Act - this will fail on db.SaveChangesAsync due to PK conflict
        var response = await _client.PostAsync(ApiRoutes.TasksBase, form);

        // Assert - HTTP request should have failed
        Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);

        // Verify that workspace directory was cleaned up and no orphaned files remain
        Assert.IsFalse(Directory.Exists(workspacePath), "Workspace directory must be cleaned up if database transaction/insert fails.");
    }

    [TestMethod]
    public async Task WorkspaceUpload_WithMultipleFiles_WhenSecondFileIsInvalid_DoesNotWriteFirstFileToDisk()
    {
        // Arrange - Create a task first
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "Test task", State = TaskState.Draft });
            await db.SaveChangesAsync();
        }

        using var form = new MultipartFormDataContent();
        // 1st file: valid
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("valid upload content")), "files", "first_file_valid.txt");
        // 2nd file: invalid characters
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("invalid content")), "files", "evil*invalid?.txt");

        // Act
        var url = ApiRoutes.TaskWorkspaceUpload.Replace("{taskId}", taskId.ToString());
        var response = await _client.PostAsync(url, form);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        // Verify first_file_valid.txt was NOT written to disk
        using var verifyScope = _factory.Services.CreateScope();
        var sysOptions = verifyScope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
        var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
        if (Directory.Exists(workspacePath))
        {
            var testFiles = Directory.GetFiles(workspacePath, "first_file_valid.txt", SearchOption.AllDirectories);
            Assert.AreEqual(0, testFiles.Length, "Invalid workspace upload batch must not write any files before full validation.");
        }
    }

    [TestMethod]
    public async Task BulkDeleteTasks_CleansUpTaskStateManagerAndWorkspaceLockManagerSessions()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "Bulk delete lock test", State = TaskState.Draft });
            await db.SaveChangesAsync();

            var stateManager = scope.ServiceProvider.GetRequiredService<SemanticAgentBroker.Core.Features.TaskState.ITaskStateManager>();
            await stateManager.InitializeStateAsync(taskId, "Test Objective", CancellationToken.None);

            var lockManager = scope.ServiceProvider.GetRequiredService<SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager>();
            await lockManager.AcquireLockAsync(taskId, "test.txt", CancellationToken.None);
        }

        var stateLocksField = typeof(SemanticAgentBroker.Core.Features.TaskState.Implementations.TaskStateManager)
            .GetField("_sessionLocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var stateLocksDict = (System.Collections.IDictionary)stateLocksField.GetValue(null)!;
        Assert.IsTrue(stateLocksDict.Contains(taskId), "TaskStateManager should hold a lock entry before bulk delete.");

        var lockManagerInstance = _factory.Services.GetRequiredService<SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager>();
        var sessionsField = typeof(SemanticAgentBroker.Core.Features.Agents.Implementations.WorkspaceLockManager)
            .GetField("_sessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var sessionsDict = (System.Collections.IDictionary)sessionsField.GetValue(lockManagerInstance)!;
        Assert.IsTrue(sessionsDict.Contains(taskId), "WorkspaceLockManager should hold a session entry before bulk delete.");

        // Act
        var response = await _client.PostAsJsonAsync(ApiRoutes.TasksBulkDelete, new BulkDeleteRequest([taskId]));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(stateLocksDict.Contains(taskId), "TaskStateManager lock entry must be cleaned up on bulk delete.");
        Assert.IsFalse(sessionsDict.Contains(taskId), "WorkspaceLockManager session entry must be cleaned up on bulk delete.");
    }

    [TestMethod]
    public async Task BulkDeleteTasks_CancelsQueuedAndActiveTasksInQueueService()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "Bulk delete queued test", State = TaskState.Queued });
            await db.SaveChangesAsync();
        }

        var queue = _factory.Services.GetRequiredService<AgentTaskQueueService>();
        await queue.EnqueueTaskAsync(new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "GeneralAssistant",
            Prompt = "Bulk delete queued test"
        });

        // Act
        var response = await _client.PostAsJsonAsync(ApiRoutes.TasksBulkDelete, new BulkDeleteRequest([taskId]));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(queue.IsTaskQueued(taskId), "Queued task must be cancelled in AgentTaskQueueService during bulk delete.");
    }

    [TestMethod]
    public async Task DeleteTask_CleansUpTaskStateManagerAndWorkspaceLockManagerSessions()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "Single delete lock test", State = TaskState.Completed });
            await db.SaveChangesAsync();

            var stateManager = scope.ServiceProvider.GetRequiredService<SemanticAgentBroker.Core.Features.TaskState.ITaskStateManager>();
            await stateManager.InitializeStateAsync(taskId, "Test Objective", CancellationToken.None);

            var lockManager = scope.ServiceProvider.GetRequiredService<SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager>();
            await lockManager.AcquireLockAsync(taskId, "test.txt", CancellationToken.None);
        }

        var stateLocksField = typeof(SemanticAgentBroker.Core.Features.TaskState.Implementations.TaskStateManager)
            .GetField("_sessionLocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var stateLocksDict = (System.Collections.IDictionary)stateLocksField.GetValue(null)!;
        Assert.IsTrue(stateLocksDict.Contains(taskId), "TaskStateManager should hold a lock entry before delete.");

        var lockManagerInstance = _factory.Services.GetRequiredService<SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager>();
        var sessionsField = typeof(SemanticAgentBroker.Core.Features.Agents.Implementations.WorkspaceLockManager)
            .GetField("_sessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var sessionsDict = (System.Collections.IDictionary)sessionsField.GetValue(lockManagerInstance)!;
        Assert.IsTrue(sessionsDict.Contains(taskId), "WorkspaceLockManager should hold a session entry before delete.");

        // Act
        var response = await _client.DeleteAsync(ApiRoutes.TaskById.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString()));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(stateLocksDict.Contains(taskId), "TaskStateManager lock entry must be cleaned up on task delete.");
        Assert.IsFalse(sessionsDict.Contains(taskId), "WorkspaceLockManager session entry must be cleaned up on task delete.");
    }

    [TestMethod]
    public async Task GetTaskStats_ReturnsAccurateCountsAcrossAllPages()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.RemoveRange(db.TaskSessions);
            await db.SaveChangesAsync();

            var tasksToSeed = new List<TaskSession>();
            for (int i = 0; i < 120; i++)
            {
                tasksToSeed.Add(new TaskSession { AgentName = "TestAgent", UserPrompt = $"Completed {i}", State = TaskState.Completed });
            }
            for (int i = 0; i < 15; i++)
            {
                tasksToSeed.Add(new TaskSession { AgentName = "TestAgent", UserPrompt = $"Failed {i}", State = TaskState.Failed });
            }
            for (int i = 0; i < 10; i++)
            {
                tasksToSeed.Add(new TaskSession { AgentName = "TestAgent", UserPrompt = $"Active {i}", State = TaskState.InProgress });
            }
            db.TaskSessions.AddRange(tasksToSeed);
            await db.SaveChangesAsync();
        }

        // Act
        var response = await _client.GetAsync(ApiRoutes.TaskStats);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var stats = await response.Content.ReadFromJsonAsync<TaskStatsDto>();
        Assert.IsNotNull(stats);
        Assert.AreEqual(145, stats.TotalCount);
        Assert.AreEqual(120, stats.CompletedCount);
        Assert.AreEqual(15, stats.FailedCount);
        Assert.AreEqual(10, stats.ActiveCount);
    }
}
