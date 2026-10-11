using SemanticAgentBroker.Core.Features.Agents.Implementations;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Core.Features.Agents;

[TestClass]
public class TaskWorkspacePathTests
{
    [TestMethod]
    public void GetWorkspacePath_WithEmptyGuid_ThrowsArgumentException()
    {
        var sysOptions = new AgentSystemOptions { TaskWorkspacesPath = "TaskWorkspaces" };
        Assert.Throws<ArgumentException>(() => AgentTaskContext.GetWorkspacePath(Guid.Empty, sysOptions));
    }

    [TestMethod]
    public void Initialize_WithEmptyGuid_ThrowsArgumentException()
    {
        var aiOptions = new AiProviderOptions();
        var sysOptions = new AgentSystemOptions { TaskWorkspacesPath = "TaskWorkspaces" };
        var context = new AgentTaskContext(aiOptions, sysOptions);

        Assert.Throws<ArgumentException>(() => context.Initialize(Guid.Empty));
    }

    [TestMethod]
    public void GetWorkspacePath_WithValidGuid_ReturnsDeterministicPath()
    {
        var taskId = Guid.NewGuid();
        var sysOptions = new AgentSystemOptions { TaskWorkspacesPath = @"C:\TestWorkspaces" };
        var path = AgentTaskContext.GetWorkspacePath(taskId, sysOptions);

        Assert.AreEqual(Path.Combine(@"C:\TestWorkspaces", taskId.ToString()), path);
    }

    [TestMethod]
    public void GetWorkspacePath_WithNullSysOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => AgentTaskContext.GetWorkspacePath(Guid.NewGuid(), null!));
    }

    [TestMethod]
    public void GetWorkspacePath_WithEmptyOrWhitespacePath_FallsBackToTempPath()
    {
        var taskId = Guid.NewGuid();
        var sysOptions = new AgentSystemOptions { TaskWorkspacesPath = "   " };
        var path = AgentTaskContext.GetWorkspacePath(taskId, sysOptions);

        Assert.IsTrue(path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
        StringAssert.EndsWith(path, taskId.ToString());
    }

    [TestMethod]
    public void GetWorkspacePath_WithRelativePath_CombinesWithBaseDirectory()
    {
        var taskId = Guid.NewGuid();
        var sysOptions = new AgentSystemOptions { TaskWorkspacesPath = "RelativeWorkspaces" };
        var path = AgentTaskContext.GetWorkspacePath(taskId, sysOptions);

        var expectedPrefix = Path.Combine(AppContext.BaseDirectory, "RelativeWorkspaces");
        Assert.IsTrue(path.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase));
        StringAssert.EndsWith(path, taskId.ToString());
    }

    [TestMethod]
    public void Initialize_And_Cleanup_LifecycleManagesDirectory()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var tempBase = Path.Combine(Path.GetTempPath(), "sab_ctx_test_" + Guid.NewGuid().ToString("N"));
        var sysOptions = new AgentSystemOptions { TaskWorkspacesPath = tempBase };
        var aiOptions = new AiProviderOptions();

        var context = new AgentTaskContext(aiOptions, sysOptions);

        // Act 1: Initialize
        context.Initialize(taskId);

        // Assert 1: Properties set and directory created
        Assert.AreEqual(taskId, context.TaskId);
        Assert.IsTrue(Directory.Exists(context.WorkspacePath));
        StringAssert.EndsWith(context.WorkspacePath, taskId.ToString());

        // Act 2: Cleanup
        context.Cleanup();

        // Assert 2: Directory removed
        Assert.IsFalse(Directory.Exists(context.WorkspacePath));

        // Cleanup tempBase parent if empty
        if (Directory.Exists(tempBase))
        {
            try { Directory.Delete(tempBase, true); } catch { }
        }
    }

    [TestMethod]
    public void Cleanup_WhenWorkspacePathEmptyOrNonExistent_DoesNotThrow()
    {
        // Arrange - Uninitialized context with empty workspace path
        var context = new AgentTaskContext(new AiProviderOptions(), new AgentSystemOptions());

        // Act & Assert - Should not throw
        context.Cleanup();
    }

    [TestMethod]
    public void AgentTaskContext_Constructor_InitializesWithUnassignedTaskIdAndEmptyWorkspace()
    {
        // Arrange & Act
        var context = new AgentTaskContext(new AiProviderOptions(), new AgentSystemOptions());

        // Assert
        Assert.AreEqual(Guid.Empty, context.TaskId);
        Assert.AreEqual(string.Empty, context.WorkspacePath);
    }
}
