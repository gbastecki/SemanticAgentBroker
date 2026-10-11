using SemanticAgentBroker.Client.Pages;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class TaskQueueTests
{
    [TestMethod]
    public void CanCancel_ReturnsFalseForTerminalStates_AndTrueForActiveStates()
    {
        // Terminal states must NOT allow cancellation
        Assert.IsFalse(TaskQueue.CanCancel(TaskState.Failed), "Failed task cannot be cancelled.");
        Assert.IsFalse(TaskQueue.CanCancel(TaskState.Completed), "Completed task cannot be cancelled.");
        Assert.IsFalse(TaskQueue.CanCancel(TaskState.Cancelled), "Cancelled task cannot be cancelled.");

        // Active/In-flight states must allow cancellation
        Assert.IsTrue(TaskQueue.CanCancel(TaskState.Draft), "Draft task can be cancelled.");
        Assert.IsTrue(TaskQueue.CanCancel(TaskState.Queued), "Queued task can be cancelled.");
        Assert.IsTrue(TaskQueue.CanCancel(TaskState.InProgress), "InProgress task can be cancelled.");
        Assert.IsTrue(TaskQueue.CanCancel(TaskState.Paused), "Paused task can be cancelled.");
        Assert.IsTrue(TaskQueue.CanCancel(TaskState.Planning), "Planning task can be cancelled.");
        Assert.IsTrue(TaskQueue.CanCancel(TaskState.PlanReview), "PlanReview task can be cancelled.");
        Assert.IsTrue(TaskQueue.CanCancel(TaskState.AwaitingApproval), "AwaitingApproval task can be cancelled.");
        Assert.IsTrue(TaskQueue.CanCancel(TaskState.AwaitingInput), "AwaitingInput task can be cancelled.");
    }
}
