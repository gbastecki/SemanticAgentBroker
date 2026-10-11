using SemanticAgentBroker.Api.Features.Agents.Models;
using SemanticAgentBroker.Api.Features.Tasks.Models;
using SemanticAgentBroker.Client.Components;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.UnitTests.Shared;

[TestClass]
public class DomainContractsTests
{
    [TestMethod]
    public void SignalRContracts_Records_InitializeWithExpectedPropertiesAndDefaults()
    {
        var taskId = Guid.NewGuid();

        // 1. TaskStateChangedEvent
        var stateEvent = new TaskStateChangedEvent(taskId, TaskState.InProgress, "Running");
        Assert.AreEqual(taskId, stateEvent.TaskId);
        Assert.AreEqual(TaskState.InProgress, stateEvent.NewState);
        Assert.AreEqual("Running", stateEvent.Detail);

        // 2. AgentThoughtEvent with defaults
        var thoughtEvent = new AgentThoughtEvent(taskId, "Reasoning step");
        Assert.AreEqual(taskId, thoughtEvent.TaskId);
        Assert.AreEqual("Reasoning step", thoughtEvent.Thought);
        Assert.AreEqual("Agent", thoughtEvent.AgentName);
        Assert.IsNull(thoughtEvent.Observation);

        // 3. PlanGeneratedEvent
        var planGen = new PlanGeneratedEvent(taskId, "# Plan", true);
        Assert.AreEqual(taskId, planGen.TaskId);
        Assert.AreEqual("# Plan", planGen.PlanMarkdown);
        Assert.IsTrue(planGen.RequiresApproval);

        // 4. ToolExecutionEvent with defaults
        var toolExec = new ToolExecutionEvent(taskId, "FileIO-ReadFile", "{\"path\":\"a.txt\"}", "ok", false);
        Assert.AreEqual(taskId, toolExec.TaskId);
        Assert.AreEqual("FileIO-ReadFile", toolExec.ToolName);
        Assert.AreEqual("Orchestrator", toolExec.AgentName);

        // 5. AgentActivityEvent
        var activity = new AgentActivityEvent(taskId, "Compacting context");
        Assert.AreEqual(taskId, activity.TaskId);
        Assert.AreEqual("Compacting context", activity.ActivityLabel);
        Assert.AreEqual("Orchestrator", activity.AgentName);

        // 6. PlanReviewRequestedEvent
        var reviewReq = new PlanReviewRequestedEvent(taskId, "# Plan v1", "Q1?", 1);
        Assert.AreEqual(taskId, reviewReq.TaskId);
        Assert.AreEqual("Q1?", reviewReq.OpenQuestions);
        Assert.AreEqual(1, reviewReq.Version);

        // 7. PlanRevisionEvent
        var planRev = new PlanRevisionEvent(taskId, "# Plan v2", 2);
        Assert.AreEqual(2, planRev.Version);

        // 8. ContextCompactionEvent
        var compEvent = new ContextCompactionEvent(taskId, 20, 8);
        Assert.AreEqual(20, compEvent.OriginalMessageCount);
        Assert.AreEqual(8, compEvent.CompactedMessageCount);

        // 9. WorkspaceFileChangedEvent
        var fileChange = new WorkspaceFileChangedEvent(taskId, "out.csv", "Created");
        Assert.AreEqual("out.csv", fileChange.FileName);
        Assert.AreEqual("Created", fileChange.ChangeType);

        // 10. TokenCountUpdatedEvent with default scope
        var tokenEvent = new TokenCountUpdatedEvent(taskId, 4000, 16384);
        Assert.AreEqual(4000, tokenEvent.TokenCount);
        Assert.AreEqual(16384, tokenEvent.TokenLimit);
        Assert.AreEqual("Subagent", tokenEvent.Scope);
    }

