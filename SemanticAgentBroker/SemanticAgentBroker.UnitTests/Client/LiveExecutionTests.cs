using Moq;
using SemanticAgentBroker.Client.Pages;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class LiveExecutionTests
{
    [TestMethod]
    public void ChatMessage_CreationAndPropertyInitialization()
    {
        // Verify chat message model structure used in LiveExecution
        var now = DateTime.UtcNow;
        var chatMsgType = typeof(LiveExecution).GetNestedType("ChatMessage", System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(chatMsgType);

        var instance = Activator.CreateInstance(chatMsgType, "tool", "Calling FileIO(test.txt)", now, null, null, "Agent1", "User1", null, null, null, null);
        Assert.IsNotNull(instance);

        var propType = chatMsgType.GetProperty("Type");
        var propContent = chatMsgType.GetProperty("Content");
        Assert.AreEqual("tool", propType?.GetValue(instance));
        Assert.AreEqual("Calling FileIO(test.txt)", propContent?.GetValue(instance));
    }

    [TestMethod]
    public void OnDebounceRender_WhenDisposed_ReturnsSafelyWithoutThrowing()
    {
        // Arrange
        var component = new LiveExecution();
        var isDisposedField = typeof(LiveExecution).GetField("_isDisposed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        isDisposedField.SetValue(component, true);

        var onDebounceMethod = typeof(LiveExecution).GetMethod("OnDebounceRender", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act & Assert - Calling OnDebounceRender when disposed should immediately return and not throw
        onDebounceMethod.Invoke(component, new object?[] { null });
    }

    [TestMethod]
    public void OnDebounceRender_LocksAreProperlyInitializedAndDistinct()
    {
        // Arrange
        var component = new LiveExecution();
        var chatLockField = typeof(LiveExecution).GetField("_chatLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var logLockField = typeof(LiveExecution).GetField("_logLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        var chatLock = chatLockField.GetValue(component);
        var logLock = logLockField.GetValue(component);

        // Assert - Both locks must be non-null and distinct instances for concurrent synchronization
        Assert.IsNotNull(chatLock, "_chatLock must be initialized.");
        Assert.IsNotNull(logLock, "_logLock must be initialized.");
        Assert.AreNotSame(chatLock, logLock, "_chatLock and _logLock must be distinct instances.");
    }

    [TestMethod]
    public void ExtractAllActionPairs_WithMixedParameterlessAndParameterizedActions_ParsesAllInOrder()
    {
        // Arrange
        var method = typeof(LiveExecution).GetMethod("ExtractAllActionPairs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.IsNotNull(method);

        var content = "Thought: Listing files then reading.\nAction: ListWorkspaceFiles\nAction: ReadFromWorkspace\nActionInput: {\"path\": \"file.txt\"}\nFinalAnswer: Done.";

        // Act
        var result = method.Invoke(null, new object[] { content }) as System.Collections.IEnumerable;
        Assert.IsNotNull(result);

        var pairs = result.Cast<object>().Select(item =>
        {
            var itemType = item.GetType();
            var actionName = (string)itemType.GetField("Item1")!.GetValue(item)!;
            var actionInput = (string)itemType.GetField("Item2")!.GetValue(item)!;
            return (actionName, actionInput);
        }).ToList();

        // Assert
        Assert.AreEqual(2, pairs.Count, "Should parse both parameterless and parameterized actions in order.");
        Assert.AreEqual("ListWorkspaceFiles", pairs[0].actionName);
        Assert.AreEqual("{}", pairs[0].actionInput);
        Assert.AreEqual("ReadFromWorkspace", pairs[1].actionName);
        Assert.AreEqual("{\"path\": \"file.txt\"}", pairs[1].actionInput);
    }

    [TestMethod]
    public void ExtractAllActionPairs_WithMultipleParameterlessActions_ParsesAllInOrder()
    {
        // Arrange
        var method = typeof(LiveExecution).GetMethod("ExtractAllActionPairs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.IsNotNull(method);

        var content = "Thought: Inspecting environment.\nAction: SystemInfo\nAction: WorkspaceInfo\nFinalAnswer: Complete.";

        // Act
        var result = method.Invoke(null, new object[] { content }) as System.Collections.IEnumerable;
        Assert.IsNotNull(result);

        var pairs = result.Cast<object>().Select(item =>
        {
            var itemType = item.GetType();
            var actionName = (string)itemType.GetField("Item1")!.GetValue(item)!;
            var actionInput = (string)itemType.GetField("Item2")!.GetValue(item)!;
            return (actionName, actionInput);
        }).ToList();

        // Assert
        Assert.AreEqual(2, pairs.Count, "Should parse all parameterless actions in order.");
        Assert.AreEqual("SystemInfo", pairs[0].actionName);
        Assert.AreEqual("{}", pairs[0].actionInput);
        Assert.AreEqual("WorkspaceInfo", pairs[1].actionName);
        Assert.AreEqual("{}", pairs[1].actionInput);
    }

    [TestMethod]
    public async Task HandleChatSubmit_WhenAwaitingInput_SendsInterruptionViaSignalR()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var mockSignalR = new Moq.Mock<SemanticAgentBroker.Client.Features.SignalR.ISignalRService>();
        mockSignalR.Setup(s => s.SendInterruptionAsync(taskId, "Proceed with option B"))
            .ReturnsAsync(true);

        var component = new LiveExecution { TaskId = taskId };

        var signalRProp = typeof(LiveExecution).GetProperty("SignalR", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!;
        signalRProp.SetValue(component, mockSignalR.Object);

        var taskDetailsField = typeof(LiveExecution).GetField("taskDetails", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var taskSession = new TaskSession
        {
            Id = taskId,
            State = TaskState.AwaitingInput,
            AgentName = "TestAgent",
            UserPrompt = "Test prompt"
        };
        taskDetailsField.SetValue(component, taskSession);

        var handleChatSubmitMethod = typeof(LiveExecution).GetMethod("HandleChatSubmit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        var task = (Task)handleChatSubmitMethod.Invoke(component, new object[] { "Proceed with option B" })!;
        await task;

        // Assert
        mockSignalR.Verify(s => s.SendInterruptionAsync(taskId, "Proceed with option B"), Moq.Times.Once());
        Assert.AreEqual(TaskState.InProgress, taskSession.State);
    }

    [TestMethod]
    public async Task HandleChatSubmit_WhenAwaitingApproval_ApproveKeyword_ApprovesStep()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var mockSignalR = new Moq.Mock<SemanticAgentBroker.Client.Features.SignalR.ISignalRService>();
        mockSignalR.Setup(s => s.ApproveStepAsync(taskId)).Returns(Task.CompletedTask);

        var component = new LiveExecution { TaskId = taskId };
        typeof(LiveExecution).GetProperty("SignalR", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!
            .SetValue(component, mockSignalR.Object);

        var taskSession = new TaskSession
        {
            Id = taskId,
            State = TaskState.AwaitingApproval,
            AgentName = "TestAgent",
            UserPrompt = "Test prompt"
        };
        typeof(LiveExecution).GetField("taskDetails", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(component, taskSession);

        var handleChatSubmitMethod = typeof(LiveExecution).GetMethod("HandleChatSubmit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        var task = (Task)handleChatSubmitMethod.Invoke(component, new object[] { "yes" })!;
        await task;

        // Assert
        mockSignalR.Verify(s => s.ApproveStepAsync(taskId), Moq.Times.Once());
        mockSignalR.Verify(s => s.RejectStepAsync(It.IsAny<Guid>(), It.IsAny<string>()), Moq.Times.Never());
    }

    [TestMethod]
    public async Task HandleChatSubmit_WhenAwaitingApproval_RejectKeyword_RejectsStep()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var mockSignalR = new Moq.Mock<SemanticAgentBroker.Client.Features.SignalR.ISignalRService>();
        mockSignalR.Setup(s => s.RejectStepAsync(taskId, It.IsAny<string>())).Returns(Task.CompletedTask);

        var component = new LiveExecution { TaskId = taskId };
        typeof(LiveExecution).GetProperty("SignalR", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!
            .SetValue(component, mockSignalR.Object);

        var taskSession = new TaskSession
        {
            Id = taskId,
            State = TaskState.AwaitingApproval,
            AgentName = "TestAgent",
            UserPrompt = "Test prompt"
        };
        typeof(LiveExecution).GetField("taskDetails", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(component, taskSession);

        var handleChatSubmitMethod = typeof(LiveExecution).GetMethod("HandleChatSubmit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        var task = (Task)handleChatSubmitMethod.Invoke(component, new object[] { "no" })!;
        await task;

        // Assert
        mockSignalR.Verify(s => s.RejectStepAsync(taskId, "User rejected step via chat."), Moq.Times.Once());
        mockSignalR.Verify(s => s.ApproveStepAsync(It.IsAny<Guid>()), Moq.Times.Never());
    }

    [TestMethod]
    public async Task HandleChatSubmit_WhenAwaitingApproval_AmbiguousInput_DoesNotReject()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var mockSignalR = new Moq.Mock<SemanticAgentBroker.Client.Features.SignalR.ISignalRService>();

        var component = new LiveExecution { TaskId = taskId };
        typeof(LiveExecution).GetProperty("SignalR", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!
            .SetValue(component, mockSignalR.Object);

        var taskSession = new TaskSession
        {
            Id = taskId,
            State = TaskState.AwaitingApproval,
            AgentName = "TestAgent",
            UserPrompt = "Test prompt"
        };
        typeof(LiveExecution).GetField("taskDetails", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(component, taskSession);

        var handleChatSubmitMethod = typeof(LiveExecution).GetMethod("HandleChatSubmit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act - ambiguous or conversational input must NOT reject the step
        var task = (Task)handleChatSubmitMethod.Invoke(component, new object[] { "looks good to me, what will this do?" })!;
        await task;

        // Assert - verify neither RejectStepAsync nor ApproveStepAsync was called
        mockSignalR.Verify(s => s.RejectStepAsync(It.IsAny<Guid>(), It.IsAny<string>()), Moq.Times.Never(), "Ambiguous input should not automatically reject the step.");
        mockSignalR.Verify(s => s.ApproveStepAsync(It.IsAny<Guid>()), Moq.Times.Never());
    }

    [TestMethod]
    public void MergeLogs_PreservesConcurrentLiveLogs_AndAppendsWithoutDuplicates()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        var snapshotLogs = new List<AgentTaskLog>
        {
            new() { Id = id1, TaskId = taskId, Message = "Step 1 started", Timestamp = DateTime.UtcNow.AddSeconds(-10) },
            new() { Id = id2, TaskId = taskId, Message = "Step 1 finished", Timestamp = DateTime.UtcNow.AddSeconds(-5) }
        };

        var liveLogs = new List<AgentTaskLog>
        {
            // id2 was already present in snapshot (duplicate received concurrently)
            new() { Id = id2, TaskId = taskId, Message = "Step 1 finished", Timestamp = DateTime.UtcNow.AddSeconds(-5) },
            // id3 arrived concurrently during fetch and is new
            new() { Id = id3, TaskId = taskId, Message = "Step 2 started", Timestamp = DateTime.UtcNow }
        };

        // Act
        var mergeMethod = typeof(LiveExecution).GetMethod("MergeLogs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.IsNotNull(mergeMethod);
        var merged = (List<AgentTaskLog>)mergeMethod.Invoke(null, new object[] { snapshotLogs, liveLogs })!;

        // Assert
        Assert.AreEqual(3, merged.Count);
        Assert.AreEqual("Step 1 started", merged[0].Message);
        Assert.AreEqual("Step 1 finished", merged[1].Message);
        Assert.AreEqual("Step 2 started", merged[2].Message);
    }

    [TestMethod]
    public void MergeChatMessages_PreservesConcurrentLiveMessages_AndSortsChronologically()
    {
        // Arrange
        var chatMsgType = typeof(LiveExecution).GetNestedType("ChatMessage", System.Reflection.BindingFlags.NonPublic)!;
        var now = DateTime.UtcNow;

        var snap1 = Activator.CreateInstance(chatMsgType, "user", "Prompt 1", now.AddSeconds(-20), null, null, null, "User", null, null, null, null)!;
        var snap2 = Activator.CreateInstance(chatMsgType, "thought", "Thinking...", now.AddSeconds(-10), null, null, "Agent", null, null, null, null, null)!;

        var live1 = Activator.CreateInstance(chatMsgType, "thought", "Thinking...", now.AddSeconds(-10), null, null, "Agent", null, null, null, null, null)!; // duplicate
        var live2 = Activator.CreateInstance(chatMsgType, "agent", "Final Answer", now, null, null, "Agent", null, null, null, null, null)!; // new concurrent

        var snapshotListType = typeof(List<>).MakeGenericType(chatMsgType);
        var snapshotList = (System.Collections.IList)Activator.CreateInstance(snapshotListType)!;
        snapshotList.Add(snap1);
        snapshotList.Add(snap2);

        var liveList = (System.Collections.IList)Activator.CreateInstance(snapshotListType)!;
        liveList.Add(live1);
        liveList.Add(live2);

        // Act
        var mergeMethod = typeof(LiveExecution).GetMethod("MergeChatMessages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.IsNotNull(mergeMethod);
        var merged = (System.Collections.IList)mergeMethod.Invoke(null, new object[] { snapshotList, liveList })!;

        // Assert
        Assert.AreEqual(3, merged.Count);
        var propContent = chatMsgType.GetProperty("Content")!;
        Assert.AreEqual("Prompt 1", propContent.GetValue(merged[0]));
        Assert.AreEqual("Thinking...", propContent.GetValue(merged[1]));
        Assert.AreEqual("Final Answer", propContent.GetValue(merged[2]));
    }

    [TestMethod]
    public void ResetTaskState_ClearsPreviousTaskData()
    {
        // Arrange
        var component = new LiveExecution();
        var taskDetailsField = typeof(LiveExecution).GetField("taskDetails", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        taskDetailsField.SetValue(component, new TaskSession { Id = Guid.NewGuid(), UserPrompt = "Old prompt" });

        var planActiveField = typeof(LiveExecution).GetField("isPlanReviewActive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        planActiveField.SetValue(component, true);

        var resetMethod = typeof(LiveExecution).GetMethod("ResetTaskState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.IsNotNull(resetMethod, "ResetTaskState must be implemented on LiveExecution for parameter updates");

        // Act
        resetMethod.Invoke(component, null);

        // Assert
        Assert.IsNull(taskDetailsField.GetValue(component), "taskDetails should be reset to null");
        Assert.IsFalse((bool)planActiveField.GetValue(component)!, "isPlanReviewActive should be reset to false");
    }

    [TestMethod]
    public void ResetTaskState_ResetsActiveTabToValidUITab()
    {
        // Arrange
        var component = new LiveExecution();
        var activeTabField = typeof(LiveExecution).GetField("activeTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        activeTabField.SetValue(component, "files");

        var resetMethod = typeof(LiveExecution).GetMethod("ResetTaskState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        resetMethod.Invoke(component, null);

        // Assert - activeTab must reset to a valid tab supported by LiveExecution.razor ("split", "chat", "console")
        var validTabs = new[] { "split", "chat", "console", "files" };
        var activeTab = (string)activeTabField.GetValue(component)!;
        CollectionAssert.Contains(validTabs, activeTab);
        Assert.AreNotEqual("terminal", activeTab, "activeTab must not be set to 'terminal' because LiveExecution.razor does not render a terminal tab.");
        Assert.AreEqual("split", activeTab, "activeTab should default to 'split' on reset, matching the initial state.");
    }

    [TestMethod]
    public async Task DisposeAsync_UnsubscribesTokenCountUpdatedEvent()
    {
        // Arrange
        var mockSignalR = new Mock<SemanticAgentBroker.Client.Features.SignalR.ISignalRService>();
        var component = new LiveExecution();
        typeof(LiveExecution).GetProperty("SignalR", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!
            .SetValue(component, mockSignalR.Object);

        // Act
        await component.DisposeAsync();

        // Assert - Verify that OnTokenCountUpdated was unsubscribed in DisposeAsync
        mockSignalR.VerifyRemove(
            s => s.OnTokenCountUpdated -= It.IsAny<Action<SemanticAgentBroker.Shared.Domain.TokenCountUpdatedEvent>>(),
            Times.Once,
            "OnTokenCountUpdated must be unsubscribed in DisposeAsync to prevent memory leaks.");
    }

    [TestMethod]
    public async Task HandleChatSubmit_WhenPlanReviewActive_ApprovesPlanEvenIfTaskDetailsStateNotPlanReview()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var mockSignalR = new Mock<SemanticAgentBroker.Client.Features.SignalR.ISignalRService>();
        var component = new LiveExecution();
        typeof(LiveExecution).GetProperty("TaskId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
            .SetValue(component, taskId);
        typeof(LiveExecution).GetProperty("SignalR", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!
            .SetValue(component, mockSignalR.Object);

        var planActiveField = typeof(LiveExecution).GetField("isPlanReviewActive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        planActiveField.SetValue(component, true);

        // taskDetails is in InProgress or not yet transitioned
        var taskDetailsField = typeof(LiveExecution).GetField("taskDetails", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        taskDetailsField.SetValue(component, new TaskSession { Id = taskId, State = TaskState.InProgress });

        var handleMethod = typeof(LiveExecution).GetMethod("HandleChatSubmit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        var task = (Task)handleMethod.Invoke(component, new object[] { "approve" })!;
        await task;

        // Assert - verify ApprovePlanAsync was called on SignalR
        mockSignalR.Verify(s => s.ApprovePlanAsync(taskId), Times.Once, "ApprovePlanAsync must be called when plan review is active and user submits approve");
    }

    [TestMethod]
    public void QueueRender_TransitionsIsStateDirtyAtomically()
    {
        // Arrange
        var component = new LiveExecution();
        var dirtyField = typeof(LiveExecution).GetField("_isStateDirty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(dirtyField, "_isStateDirty field must exist for thread-safe Interlocked operations.");
        Assert.AreEqual(typeof(int), dirtyField.FieldType, "_isStateDirty must be an int for Interlocked operations.");

        var queueRenderMethod = typeof(LiveExecution).GetMethod("QueueRender", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(queueRenderMethod, "QueueRender method must exist.");

        // Initial state is 0 (clean)
        Assert.AreEqual(0, (int)dirtyField.GetValue(component)!);

        // Act - Call QueueRender
        queueRenderMethod.Invoke(component, null);

        // Assert - Field should be 1 (dirty)
        Assert.AreEqual(1, (int)dirtyField.GetValue(component)!);

        // Act - Call QueueRender again while dirty
        queueRenderMethod.Invoke(component, null);

        // Assert - Field remains 1
        Assert.AreEqual(1, (int)dirtyField.GetValue(component)!);
    }
}

