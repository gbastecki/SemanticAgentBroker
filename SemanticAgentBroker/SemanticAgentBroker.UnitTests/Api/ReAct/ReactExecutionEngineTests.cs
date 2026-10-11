using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Moq;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.ReAct.Services;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Api.Hubs;
using SemanticAgentBroker.Core.Features.TaskState;
using SemanticAgentBroker.Core.Features.TokenCounting;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.ReAct;

[TestClass]
public class ReactExecutionEngineTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private TestWorkspaceFixture _workspaceFixture = null!;
    private AppDbContext _db = null!;
    private Mock<IHubContext<AgentLogHub>> _hubMock = null!;
    private Mock<ISubagentExecutor> _subagentExecutorMock = null!;
    private Mock<ITokenCounter> _tokenCounterMock = null!;
    private Mock<ITaskStateManager> _stateManagerMock = null!;
    private TaskOperationsService _taskOps = null!;
    private AgentTaskStore _taskStore = null!;
    private ToolExecutionService _toolExecutionService = null!;
    private Mock<SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager> _lockManagerMock = null!;
    private AgentSystemOptions _sysOptions = null!;
    private AiProviderOptions _aiOptions = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();
        _workspaceFixture = new TestWorkspaceFixture();
        _db = _dbFixture.CreateAppDbContext();

        _hubMock = new Mock<IHubContext<AgentLogHub>>();
        var clientProxyMock = new Mock<IClientProxy>();
        var hubClientsMock = new Mock<IHubClients>();
        _hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
        hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxyMock.Object);

        _sysOptions = _dbFixture.CreateSystemOptions();
        _sysOptions.TaskWorkspacesPath = Path.GetDirectoryName(_workspaceFixture.WorkspacePath)!;
        _aiOptions = new AiProviderOptions();

        _taskStore = new AgentTaskStore(_db, _hubMock.Object, _sysOptions);
        _taskOps = new TaskOperationsService();
        _subagentExecutorMock = new Mock<ISubagentExecutor>();
        _tokenCounterMock = new Mock<ITokenCounter>();
        _stateManagerMock = new Mock<ITaskStateManager>();
        _lockManagerMock = new Mock<SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager>();
        _lockManagerMock.Setup(l => l.AcquireLockAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<IDisposable>().Object);

        var emptySnapshot = new TaskStateSnapshot(
            Objective: "Objective",
            CompletedSteps: new List<CompletedStep>(),
            FilesManifest: new Dictionary<string, string>(),
            KeyFacts: new List<string>(),
            CompactionCount: 0,
            CurrentStatus: "Active",
            RecentObservations: new List<RecentObservation>());

        _stateManagerMock.Setup(s => s.ReadStateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptySnapshot);

        _stateManagerMock.Setup(s => s.BuildContextInjectionAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("# State Injection");
        _tokenCounterMock.Setup(t => t.CountTokens(It.IsAny<string>(), It.IsAny<string>())).Returns(10);

        _toolExecutionService = new ToolExecutionService(
            _taskStore,
            _stateManagerMock.Object,
            _aiOptions,
            _sysOptions,
            NullLogger<ToolExecutionService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _dbFixture.Dispose();
        _workspaceFixture.Dispose();
    }

    private ReactExecutionEngine CreateEngine()
    {
        return new ReactExecutionEngine(
            _taskStore,
            _taskOps,
            _stateManagerMock.Object,
            _toolExecutionService,
            _subagentExecutorMock.Object,
            _tokenCounterMock.Object,
            _sysOptions,
            _aiOptions,
            NullLogger<ReactExecutionEngine>.Instance,
            _lockManagerMock.Object);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithImmediateFinalAnswer_ReturnsAnswerAndPersistsState()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Say hello",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "I will provide the greeting.",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Hello, world!",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();
        var kernel = new Kernel();

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Say hello",
            maxSteps: 5,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("Hello, world!", result);
        _stateManagerMock.Verify(s => s.FinalizeStateAsync(taskId, "Hello, world!", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenFinalResultTruncated_AcquiresWorkspaceLockForFinalResult()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "Orchestrator",
            UserPrompt = "Say hello",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        var kernel = new Kernel();
        var longResult = new string('A', _aiOptions.Inference.MaxFinalAnswerInlineChars + 100);

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Done",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: longResult,
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Say hello",
            maxSteps: 5,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert - lockManager was invoked for final_result.md
        _lockManagerMock.Verify(l => l.AcquireLockAsync(taskId, "final_result.md", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithConsecutivePlanOnlyDeadlock_TerminatesGracefully()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Complex task",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Planning my next move...",
                Plan: "1. Think more\n2. Replan",
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: null,
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();
        var kernel = new Kernel();

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Complex task",
            maxSteps: 10,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "unable to make progress after multiple reasoning attempts");
    }

    [TestMethod]
    public async Task ExecuteAsync_ExceedingMaxSteps_ReturnsStepLimitErrorMessage()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Long task",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Running a step...",
                Plan: null,
                Actions: new[] { new ToolAction("FileIO-SaveToWorkspace", "{\"filename\":\"tmp.txt\",\"content\":\"data\"}") },
                FinalAnswer: null,
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("FileIO", [KernelFunctionFactory.CreateFromMethod(() => "saved", "SaveToWorkspace")]);

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Long task",
            maxSteps: 2,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Reached maximum step limit (2)");
    }

    [TestMethod]
    public async Task ExecuteAsync_FinalAnswerWorkspaceFile_AnchorsOutputFromDisk()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Generate report",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        var finalResultPath = Path.Combine(_workspaceFixture.WorkspacePath, "final_result.md");
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        await File.WriteAllTextAsync(finalResultPath, "# Comprehensive Analysis Report\nKey metrics: 100%");

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Report generated.",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "I have written the report to /app/workdir/final_result.md",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();
        var kernel = new Kernel();

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Generate report",
            maxSteps: 5,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Comprehensive Analysis Report");
        StringAssert.Contains(result, "Key metrics: 100%");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithActionsExceedingConcurrencyLimit_ExecutesAllInBatches()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Batch test",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        _aiOptions.Inference.MaxParallelToolCalls = 2; // Concurrency limit = 2

        int executedCount = 0;
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod(() => { Interlocked.Increment(ref executedCount); return "done"; }, "RunAction")
        ]);

        // Step 1: Subagent outputs 5 parallel actions
        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Executing 5 actions in batches",
                Plan: null,
                Actions: new[]
                {
                    new ToolAction("TestPlugin-RunAction", "{\"id\":1}"),
                    new ToolAction("TestPlugin-RunAction", "{\"id\":2}"),
                    new ToolAction("TestPlugin-RunAction", "{\"id\":3}"),
                    new ToolAction("TestPlugin-RunAction", "{\"id\":4}"),
                    new ToolAction("TestPlugin-RunAction", "{\"id\":5}")
                },
                FinalAnswer: null,
                IsError: false,
                ErrorMessage: null));

        // Step 2: Subagent provides final answer
        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 2, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Finished.",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "All done!",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Batch test",
            maxSteps: 5,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("All done!", result);
        Assert.AreEqual(5, executedCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithUserInterruption_InjectsInterruptionIntoNextStep()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Interruption test",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod(() => "action done", "DoWork")
        ]);

        // Send an interruption before step 1 completes
        _taskOps.SendInterruption(taskId, "Stop and summarize instead", "Operator");

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Doing work",
                Plan: null,
                Actions: [new ToolAction("TestPlugin-DoWork", "{}")],
                FinalAnswer: null,
                IsError: false,
                ErrorMessage: null));

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 2, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Understood interruption.",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Interruption received and handled.",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Interruption test",
            maxSteps: 5,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("Interruption received and handled.", result);
        _stateManagerMock.Verify(s => s.RecordStepAsync(
            taskId, 1, "UserInterruption", "", It.Is<string>(obs => obs.Contains("Stop and summarize instead")), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_LargeFinalAnswerExceedingInlineLimit_SpillsToWorkspaceFile()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Generate large report",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        _aiOptions.Inference.MaxFinalAnswerInlineChars = 50;

        string largeReport = "This is a very detailed and lengthy final report output that will definitely exceed fifty characters limit.";
        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Done",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: largeReport,
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();
        var kernel = new Kernel();

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Generate large report",
            maxSteps: 3,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert - Return text is truncated with pointer notice
        Assert.IsNotNull(result);
        StringAssert.Contains(result, "[TRUNCATED]");
        StringAssert.Contains(result, "/app/workdir/final_result.md");

        // Verify file was written to disk in workspace
        var finalResultPath = Path.Combine(_workspaceFixture.WorkspacePath, "final_result.md");
        Assert.IsTrue(File.Exists(finalResultPath));
        Assert.AreEqual(largeReport, await File.ReadAllTextAsync(finalResultPath));
    }

    [TestMethod]
    public async Task ExecuteAsync_WithDangerousActionApproved_ExecutesActionAndCompletes()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Delete old log file",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        bool actionExecuted = false;
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("FileIO", [
            CreateDangerousFunction(() => { actionExecuted = true; return "File deleted"; }, "DeleteFile")
        ]);

        // Step 1: Request dangerous action
        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Deleting the file.",
                Plan: null,
                Actions: [new ToolAction("FileIO-DeleteFile", "{\"filename\":\"old.log\"}")],
                FinalAnswer: null,
                IsError: false,
                ErrorMessage: null));

        // Step 2: Final answer after tool execution
        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 2, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "File deletion verified.",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Operation completed successfully.",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();

        var clientProxyMock = new Mock<IClientProxy>();
        clientProxyMock.Setup(c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns((string method, object?[] args, CancellationToken token) =>
            {
                if (args.Length > 0 && args[0] is TaskStateChangedEvent evt && evt.NewState == TaskState.AwaitingApproval)
                {
                    _taskOps.ApproveStep(taskId);
                }
                return Task.CompletedTask;
            });
        var hubClientsMock = new Mock<IHubClients>();
        hubClientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);
        hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxyMock.Object);
        _hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Delete old log file",
            maxSteps: 5,
            debugMode: false,
            isContinuation: false,
            ct: cts.Token);

        // Assert
        Assert.AreEqual("Operation completed successfully.", result);
        Assert.IsTrue(actionExecuted);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithDangerousActionRejected_RecordsRejectionObservationAndContinues()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Delete database",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        bool actionExecuted = false;
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("Database", [
            CreateDangerousFunction(() => { actionExecuted = true; return "Database dropped"; }, "DropTable")
        ]);

        // Step 1: Request dangerous action
        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Dropping table.",
                Plan: null,
                Actions: [new ToolAction("Database-DropTable", "{\"table\":\"Users\"}")],
                FinalAnswer: null,
                IsError: false,
                ErrorMessage: null));

        // Step 2: Receive rejection observation and finish
        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 2, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Understood rejection.",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Action was aborted as requested.",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();

        var clientProxyMock = new Mock<IClientProxy>();
        clientProxyMock.Setup(c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns((string method, object?[] args, CancellationToken token) =>
            {
                if (args.Length > 0 && args[0] is TaskStateChangedEvent evt && evt.NewState == TaskState.AwaitingApproval)
                {
                    _taskOps.RejectStep(taskId, "Dropping tables is strictly forbidden.", "SecurityAdmin");
                }
                return Task.CompletedTask;
            });
        var hubClientsMock = new Mock<IHubClients>();
        hubClientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);
        hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxyMock.Object);
        _hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Delete database",
            maxSteps: 5,
            debugMode: false,
            isContinuation: false,
            ct: cts.Token);

        // Assert
        Assert.AreEqual("Action was aborted as requested.", result);
        Assert.IsFalse(actionExecuted);
        _stateManagerMock.Verify(s => s.RecordStepAsync(
            taskId, 1, "Database-DropTable", "{\"table\":\"Users\"}", It.Is<string>(obs => obs.Contains("Rejected: Dropping tables is strictly forbidden.")), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenSubagentReturnsError_LogsToolErrorAndTerminates()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Failing subagent prompt",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: null,
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: null,
                IsError: true,
                ErrorMessage: "LLM endpoint unreachable after retries"));

        var engine = CreateEngine();
        var kernel = new Kernel();

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Failing subagent prompt",
            maxSteps: 5,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("Task failed: LLM endpoint unreachable after retries", result);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithIsContinuation_AppendsObjectiveAndOffsetsStartingStep()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Follow-up task",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        _stateManagerMock.Setup(s => s.GetMaxStepNumberAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 4, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Executing continuation step 4.",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Continuation complete.",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();
        var kernel = new Kernel();

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Follow-up task",
            maxSteps: 5,
            debugMode: false,
            isContinuation: true,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("Continuation complete.", result);
        _stateManagerMock.Verify(s => s.AppendObjectiveAsync(taskId, "Follow-up task", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithPauseAndResume_ReleasesAndReacquiresSemaphore()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Pausing task",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        var semaphore = new SemaphoreSlim(1, 1);
        await semaphore.WaitAsync(); // Simulate holding semaphore slot
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        _taskOps.PauseTask(taskId);

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Executing after resume.",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Resumed and finished.",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();
        var kernel = new Kernel();

        // Simulate async resume
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            _taskOps.ResumeTask(taskId);
        });

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Pausing task",
            maxSteps: 5,
            debugMode: false,
            isContinuation: false,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("Resumed and finished.", result);
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenSemaphoreDisposedDuringPause_ThrowsOperationCanceledException()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Pausing task",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        var semaphore = new SemaphoreSlim(1, 1);
        await semaphore.WaitAsync(); // Holding semaphore
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        _taskOps.PauseTask(taskId);

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Executing after resume.",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Resumed and finished.",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();
        var kernel = new Kernel();

        // Simulate shutdown disposing the semaphore while paused
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                using var checkDb = _dbFixture.CreateAppDbContext();
                var s = await checkDb.TaskSessions.FindAsync(taskId);
                if (s?.State == TaskState.Paused)
                    break;
                await Task.Delay(20);
            }
            semaphore.Dispose();
            _taskOps.ResumeTask(taskId);
        });

        // Act & Assert - Semaphore disposal on shutdown must abort execution with OperationCanceledException
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await engine.ExecuteAsync(
                taskId,
                kernel,
                systemPrompt: "System",
                originalObjective: "Pausing task",
                maxSteps: 5,
                debugMode: false,
                isContinuation: false,
                executionSemaphore: semaphore,
                semaphoreState: semState,
                ct: CancellationToken.None);
        });

        Assert.IsFalse(semState.IsHeld);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithMaliciousAnchoringPath_RejectsPathTraversal()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Anchoring test",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        // Return path traversal in final answer
        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Done",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Read result: /app/workdir/../../secret.md",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();
        var kernel = new Kernel();

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Anchoring test",
            maxSteps: 3,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert - Traversal is rejected so the original string is returned as-is
        Assert.AreEqual("Read result: /app/workdir/../../secret.md", result);
    }

    [TestMethod]
    public async Task ExecuteAsync_StepLimitExhausted_CallsFinalizeState()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Step limit test",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Still reasoning...",
                Plan: "1. Step 1",
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: null,
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();
        var kernel = new Kernel();

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Step limit test",
            maxSteps: 2,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert - FinalizeStateAsync must have been called on TaskStateManager
        _stateManagerMock.Verify(
            s => s.FinalizeStateAsync(taskId, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "TaskStateManager.FinalizeStateAsync must be called when the task exhausts steps or reasoning loops.");
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenDangerousApprovalThrowsAndCancelled_PreservesOriginalException()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Dangerous exception test",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        var kernel = new Kernel();
        var fn = CreateDangerousFunction(() => "res", "DeleteDatabase");
        var plugin = KernelPluginFactory.CreateFromFunctions("TestPlugin", new[] { fn });
        kernel.Plugins.Add(plugin);

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Executing dangerous action...",
                Plan: "1. Delete",
                Actions: new[] { new ToolAction("TestPlugin-DeleteDatabase", "{}") },
                FinalAnswer: null,
                IsError: false,
                ErrorMessage: null));

        using var cts = new CancellationTokenSource();

        // When AwaitingApproval is broadcast, cancel token and throw an InvalidOperationException from try block
        var clientProxyMock = new Mock<IClientProxy>();
        clientProxyMock.Setup(c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns((string method, object?[] args, CancellationToken token) =>
            {
                if (args.Length > 0 && args[0] is TaskStateChangedEvent evt && evt.NewState == TaskState.AwaitingApproval)
                {
                    cts.Cancel();
                    throw new InvalidOperationException("Original error during approval gate!");
                }
                return Task.CompletedTask;
            });

        var hubClientsMock = new Mock<IHubClients>();
        hubClientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);
        hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxyMock.Object);
        _hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);

        var engine = CreateEngine();
        using var executionSemaphore = new SemaphoreSlim(0, 1);
        var semaphoreState = new SemaphoreOwnershipState { IsHeld = true };

        // Act & Assert - The original InvalidOperationException must NOT be masked by OperationCanceledException
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Dangerous exception test",
            maxSteps: 2,
            debugMode: false,
            isContinuation: false,
            executionSemaphore: executionSemaphore,
            semaphoreState: semaphoreState,
            ct: cts.Token));

        Assert.AreEqual("Original error during approval gate!", ex.Message);
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenWorkspaceFolderDoesNotExist_CreatesDirectoryAndSavesFinalResult()
    {
        // Arrange - use a fresh taskId whose workspace directory has NOT been created on disk
        var taskId = Guid.NewGuid();
        var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, _sysOptions);
        if (Directory.Exists(workspacePath))
        {
            Directory.Delete(workspacePath, true);
        }
        Assert.IsFalse(Directory.Exists(workspacePath));

        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Generate large report without prior workspace",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        _aiOptions.Inference.MaxFinalAnswerInlineChars = 50;
        string largeReport = "This is a detailed report that exceeds the fifty characters limit and requires workspace file persistence.";

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Done",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: largeReport,
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();
        var kernel = new Kernel();

        try
        {
            // Act
            var result = await engine.ExecuteAsync(
                taskId,
                kernel,
                systemPrompt: "System",
                originalObjective: "Generate large report without prior workspace",
                maxSteps: 3,
                debugMode: false,
                isContinuation: false,
                ct: CancellationToken.None);

            // Assert
            Assert.IsNotNull(result);
            StringAssert.Contains(result, "[TRUNCATED]");
            StringAssert.Contains(result, "/app/workdir/final_result.md");

            var finalResultPath = Path.Combine(workspacePath, "final_result.md");
            Assert.IsTrue(Directory.Exists(workspacePath), "Workspace directory should have been created automatically.");
            Assert.IsTrue(File.Exists(finalResultPath), "final_result.md should exist in workspace.");
            Assert.AreEqual(largeReport, await File.ReadAllTextAsync(finalResultPath));
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                try { Directory.Delete(workspacePath, true); } catch { }
            }
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenWorkspaceFolderDoesNotExist_CreatesDirectoryAndSavesRuntimePlan()
    {
        // Arrange - use a fresh taskId whose workspace directory has NOT been created on disk
        var taskId = Guid.NewGuid();
        var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, _sysOptions);
        if (Directory.Exists(workspacePath))
        {
            Directory.Delete(workspacePath, true);
        }
        Assert.IsFalse(Directory.Exists(workspacePath));

        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Plan step without prior workspace",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        string planContent = "1. First step\n2. Second step";

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Planning",
                Plan: planContent,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Finished",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();
        var kernel = new Kernel();

        try
        {
            // Act
            var result = await engine.ExecuteAsync(
                taskId,
                kernel,
                systemPrompt: "System",
                originalObjective: "Plan step without prior workspace",
                maxSteps: 3,
                debugMode: false,
                isContinuation: false,
                ct: CancellationToken.None);

            // Assert
            Assert.IsNotNull(result);
            var runtimePlanPath = Path.Combine(workspacePath, "_runtime_plan.md");
            Assert.IsTrue(Directory.Exists(workspacePath), "Workspace directory should have been created automatically.");
            Assert.IsTrue(File.Exists(runtimePlanPath), "_runtime_plan.md should exist in workspace.");
            Assert.AreEqual(planContent, await File.ReadAllTextAsync(runtimePlanPath));
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                try { Directory.Delete(workspacePath, true); } catch { }
            }
        }
    }

    [TestMethod]
    public async Task MultiToolStep_UsesPerToolScope_NoConcurrentDbContextViolation()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Execute multiple tools concurrently",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        int scopesCreated = 0;
        var services = new ServiceCollection();
        services.AddScoped<IHubContext<AgentLogHub>>(_ => _hubMock.Object);
        services.AddScoped(_ => _dbFixture.CreateAppDbContext());
        services.AddScoped<AgentTaskStore>();
        services.AddScoped<ITaskStateManager>(_ => _stateManagerMock.Object);
        services.AddSingleton(_aiOptions);
        services.AddSingleton(_sysOptions);
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ToolExecutionService>>(NullLogger<ToolExecutionService>.Instance);
        services.AddScoped(sp =>
        {
            Interlocked.Increment(ref scopesCreated);
            return new ToolExecutionService(
                sp.GetRequiredService<AgentTaskStore>(),
                sp.GetRequiredService<ITaskStateManager>(),
                sp.GetRequiredService<AiProviderOptions>(),
                sp.GetRequiredService<AgentSystemOptions>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ToolExecutionService>>(),
                _lockManagerMock.Object);
        });

        var serviceProvider = services.BuildServiceProvider();

        var engine = new ReactExecutionEngine(
            _taskStore,
            _taskOps,
            _stateManagerMock.Object,
            _toolExecutionService,
            _subagentExecutorMock.Object,
            _tokenCounterMock.Object,
            _sysOptions,
            _aiOptions,
            NullLogger<ReactExecutionEngine>.Instance,
            _lockManagerMock.Object,
            serviceProvider);

        var kernel = new Kernel();
        var mockPlugin = new MultiToolTestPlugin();
        kernel.Plugins.AddFromObject(mockPlugin, "MultiTool");

        _subagentExecutorMock.SetupSequence(s => s.ExecuteStepAsync(
            taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Running tools concurrently",
                Plan: null,
                Actions: new[]
                {
                    new ToolAction("MultiTool-ActionA", "{}"),
                    new ToolAction("MultiTool-ActionB", "{}")
                },
                FinalAnswer: null,
                IsError: false,
                ErrorMessage: null))
            .ReturnsAsync(new SubagentResult(
                Thought: "Done",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Complete",
                IsError: false,
                ErrorMessage: null));

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Test multi-tool scopes",
            maxSteps: 3,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("Complete", result);
        Assert.IsTrue(scopesCreated >= 2, $"Expected at least 2 scoped ToolExecutionService resolutions for parallel tools, but got {scopesCreated}");
        Assert.IsTrue(mockPlugin.ActionAExecuted);
        Assert.IsTrue(mockPlugin.ActionBExecuted);
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenDangerousApprovalAndSemaphoreNotHeld_DoesNotLeakPermits()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Delete old log file",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("FileIO", [
            KernelFunctionFactory.CreateFromMethod(() => "File deleted", "DeleteFile")
        ]);

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Deleting the file.",
                Plan: null,
                Actions: [new ToolAction("FileIO-DeleteFile", "{\"filename\":\"old.log\"}")],
                FinalAnswer: null,
                IsError: false,
                ErrorMessage: null));

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 2, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "File deletion verified.",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Operation completed successfully.",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();

        var clientProxyMock = new Mock<IClientProxy>();
        clientProxyMock.Setup(c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns((string method, object?[] args, CancellationToken token) =>
            {
                if (args.Length > 0 && args[0] is TaskStateChangedEvent evt && evt.NewState == TaskState.AwaitingApproval)
                {
                    _taskOps.ApproveStep(taskId);
                }
                return Task.CompletedTask;
            });
        var hubClientsMock = new Mock<IHubClients>();
        hubClientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);
        hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxyMock.Object);
        _hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);

        using var semaphore = new SemaphoreSlim(1, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = false };

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Delete old log file",
            maxSteps: 5,
            debugMode: false,
            isContinuation: false,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual(1, semaphore.CurrentCount, "Semaphore permit must not be acquired if not released.");
        Assert.IsFalse(semState.IsHeld, "Semaphore state must remain unheld.");
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenPausedAndSemaphoreNotHeld_DoesNotLeakPermits()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _taskOps.PauseTask(taskId); // Start paused
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Paused execution test",
            State = TaskState.Paused
        });
        await _db.SaveChangesAsync();

        var kernel = new Kernel();

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Running task after pause.",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Done after resume.",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();

        var clientProxyMock = new Mock<IClientProxy>();
        clientProxyMock.Setup(c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns((string method, object?[] args, CancellationToken token) =>
            {
                if (args.Length > 0 && args[0] is TaskStateChangedEvent evt && evt.NewState == TaskState.Paused)
                {
                    _taskOps.ResumeTask(taskId);
                }
                return Task.CompletedTask;
            });
        var hubClientsMock = new Mock<IHubClients>();
        hubClientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);
        hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxyMock.Object);
        _hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);

        using var semaphore = new SemaphoreSlim(1, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = false };

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Paused execution test",
            maxSteps: 3,
            debugMode: false,
            isContinuation: false,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual(1, semaphore.CurrentCount, "Semaphore permit must not be acquired if not released.");
        Assert.IsFalse(semState.IsHeld, "Semaphore state must remain unheld.");
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenRuntimePlanRepeated_DeduplicatesPlanPersistence()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Plan deduplication test",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        var kernel = new Kernel();
        const string identicalPlan = "1. Step one\n2. Step two";

        _subagentExecutorMock.SetupSequence(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Step 1 thinking",
                Plan: identicalPlan,
                Actions: new List<ToolAction> { new("FileIO-SaveToWorkspace", "{\"file\":\"a.txt\"}") },
                FinalAnswer: null,
                IsError: false,
                ErrorMessage: null))
            .ReturnsAsync(new SubagentResult(
                Thought: "Step 2 thinking",
                Plan: identicalPlan, // Same identical plan!
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Finished",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();

        // Act
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Plan deduplication test",
            maxSteps: 5,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert - Latest plan must be version 1, not version 2
        var latestPlan = await _taskStore.GetLatestPlanAsync(taskId);
        Assert.IsNotNull(latestPlan);
        Assert.AreEqual(1, latestPlan.Version, "Repeated identical plan must not create a new version.");
        Assert.AreEqual(identicalPlan, latestPlan.PlanMarkdown);
    }

    [TestMethod]
    public async Task ExecuteAsync_Anchoring_RejectsInternalStateFilesAndEmptyFiles()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Anchoring test",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        var kernel = new Kernel();
        var workspacePath = _workspaceFixture.WorkspacePath;

        // Write an internal file and an empty file
        var stateFile = Path.Combine(workspacePath, "_task_state.md");
        await File.WriteAllTextAsync(stateFile, "# Internal Secret State");

        var emptyFile = Path.Combine(workspacePath, "empty_report.md");
        await File.WriteAllTextAsync(emptyFile, "   \n\r  ");

        // Step 1: Subagent tries to anchor to internal _task_state.md
        var internalLink = "[Read the final report here](_task_state.md)";
        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Done",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: internalLink,
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();

        // Act 1
        var result1 = await engine.ExecuteAsync(
            taskId, kernel, systemPrompt: "System", originalObjective: "Anchor test", maxSteps: 1, ct: CancellationToken.None);

        // Assert 1: internal file was rejected, so final answer remains the raw link text, not the file contents
        Assert.AreEqual(internalLink, result1, "Internal files prefixed with '_' must not be anchored.");

        // Step 2: Subagent tries to anchor to empty file
        var emptyLink = "[Read the final report here](empty_report.md)";
        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Done",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: emptyLink,
                IsError: false,
                ErrorMessage: null));

        // Act 2
        var result2 = await engine.ExecuteAsync(
            taskId, kernel, systemPrompt: "System", originalObjective: "Anchor test", maxSteps: 1, ct: CancellationToken.None);

        // Assert 2: empty file was rejected
        Assert.AreEqual(emptyLink, result2, "Empty or whitespace files must not be anchored.");

        // Step 3: Valid file
        var validFile = Path.Combine(workspacePath, "valid_report.txt");
        await File.WriteAllTextAsync(validFile, "Detailed research findings.");
        var validLink = "[Read the final report here](valid_report.txt)";
        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Done",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: validLink,
                IsError: false,
                ErrorMessage: null));

        // Act 3
        var result3 = await engine.ExecuteAsync(
            taskId, kernel, systemPrompt: "System", originalObjective: "Anchor test", maxSteps: 1, ct: CancellationToken.None);

        // Assert 3: valid file was anchored
        Assert.AreEqual("Detailed research findings.", result3, "Valid report file must be anchored successfully.");
    }

    [TestMethod]
    public async Task ExecuteAsync_UserInterruption_DoesNotConsumeStepsBudget()
    {
        // Arrange
        var taskId = _workspaceFixture.SessionId;
        _taskOps.RegisterTask(taskId);
        _db.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Interruption budget test",
            State = TaskState.InProgress
        });
        await _db.SaveChangesAsync();

        var kernel = new Kernel();

        // Queue an interruption before execution begins
        _taskOps.SendInterruption(taskId, "User redirection message", "TestUser");

        _subagentExecutorMock.Setup(s => s.ExecuteStepAsync(
                taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubagentResult(
                Thought: "Responding after interruption.",
                Plan: null,
                Actions: Array.Empty<ToolAction>(),
                FinalAnswer: "Task completed successfully with user feedback.",
                IsError: false,
                ErrorMessage: null));

        var engine = CreateEngine();

        // Act with maxSteps: 1.
        // If the interruption consumed a step, maxSteps (1) would be reached on the interruption iteration,
        // and ExecuteStepAsync would NEVER be called.
        var result = await engine.ExecuteAsync(
            taskId,
            kernel,
            systemPrompt: "System",
            originalObjective: "Interruption budget test",
            maxSteps: 1,
            debugMode: false,
            isContinuation: false,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("Task completed successfully with user feedback.", result);
        _subagentExecutorMock.Verify(s => s.ExecuteStepAsync(
            taskId, It.IsAny<Kernel>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<CancellationToken>()),
            Times.Once,
            "The subagent must be invoked for its reasoning step despite the preceding user interruption.");
    }

    private class MultiToolTestPlugin
    {
        public bool ActionAExecuted { get; private set; }
        public bool ActionBExecuted { get; private set; }

        [KernelFunction("ActionA")]
        public async Task<string> ActionA()
        {
            await Task.Delay(20);
            ActionAExecuted = true;
            return "Result A";
        }

        [KernelFunction("ActionB")]
        public async Task<string> ActionB()
        {
            await Task.Delay(20);
            ActionBExecuted = true;
            return "Result B";
        }
    }

    private static KernelFunction CreateDangerousFunction(Delegate method, string name)
    {
        var func = KernelFunctionFactory.CreateFromMethod(method, name);
        var props = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?> { ["IsDangerous"] = true });
        var prop = typeof(KernelFunctionMetadata).GetProperty(nameof(KernelFunctionMetadata.AdditionalProperties));
        if (prop?.SetMethod != null)
        {
            prop.SetValue(func.Metadata, props);
        }
        else
        {
            var field = typeof(KernelFunctionMetadata).GetField($"<{nameof(KernelFunctionMetadata.AdditionalProperties)}>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field?.SetValue(func.Metadata, props);
        }
        return func;
    }
}