    [TestMethod]
    public void TaskEntities_DefaultProperties_InitializeProperly()
    {
        // TaskSession
        var session = new TaskSession();
        Assert.AreNotEqual(Guid.Empty, session.Id);
        Assert.AreEqual(string.Empty, session.AgentName);
        Assert.AreEqual(string.Empty, session.UserPrompt);
        Assert.AreEqual(TaskState.Draft, session.State);
        Assert.AreEqual(ExecutionMode.Auto, session.Mode);
        Assert.IsFalse(session.DebugMode);
        Assert.IsFalse(session.TraceMode);
        Assert.IsFalse(session.IsPrivate);
        Assert.IsNotNull(session.AllowedOptions);
        Assert.IsNotNull(session.Events);
        Assert.IsNotNull(session.ChatHistory);
        Assert.IsNotNull(session.Plans);

        // TaskEvent
        var evt = new TaskEvent();
        Assert.AreNotEqual(Guid.Empty, evt.Id);
        Assert.AreEqual("System", evt.Level);
        Assert.AreEqual("Orchestrator", evt.AgentName);

        // ChatMessageSnapshot
        var msg = new ChatMessageSnapshot();
        Assert.AreNotEqual(Guid.Empty, msg.Id);
        Assert.AreEqual(0, msg.SequenceNumber);

        // ImplementationPlan
        var plan = new ImplementationPlan();
        Assert.AreNotEqual(Guid.Empty, plan.Id);
        Assert.AreEqual(1, plan.Version);
        Assert.AreEqual(PlanStatus.Draft, plan.Status);

        // AgentTaskLog
        var log = new AgentTaskLog();
        Assert.AreNotEqual(Guid.Empty, log.Id);
        Assert.AreEqual("Info", log.LogLevel);
    }

    [TestMethod]
    public void AgentAndTaskDtos_Properties_InitializeAndBindCorrectly()
    {
        // CreateAgentRequest
        var createReq = new CreateAgentRequest
        {
            Name = "CustomAgent",
            Description = "Custom Agent Desc",
            Icon = "smart_toy",
            AccentClass = "accent-blue",
            SystemInstruction = "You are helpful.",
            Methods = [new MethodBindingDto { PluginId = "Math", MethodId = "Add" }]
        };
        Assert.AreEqual("CustomAgent", createReq.Name);
        Assert.AreEqual(1, createReq.Methods.Count);
        Assert.AreEqual("Math", createReq.Methods[0].PluginId);
        Assert.AreEqual("Add", createReq.Methods[0].MethodId);

        // UpdateAgentRequest
        var updateReq = new UpdateAgentRequest
        {
            Description = "Updated Desc",
            IsEnabled = true
        };
        Assert.AreEqual("Updated Desc", updateReq.Description);
        Assert.IsTrue(updateReq.IsEnabled);

        // BulkDeleteRequest
        var bulkReq = new BulkDeleteRequest([Guid.NewGuid(), Guid.NewGuid()]);
        Assert.AreEqual(2, bulkReq.TaskIds.Count);

        // WorkspaceFileDto
        var now = DateTime.UtcNow;
        var fileDto = new WorkspaceFileDto("report.txt", 1024, now, true);
        Assert.AreEqual("report.txt", fileDto.Name);
        Assert.AreEqual(1024, fileDto.SizeBytes);
        Assert.AreEqual(now, fileDto.LastModified);
        Assert.IsTrue(fileDto.IsTextFile);
    }

    [TestMethod]
    public void SelectOptionItem_GenericValue_StoresAndRetrievesValueAndText()
    {
        // Value type
        var intOption = new SelectOptionItem<int>(42, "Answer");
        Assert.AreEqual(42, intOption.Value);
        Assert.AreEqual("Answer", intOption.Text);

        // Reference type
        var stringOption = new SelectOptionItem<string>("opt_1", "Option One");
        Assert.AreEqual("opt_1", stringOption.Value);
        Assert.AreEqual("Option One", stringOption.Text);

        // Nullable type with null value
        var nullOption = new SelectOptionItem<string?>(null, "-- None --");
        Assert.IsNull(nullOption.Value);
        Assert.AreEqual("-- None --", nullOption.Text);
    }

    [TestMethod]
    [DataRow("Dockerfile", true)]
    [DataRow("Dockerfile.dev", true)]
    [DataRow("Makefile", true)]
    [DataRow("LICENSE", true)]
    [DataRow("README", true)]
    [DataRow(".gitignore", true)]
    [DataRow(".env", true)]
    [DataRow("script.py", true)]
    [DataRow("code.cs", true)]
    [DataRow("doc.md", true)]
    [DataRow("image.png", false)]
    [DataRow("archive.zip", false)]
    [DataRow("binary.exe", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void WorkspaceFileUtils_IsTextFile_CorrectlyClassifiesFiles(string? fileName, bool expected)
    {
        Assert.AreEqual(expected, SemanticAgentBroker.Shared.Utilities.WorkspaceFileUtils.IsTextFile(fileName));
    }
}
