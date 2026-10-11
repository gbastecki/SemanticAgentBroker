using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Moq;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.ReAct.Services;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Api.Hubs;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Reflection;

namespace SemanticAgentBroker.UnitTests.Api.ReAct;

[TestClass]
public class PlanningEngineTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private TestWorkspaceFixture _workspaceFixture = null!;
    private AppDbContext _db = null!;
    private Mock<IHubContext<AgentLogHub>> _hubMock = null!;
    private TaskOperationsService _taskOps = null!;
    private AgentTaskStore _taskStore = null!;
    private AgentSystemOptions _sysOptions = null!;
    private AiProviderOptions _aiOptions = null!;
    private LlmCallService _llmCallService = null!;

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
        _llmCallService = new LlmCallService(_taskStore, _aiOptions, _sysOptions, NullLogger<LlmCallService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _dbFixture.Dispose();
        _workspaceFixture.Dispose();
    }

    [TestMethod]
    public void ExtractOpenQuestions_WithValidQuestions_ReturnsExtractedText()
    {
        // Arrange
        var plan = """
        ## Goal
        Migrate database

        ## Proposed Steps
        1. Step 1

        ## Open Questions
        1. Should we backup the database first?
        2. What is the target cutoff time?

        ## Risks & Considerations
        Downtime during schema migration.
        """;

        // Act
        var method = typeof(PlanningEngine).GetMethod("ExtractOpenQuestions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        var result = (string?)method.Invoke(null, new object[] { plan });

        // Assert
        Assert.IsNotNull(result);
        StringAssert.Contains(result, "Should we backup the database first?");
        StringAssert.Contains(result, "What is the target cutoff time?");
    }

    [TestMethod]
    public void ExtractOpenQuestions_WhenNoneOrNA_ReturnsNull()
    {
        // Arrange
        var planNone = """
        ## Goal
        Simple task

        ## Open Questions
        None.

        ## Risks & Considerations
        Low risk.
        """;

        var planNA = """
        ## Goal
        Simple task

        ## Open Questions
        N/A

        ## Risks & Considerations
        Low risk.
        """;

        var method = typeof(PlanningEngine).GetMethod("ExtractOpenQuestions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);

        // Act & Assert
        var resultNone = (string?)method.Invoke(null, new object[] { planNone });
        var resultNA = (string?)method.Invoke(null, new object[] { planNA });

        Assert.IsNull(resultNone);
        Assert.IsNull(resultNA);
    }

    [TestMethod]
    public async Task GeneratePlanAsync_AutoApprove_ReturnsPlanAndMarksApproved()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var planText = "## Goal\nBuild feature\n## Proposed Steps\n1. Do step 1\n## Open Questions\nNone\n## Risks\nNone";
        var kernel = CreateKernelWithMockResponse(planText);

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(0, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        // Act
        var result = await engine.GeneratePlanAsync(
            sessionId,
            kernel,
            systemPrompt: "System",
            userPrompt: "Build feature",
            debugMode: true,
            autoApprove: true,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            CancellationToken.None);

        // Assert
        Assert.AreEqual(planText, result);
        var latestPlan = await _taskStore.GetLatestPlanAsync(sessionId);
        Assert.IsNotNull(latestPlan);
        Assert.AreEqual(PlanStatus.Approved, latestPlan.Status);
    }

    [TestMethod]
    public async Task GeneratePlanAsync_UserApproves_ReturnsPlan()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var planText = "## Goal\nBuild feature\n## Proposed Steps\n1. Step 1\n## Open Questions\nNone\n## Risks\nNone";
        var kernel = CreateKernelWithMockResponse(planText);

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(0, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        // Post user approval asynchronously
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null && plan.Version == 1)
                    break;
            }
            _taskOps.ApprovePlan(sessionId);
        });

        // Act
        var result = await engine.GeneratePlanAsync(
            sessionId,
            kernel,
            systemPrompt: "System",
            userPrompt: "Build feature",
            debugMode: false,
            autoApprove: false,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            CancellationToken.None);

        // Assert
        Assert.AreEqual(planText, result);
        var latestPlan = await _taskStore.GetLatestPlanAsync(sessionId);
        Assert.IsNotNull(latestPlan);
        Assert.AreEqual(PlanStatus.Approved, latestPlan.Status);
    }

    [TestMethod]
    public async Task GeneratePlanAsync_UserModifiesPlan_ReturnsModifiedPlan()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var planText = "## Goal\nInitial Plan";
        var modifiedPlan = "## Goal\nModified Plan by User\n## Proposed Steps\n1. Custom step";
        var kernel = CreateKernelWithMockResponse(planText);

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(0, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        // Post modify decision first, then approve modified plan
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null && plan.Version == 1)
                    break;
            }
            _taskOps.ModifyPlan(sessionId, modifiedPlan);

            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null && plan.Version == 2)
                    break;
            }
            await Task.Delay(50);
            _taskOps.ApprovePlan(sessionId);
        });

        // Act
        var result = await engine.GeneratePlanAsync(
            sessionId,
            kernel,
            systemPrompt: "System",
            userPrompt: "Build feature",
            debugMode: false,
            autoApprove: false,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            CancellationToken.None);

        // Assert
        Assert.AreEqual(modifiedPlan, result);
        var latestPlan = await _taskStore.GetLatestPlanAsync(sessionId);
        Assert.IsNotNull(latestPlan);
        Assert.AreEqual(2, latestPlan.Version);
        Assert.AreEqual(PlanStatus.Approved, latestPlan.Status);
    }

    [TestMethod]
    public async Task GeneratePlanAsync_UserRejectsWithFeedback_RevisesPlan()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var initialPlan = "## Goal\nInitial Plan";
        var revisedPlan = "## Goal\nRevised Plan addressing feedback\n## Proposed Steps\n1. Revised step";

        var chatServiceMock = new Mock<IChatCompletionService>();
        int callCount = 0;
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(It.IsAny<ChatHistory>(), It.IsAny<PromptExecutionSettings>(), It.IsAny<Kernel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                var content = callCount == 1 ? initialPlan : revisedPlan;
                return new List<ChatMessageContent> { new(AuthorRole.Assistant, content) };
            });

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(0, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        // Post reject with feedback, then approve revised plan
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null && plan.Version == 1)
                    break;
            }
            _taskOps.RejectPlan(sessionId, "Add more tests.");

            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null && plan.Version == 2)
                    break;
            }
            await Task.Delay(50);
            _taskOps.ApprovePlan(sessionId);
        });

        // Act
        var result = await engine.GeneratePlanAsync(
            sessionId,
            kernel,
            systemPrompt: "System",
            userPrompt: "Build feature",
            debugMode: false,
            autoApprove: false,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            CancellationToken.None);

        // Assert
        Assert.AreEqual(revisedPlan, result);
        var latestPlan = await _taskStore.GetLatestPlanAsync(sessionId);
        Assert.IsNotNull(latestPlan);
        Assert.AreEqual(2, latestPlan.Version);
    }

    [TestMethod]
    public async Task GeneratePlanAsync_LlmReturnsEmpty_ReturnsNull()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var kernel = CreateKernelWithMockResponse("");

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(0, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        // Act
        var result = await engine.GeneratePlanAsync(
            sessionId,
            kernel,
            systemPrompt: "System",
            userPrompt: "Build feature",
            debugMode: false,
            autoApprove: false,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            CancellationToken.None);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GeneratePlanAsync_WithPreviousPlan_IncorporatesPreviousPlanIntoHistory()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Follow-up" });
        _db.ImplementationPlans.Add(new ImplementationPlan
        {
            SessionId = sessionId,
            Version = 1,
            PlanMarkdown = "## Goal\nPrevious base plan",
            Status = PlanStatus.Approved
        });
        await _db.SaveChangesAsync();

        ChatHistory? capturedHistory = null;
        var chatServiceMock = new Mock<IChatCompletionService>();
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(It.IsAny<ChatHistory>(), It.IsAny<PromptExecutionSettings>(), It.IsAny<Kernel>(), It.IsAny<CancellationToken>()))
            .Callback<ChatHistory, PromptExecutionSettings, Kernel, CancellationToken>((history, _, _, _) => capturedHistory = history)
            .ReturnsAsync(new List<ChatMessageContent> { new(AuthorRole.Assistant, "## Goal\nUpdated Plan") });

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(0, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        // Act
        var result = await engine.GeneratePlanAsync(
            sessionId,
            kernel,
            systemPrompt: "System",
            userPrompt: "Follow-up",
            debugMode: false,
            autoApprove: true,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            CancellationToken.None);

        // Assert
        Assert.IsNotNull(capturedHistory);
        Assert.IsTrue(capturedHistory.Any(m => m.Content != null && m.Content.Contains("Previous Plan:")));
        Assert.AreEqual("## Goal\nUpdated Plan", result);
    }

    [TestMethod]
    public async Task GeneratePlanAsync_UserAsksQuestion_FeedsQuestionBackAndRegeneratesPlan()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var initialPlan = "## Goal\nInitial Plan with ambiguity";
        var clarifiedPlan = "## Goal\nClarified Plan answering SQLite requirement\n## Proposed Steps\n1. Use SQLite";

        var chatServiceMock = new Mock<IChatCompletionService>();
        int callCount = 0;
        ChatHistory? lastCapturedHistory = null;
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(It.IsAny<ChatHistory>(), It.IsAny<PromptExecutionSettings>(), It.IsAny<Kernel>(), It.IsAny<CancellationToken>()))
            .Callback<ChatHistory, PromptExecutionSettings, Kernel, CancellationToken>((history, _, _, _) => lastCapturedHistory = history)
            .ReturnsAsync(() =>
            {
                callCount++;
                var content = callCount == 1 ? initialPlan : clarifiedPlan;
                return new List<ChatMessageContent> { new(AuthorRole.Assistant, content) };
            });

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(0, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        // Post AskQuestion decision first, then approve the clarified plan
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null && plan.Version == 1)
                    break;
            }
            _taskOps.AskPlanQuestion(sessionId, "Do we need to support SQLite or InMemory database?");

            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null && plan.Version == 2)
                    break;
            }
            await Task.Delay(50);
            _taskOps.ApprovePlan(sessionId);
        });

        // Act
        var result = await engine.GeneratePlanAsync(
            sessionId,
            kernel,
            systemPrompt: "System",
            userPrompt: "Build feature",
            debugMode: false,
            autoApprove: false,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            CancellationToken.None);

        // Assert
        Assert.AreEqual(clarifiedPlan, result);
        Assert.IsNotNull(lastCapturedHistory);
        Assert.IsTrue(lastCapturedHistory.Any(m => m.Content != null && m.Content.Contains("Do we need to support SQLite")));
        var latestPlan = await _taskStore.GetLatestPlanAsync(sessionId);
        Assert.IsNotNull(latestPlan);
        Assert.AreEqual(2, latestPlan.Version);
        Assert.AreEqual(PlanStatus.Approved, latestPlan.Status);
    }

    [TestMethod]
    public async Task GeneratePlanAsync_UserAsksQuestion_BroadcastsPlanRevisionEvent()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var clientProxyMock = new Mock<IClientProxy>();
        var hubClientsMock = new Mock<IHubClients>();
        hubClientsMock.Setup(c => c.Group(sessionId.ToString())).Returns(clientProxyMock.Object);
        _hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);

        var initialPlan = "## Goal\nInitial Plan with ambiguity";
        var clarifiedPlan = "## Goal\nClarified Plan\n## Proposed Steps\n1. Step 1";

        var chatServiceMock = new Mock<IChatCompletionService>();
        int callCount = 0;
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(It.IsAny<ChatHistory>(), It.IsAny<PromptExecutionSettings>(), It.IsAny<Kernel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                var content = callCount == 1 ? initialPlan : clarifiedPlan;
                return new List<ChatMessageContent> { new(AuthorRole.Assistant, content) };
            });

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(0, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null && plan.Version == 1)
                    break;
            }
            _taskOps.AskPlanQuestion(sessionId, "What is the requirement?");

            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null && plan.Version == 2)
                    break;
            }
            await Task.Delay(50);
            _taskOps.ApprovePlan(sessionId);
        });

        // Act
        var result = await engine.GeneratePlanAsync(
            sessionId,
            kernel,
            systemPrompt: "System",
            userPrompt: "Build feature",
            debugMode: false,
            autoApprove: false,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            CancellationToken.None);

        // Assert
        Assert.AreEqual(clarifiedPlan, result);
        clientProxyMock.Verify(c => c.SendCoreAsync(
            HubMethodNames.PlanRevision,
            It.Is<object[]>(args => args.Length == 1 && ((PlanRevisionEvent)args[0]).Version == 2),
            default), Times.Once, "Expected BroadcastPlanRevisionAsync to be invoked when plan is revised in response to user question.");
    }

    [TestMethod]
    public async Task GeneratePlanAsync_MaxRevisionAttemptsExceeded_ReturnsNull()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var chatServiceMock = new Mock<IChatCompletionService>();
        int revisionCount = 0;
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(It.IsAny<ChatHistory>(), It.IsAny<PromptExecutionSettings>(), It.IsAny<Kernel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                revisionCount++;
                return new List<ChatMessageContent> { new(AuthorRole.Assistant, $"## Goal\nPlan Attempt #{revisionCount}") };
            });

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(0, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        // Post 5 consecutive rejections
        _ = Task.Run(async () =>
        {
            for (int i = 1; i <= 6; i++)
            {
                for (int poll = 0; poll < 50; poll++)
                {
                    await Task.Delay(50);
                    var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                    if (plan != null && plan.Version == i)
                        break;
                }
                _taskOps.RejectPlan(sessionId, $"Rejected iteration {i}");
            }
        });

        // Act
        var result = await engine.GeneratePlanAsync(
            sessionId,
            kernel,
            systemPrompt: "System",
            userPrompt: "Build feature",
            debugMode: false,
            autoApprove: false,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            CancellationToken.None);

        // Assert - Max revision attempts (5) exceeded returns null
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GeneratePlanAsync_WhenCancelledWhileWaitingForReview_ThrowsOperationCanceledExceptionAndDoesNotLeakSemaphore()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var planText = "## Goal\nValid Plan Waiting For Review";
        var kernel = CreateKernelWithMockResponse(planText);

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(0, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        using var cts = new CancellationTokenSource();

        // Trigger cancellation while in review wait
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null)
                    break;
            }
            cts.Cancel();
        });

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await engine.GeneratePlanAsync(
                sessionId,
                kernel,
                systemPrompt: "System",
                userPrompt: "Build feature",
                debugMode: false,
                autoApprove: false,
                executionSemaphore: semaphore,
                semaphoreState: semState,
                cts.Token);
        });

        // Verify semaphore state was cleared and not leaked
        Assert.IsFalse(semState.IsHeld);
    }

    [TestMethod]
    public async Task GeneratePlanAsync_WhenUpdateStateThrowsAndCancelled_DoesNotMaskOriginalException()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var planText = "## Goal\nValid Plan";
        var kernel = CreateKernelWithMockResponse(planText);
        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(0, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        using var cts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null)
                    break;
            }
            _taskOps.ApprovePlan(sessionId);
            _db.Dispose();
            cts.Cancel();
        });

        // Act & Assert: The original ObjectDisposedException should NOT be masked by OperationCanceledException
        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await engine.GeneratePlanAsync(
                sessionId,
                kernel,
                systemPrompt: "System",
                userPrompt: "Build feature",
                debugMode: false,
                autoApprove: false,
                executionSemaphore: semaphore,
                semaphoreState: semState,
                cts.Token);
        });

        Assert.IsNotNull(ex);
    }

    [TestMethod]
    public async Task GeneratePlanAsync_WhenSemaphoreDisposedWhileWaitingForReview_ThrowsOperationCanceledException()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var planText = "## Goal\nValid Plan Waiting For Review";
        var kernel = CreateKernelWithMockResponse(planText);

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(1, 1);
        await semaphore.WaitAsync(); // Simulate caller holding the slot
        var semState = new SemaphoreOwnershipState { IsHeld = true };

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                if (!semState.IsHeld)
                    break;
                await Task.Delay(10);
            }
            semaphore.Dispose();
            _taskOps.ApprovePlan(sessionId);
        });

        // Act & Assert - Disposal of semaphore must throw OperationCanceledException, not continue execution
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await engine.GeneratePlanAsync(
                sessionId,
                kernel,
                systemPrompt: "System",
                userPrompt: "Build feature",
                debugMode: false,
                autoApprove: false,
                executionSemaphore: semaphore,
                semaphoreState: semState,
                CancellationToken.None);
        });

        Assert.IsFalse(semState.IsHeld);
    }

    [TestMethod]
    public async Task GeneratePlanAsync_WhenSemaphoreNotHeld_DoesNotLeakPermits()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var planText = "## Goal\nBuild feature\n## Proposed Steps\n1. Step 1\n## Open Questions\nNone\n## Risks\nNone";
        var kernel = CreateKernelWithMockResponse(planText);

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(1, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = false };

        // Post user approval asynchronously
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null && plan.Version == 1)
                    break;
            }
            _taskOps.ApprovePlan(sessionId);
        });

        // Act
        var result = await engine.GeneratePlanAsync(
            sessionId,
            kernel,
            systemPrompt: "System",
            userPrompt: "Build feature",
            debugMode: false,
            autoApprove: false,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            CancellationToken.None);

        // Assert
        Assert.AreEqual(planText, result);
        Assert.AreEqual(1, semaphore.CurrentCount, "Semaphore permit must not be acquired if not released.");
        Assert.IsFalse(semState.IsHeld, "Semaphore state must remain unheld.");
    }

    [TestMethod]
    public async Task GeneratePlanAsync_WhenSemaphoreNotHeldAndCountZero_CompletesWithoutDeadlock()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _taskOps.RegisterTask(sessionId);
        _db.TaskSessions.Add(new TaskSession { Id = sessionId, AgentName = "TestAgent", UserPrompt = "Build feature" });
        await _db.SaveChangesAsync();

        var planText = "## Goal\nBuild feature\n## Proposed Steps\n1. Step 1\n## Open Questions\nNone\n## Risks\nNone";
        var kernel = CreateKernelWithMockResponse(planText);

        var engine = new PlanningEngine(_taskStore, _taskOps, _aiOptions, _sysOptions, _llmCallService, NullLogger<PlanningEngine>.Instance);
        var semaphore = new SemaphoreSlim(0, 1);
        var semState = new SemaphoreOwnershipState { IsHeld = false };

        // Post user approval asynchronously
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(50);
                var plan = await _taskStore.GetLatestPlanAsync(sessionId);
                if (plan != null && plan.Version == 1)
                    break;
            }
            _taskOps.ApprovePlan(sessionId);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Act
        var result = await engine.GeneratePlanAsync(
            sessionId,
            kernel,
            systemPrompt: "System",
            userPrompt: "Build feature",
            debugMode: false,
            autoApprove: false,
            executionSemaphore: semaphore,
            semaphoreState: semState,
            cts.Token);

        // Assert
        Assert.AreEqual(planText, result);
        Assert.AreEqual(0, semaphore.CurrentCount);
        Assert.IsFalse(semState.IsHeld);
    }

    private static Kernel CreateKernelWithMockResponse(string responseText)
    {
        var chatServiceMock = new Mock<IChatCompletionService>();
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(It.IsAny<ChatHistory>(), It.IsAny<PromptExecutionSettings>(), It.IsAny<Kernel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { new(AuthorRole.Assistant, responseText) });

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        return kernelBuilder.Build();
    }
}
