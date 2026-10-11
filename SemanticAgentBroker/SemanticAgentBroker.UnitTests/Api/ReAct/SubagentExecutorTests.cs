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
using SemanticAgentBroker.Core.Features.TokenCounting.Implementations;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.ReAct;

[TestClass]
public class SubagentExecutorTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private AppDbContext _db = null!;
    private AgentTaskStore _taskStore = null!;
    private Mock<IHubContext<AgentLogHub>> _hubMock = null!;
    private Mock<IClientProxy> _clientProxyMock = null!;
    private LlmCallService _llmService = null!;
    private AiProviderOptions _aiOptions = null!;
    private AgentSystemOptions _sysOptions = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();
        _db = _dbFixture.CreateAppDbContext();
        _sysOptions = _dbFixture.CreateSystemOptions();

        _hubMock = new Mock<IHubContext<AgentLogHub>>();
        var clientsMock = new Mock<IHubClients>();
        _clientProxyMock = new Mock<IClientProxy>();
        _hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);
        clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxyMock.Object);

        _taskStore = new AgentTaskStore(_db, _hubMock.Object, _sysOptions);
        _aiOptions = new AiProviderOptions
        {
            DefaultTimeoutMinutes = 1,
            Inference = new InferenceOptions
            {
                MaxLlmRetries = 1,
                LlmRetryDelaySeconds = 1,
                MaxLlmRetryDelaySeconds = 2
            }
        };

        _llmService = new LlmCallService(
            _taskStore,
            _aiOptions,
            _sysOptions,
            NullLogger<LlmCallService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _dbFixture.Dispose();
    }

    [TestMethod]
    public async Task ExecuteStepAsync_WithThoughtAndToolAction_ParsesAndBroadcastsThought()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var responseText = """
            Thought: I need to read the dataset file.
            Plan: Read file and summarize.
            Action: FileIO-ReadFromWorkspace
            ActionInput: {"filename": "data.csv"}
            """;

        var responseMessage = new ChatMessageContent(AuthorRole.Assistant, responseText);
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { responseMessage });

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var executor = new SubagentExecutor(
            _llmService,
            _aiOptions,
            new MlTokenCounter(_aiOptions, NullLogger<MlTokenCounter>.Instance),
            _taskStore,
            NullLogger<SubagentExecutor>.Instance);

        var sessionId = Guid.NewGuid();

        // Act
        var result = await executor.ExecuteStepAsync(
            sessionId,
            kernel,
            systemPrompt: "You are an agent",
            stateInjection: "Task State",
            step: 1,
            maxContextTokens: 4096,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert
        Assert.IsFalse(result.IsError);
        Assert.AreEqual("I need to read the dataset file.", result.Thought);
        Assert.AreEqual(1, result.Actions.Count);
        Assert.AreEqual("FileIO-ReadFromWorkspace", result.Actions[0].ActionName);
        StringAssert.Contains(result.Actions[0].ActionInput, "data.csv");
        Assert.IsNull(result.FinalAnswer);

        // Verify broadcast occurred for thought
        _clientProxyMock.Verify(
            c => c.SendCoreAsync(
                HubMethodNames.AgentThought,
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [TestMethod]
    public async Task ExecuteStepAsync_WithFinalAnswer_ReturnsFinalAnswerResult()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var responseText = """
            Thought: Task is complete and verified.
            FinalAnswer: The computation result is 42.
            """;

        var responseMessage = new ChatMessageContent(AuthorRole.Assistant, responseText);
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { responseMessage });

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var executor = new SubagentExecutor(
            _llmService,
            _aiOptions,
            new MlTokenCounter(_aiOptions, NullLogger<MlTokenCounter>.Instance),
            _taskStore,
            NullLogger<SubagentExecutor>.Instance);

        var sessionId = Guid.NewGuid();

        // Act
        var result = await executor.ExecuteStepAsync(
            sessionId,
            kernel,
            systemPrompt: "You are an agent",
            stateInjection: "Task State",
            step: 1,
            maxContextTokens: 4096,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert
        Assert.IsFalse(result.IsError);
        Assert.AreEqual("The computation result is 42.", result.FinalAnswer);
        Assert.AreEqual(0, result.Actions.Count);
    }

    [TestMethod]
    public async Task ExecuteStepAsync_WithTruncatedInitialReasoning_RetriesWithNudgeAndSucceeds()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var truncatedMessage = new ChatMessageContent(
            AuthorRole.Assistant,
            "",
            metadata: new Dictionary<string, object?> { ["FinishReason"] = "length" });

        var successMessage = new ChatMessageContent(
            AuthorRole.Assistant,
            """
            Thought: Executing tool now.
            Action: FileIO-ReadFromWorkspace
            ActionInput: {"filename": "data.csv"}
            """);

        chatServiceMock
            .SetupSequence(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { truncatedMessage })
            .ReturnsAsync(new List<ChatMessageContent> { successMessage });

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var executor = new SubagentExecutor(
            _llmService,
            _aiOptions,
            new MlTokenCounter(_aiOptions, NullLogger<MlTokenCounter>.Instance),
            _taskStore,
            NullLogger<SubagentExecutor>.Instance);

        var sessionId = Guid.NewGuid();

        // Act
        var result = await executor.ExecuteStepAsync(
            sessionId,
            kernel,
            systemPrompt: "You are an agent",
            stateInjection: "Task State",
            step: 1,
            maxContextTokens: 4096,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert - should recover and succeed via nudged retry
        Assert.IsFalse(result.IsError);
        Assert.AreEqual("Executing tool now.", result.Thought);
        Assert.AreEqual(1, result.Actions.Count);
        Assert.AreEqual("FileIO-ReadFromWorkspace", result.Actions[0].ActionName);
    }

    [TestMethod]
    public async Task ExecuteStepAsync_WithAllReasoningTruncated_FallsBackToSingleCall()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var truncatedMessage = new ChatMessageContent(
            AuthorRole.Assistant,
            "",
            metadata: new Dictionary<string, object?> { ["FinishReason"] = "length" });

        var fallbackMessage = new ChatMessageContent(
            AuthorRole.Assistant,
            """
            FinalAnswer: Completed via fallback.
            """);

        chatServiceMock
            .SetupSequence(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { truncatedMessage })  // 1st reasoning call
            .ReturnsAsync(new List<ChatMessageContent> { truncatedMessage })  // 2nd nudged reasoning call
            .ReturnsAsync(new List<ChatMessageContent> { fallbackMessage });  // single-call fallback

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var executor = new SubagentExecutor(
            _llmService,
            _aiOptions,
            new MlTokenCounter(_aiOptions, NullLogger<MlTokenCounter>.Instance),
            _taskStore,
            NullLogger<SubagentExecutor>.Instance);

        var sessionId = Guid.NewGuid();

        // Act
        var result = await executor.ExecuteStepAsync(
            sessionId,
            kernel,
            systemPrompt: "You are an agent",
            stateInjection: "Task State",
            step: 1,
            maxContextTokens: 4096,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert - should recover and complete via fallback
        Assert.IsFalse(result.IsError);
        Assert.AreEqual("Completed via fallback.", result.FinalAnswer);
    }

    [TestMethod]
    public async Task ExecuteStepAsync_WithMalformedJsonInActionInput_RepairsViaSanitizer()
    {
        // Arrange - trailing comma in JSON
        var chatServiceMock = new Mock<IChatCompletionService>();
        var responseText = """
            Thought: Executing tool with trailing comma JSON
            Action: FileIO-Write
            ActionInput: {"file": "test.txt", "data": "123",}
            """;

        var responseMessage = new ChatMessageContent(AuthorRole.Assistant, responseText);
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { responseMessage });

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var executor = new SubagentExecutor(
            _llmService,
            _aiOptions,
            new MlTokenCounter(_aiOptions, NullLogger<MlTokenCounter>.Instance),
            _taskStore,
            NullLogger<SubagentExecutor>.Instance);

        var sessionId = Guid.NewGuid();

        // Act
        var result = await executor.ExecuteStepAsync(
            sessionId,
            kernel,
            systemPrompt: "You are an agent",
            stateInjection: "Task State",
            step: 1,
            maxContextTokens: 4096,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert
        Assert.IsFalse(result.IsError);
        Assert.AreEqual(1, result.Actions.Count);
        Assert.IsTrue(JsonSanitizer.IsValidJson(result.Actions[0].ActionInput));
    }

    [TestMethod]
    public async Task ExecuteStepAsync_WhenLlmThrowsFatalException_ReturnsSubagentErrorResult()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Fatal API failure"));

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var executor = new SubagentExecutor(
            _llmService,
            _aiOptions,
            new MlTokenCounter(_aiOptions, NullLogger<MlTokenCounter>.Instance),
            _taskStore,
            NullLogger<SubagentExecutor>.Instance);

        var sessionId = Guid.NewGuid();

        // Act
        var result = await executor.ExecuteStepAsync(
            sessionId,
            kernel,
            systemPrompt: "You are an agent",
            stateInjection: "Task State",
            step: 1,
            maxContextTokens: 4096,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert - Returns error state gracefully without crashing
        Assert.IsTrue(result.IsError);
        Assert.IsNotNull(result.ErrorMessage);
        StringAssert.Contains(result.ErrorMessage, "Fatal API failure");
    }

    [TestMethod]
    public async Task ExecuteStepAsync_WithActionAndFinalAnswer_PrioritizesActionAndDefersFinalAnswer()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var responseContent = "Thought: I will write to file.\nAction: FileIO-SaveToWorkspace\nActionInput: {\"path\": \"a.txt\"}\nFinalAnswer: I have written the file.";

        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { new(AuthorRole.Assistant, responseContent) });

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var executor = new SubagentExecutor(
            _llmService,
            _aiOptions,
            new MlTokenCounter(_aiOptions, NullLogger<MlTokenCounter>.Instance),
            _taskStore,
            NullLogger<SubagentExecutor>.Instance);

        var sessionId = Guid.NewGuid();

        // Act
        var result = await executor.ExecuteStepAsync(
            sessionId,
            kernel,
            systemPrompt: "You are an agent",
            stateInjection: "Task State",
            step: 1,
            maxContextTokens: 4096,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert - Actions must be executed first, FinalAnswer deferred
        Assert.IsFalse(result.IsError);
        Assert.AreEqual(1, result.Actions.Count);
        Assert.AreEqual("FileIO-SaveToWorkspace", result.Actions[0].ActionName);
        Assert.IsNull(result.FinalAnswer, "FinalAnswer must be deferred until action observations are collected.");
    }

    [TestMethod]
    public async Task ExecuteStepAsync_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel token

        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var executor = new SubagentExecutor(
            _llmService,
            _aiOptions,
            new MlTokenCounter(_aiOptions, NullLogger<MlTokenCounter>.Instance),
            _taskStore,
            NullLogger<SubagentExecutor>.Instance);

        var sessionId = Guid.NewGuid();

        // Act & Assert - Must throw OperationCanceledException instead of falling back or returning IsError
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await executor.ExecuteStepAsync(
                sessionId,
                kernel,
                systemPrompt: "You are an agent",
                stateInjection: "Task State",
                step: 1,
                maxContextTokens: 4096,
                debugMode: false,
                ct: cts.Token);
        });
    }

    [TestMethod]
    public async Task ExecuteStepAsync_WithAllReasoningTruncated_FallsBackToSingleCall_ExtractsTextActionPairs()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var truncatedMessage = new ChatMessageContent(
            AuthorRole.Assistant,
            "",
            metadata: new Dictionary<string, object?> { ["FinishReason"] = "length" });

        var fallbackMessage = new ChatMessageContent(
            AuthorRole.Assistant,
            """
            Thought: Executing tool via text fallback.
            Plan: Call data tool.
            Action: MockData-Query
            ActionInput: {"query": "SELECT 1"}
            """);

        chatServiceMock
            .SetupSequence(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { truncatedMessage })  // 1st reasoning call
            .ReturnsAsync(new List<ChatMessageContent> { truncatedMessage })  // 2nd nudged reasoning call
            .ReturnsAsync(new List<ChatMessageContent> { fallbackMessage });  // single-call fallback

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(chatServiceMock.Object);
        var kernel = kernelBuilder.Build();

        var executor = new SubagentExecutor(
            _llmService,
            _aiOptions,
            new MlTokenCounter(_aiOptions, NullLogger<MlTokenCounter>.Instance),
            _taskStore,
            NullLogger<SubagentExecutor>.Instance);

        var sessionId = Guid.NewGuid();

        // Act
        var result = await executor.ExecuteStepAsync(
            sessionId,
            kernel,
            systemPrompt: "You are an agent",
            stateInjection: "Task State",
            step: 1,
            maxContextTokens: 4096,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert - should extract text-based Action/ActionInput pairs
        Assert.IsFalse(result.IsError);
        Assert.AreEqual(1, result.Actions.Count);
        Assert.AreEqual("MockData-Query", result.Actions[0].ActionName);
        Assert.AreEqual("{\"query\": \"SELECT 1\"}", result.Actions[0].ActionInput);
        Assert.AreEqual("Call data tool.", result.Plan);
    }

    [TestMethod]
    public async Task LlmRepairJsonAsync_WhenCancellationRequested_RethrowsOperationCanceledException()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancellation already requested

        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var kernel = new Kernel();
        var executor = new SubagentExecutor(
            _llmService,
            _aiOptions,
            new MlTokenCounter(_aiOptions, NullLogger<MlTokenCounter>.Instance),
            _taskStore,
            NullLogger<SubagentExecutor>.Instance);

        // Act & Assert - M3: Must rethrow OperationCanceledException when ct is canceled
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await executor.LlmRepairJsonAsync(
                chatServiceMock.Object,
                kernel,
                "{ broken: json",
                Guid.NewGuid(),
                step: 1,
                debugMode: false,
                ct: cts.Token);
        });
    }

    [TestMethod]
    public async Task LlmRepairJsonAsync_WhenGenericExceptionOccurs_ReturnsOriginalMalformedJson()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API down"));

        var kernel = new Kernel();
        var executor = new SubagentExecutor(
            _llmService,
            _aiOptions,
            new MlTokenCounter(_aiOptions, NullLogger<MlTokenCounter>.Instance),
            _taskStore,
            NullLogger<SubagentExecutor>.Instance);

        const string rawJson = "{ invalid: json";

        // Act
        var result = await executor.LlmRepairJsonAsync(
            chatServiceMock.Object,
            kernel,
            rawJson,
            Guid.NewGuid(),
            step: 1,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert - Generic exceptions must be caught and return original rawJson
        Assert.AreEqual(rawJson, result);
    }
}
