using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Moq;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.ReAct.Services;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Api.Hubs;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.ReAct;

[TestClass]
public class LlmCallServiceTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private TestWorkspaceFixture _workspaceFixture = null!;
    private AppDbContext _db = null!;
    private AgentTaskStore _taskStore = null!;
    private LlmCallService _llmService = null!;
    private AiProviderOptions _aiOptions = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();
        _workspaceFixture = new TestWorkspaceFixture();
        _db = _dbFixture.CreateAppDbContext();

        var hubMock = new Mock<IHubContext<AgentLogHub>>();
        var clientsMock = new Mock<IHubClients>();
        var proxyMock = new Mock<IClientProxy>();
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);
        clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(proxyMock.Object);

        _taskStore = new AgentTaskStore(_db, hubMock.Object, _dbFixture.CreateSystemOptions());
        _aiOptions = new AiProviderOptions
        {
            DefaultTimeoutMinutes = 1,
            Inference = new InferenceOptions
            {
                MaxLlmRetries = 2,
                LlmRetryDelaySeconds = 1,
                MaxLlmRetryDelaySeconds = 1
            }
        };

        _llmService = new LlmCallService(
            _taskStore,
            _aiOptions,
            _dbFixture.CreateSystemOptions(),
            NullLogger<LlmCallService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _dbFixture.Dispose();
        _workspaceFixture.Dispose();
    }

    [TestMethod]
    public async Task CallLlmWithRetryAsync_SuccessfulCall_ReturnsContent()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var chatHistory = new ChatHistory("System prompt");
        chatHistory.AddUserMessage("Hello");

        var responseMessage = new ChatMessageContent(AuthorRole.Assistant, "Thought: Doing task\nAction: Tool-Exec\nActionInput: {}");
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                chatHistory,
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { responseMessage });

        var kernel = new Kernel();
        var settings = new PromptExecutionSettings();
        var sessionId = Guid.NewGuid();

        // Act
        var (content, toolCalls, wasTruncated) = await _llmService.CallLlmWithRetryAsync(
            chatServiceMock.Object,
            chatHistory,
            settings,
            kernel,
            sessionId,
            step: 1,
            tracePath: null,
            traceMode: false,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert
        Assert.IsFalse(wasTruncated);
        StringAssert.Contains(content, "Thought: Doing task");
    }

    [TestMethod]
    public async Task CallLlmWithRetryAsync_TransientHttpException_RetriesAndSucceeds()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var chatHistory = new ChatHistory("System");
        chatHistory.AddUserMessage("Hello");

        int callCount = 0;
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                chatHistory,
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new HttpRequestException("503 Service Unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable);

                return Task.FromResult<IReadOnlyList<ChatMessageContent>>(
                    new List<ChatMessageContent> { new(AuthorRole.Assistant, "Success after retry") });
            });

        var kernel = new Kernel();
        var settings = new PromptExecutionSettings();
        var sessionId = Guid.NewGuid();

        // Act
        var (content, toolCalls, wasTruncated) = await _llmService.CallLlmWithRetryAsync(
            chatServiceMock.Object,
            chatHistory,
            settings,
            kernel,
            sessionId,
            step: 1,
            tracePath: null,
            traceMode: false,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual(2, callCount);
        Assert.AreEqual("Success after retry", content);
    }

    [TestMethod]
    public async Task CallLlmWithRetryAsync_ExceedingMaxRetries_ThrowsInvalidOperationException()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var chatHistory = new ChatHistory("System");
        chatHistory.AddUserMessage("Hello");

        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                chatHistory,
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("500 Internal Server Error", null, System.Net.HttpStatusCode.InternalServerError));

        var kernel = new Kernel();
        var settings = new PromptExecutionSettings();
        var sessionId = Guid.NewGuid();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _llmService.CallLlmWithRetryAsync(
                chatServiceMock.Object,
                chatHistory,
                settings,
                kernel,
                sessionId,
                step: 1,
                tracePath: null,
                traceMode: false,
                debugMode: false,
                ct: CancellationToken.None);
        });
    }

    [TestMethod]
    public async Task CallLlmWithRetryAsync_NativeFunctionCalls_ExtractedIntoToolCalls()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var chatHistory = new ChatHistory("System");
        chatHistory.AddUserMessage("Query");

        var responseMessage = new ChatMessageContent(AuthorRole.Assistant, "Calling native function");
        responseMessage.Items.Add(new FunctionCallContent("RunSearch", "SearchPlugin", "call_1", new KernelArguments { ["q"] = "test" }));

        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                chatHistory,
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { responseMessage });

        var kernel = new Kernel();
        var settings = new PromptExecutionSettings();
        var sessionId = Guid.NewGuid();

        // Act
        var (content, toolCalls, wasTruncated) = await _llmService.CallLlmWithRetryAsync(
            chatServiceMock.Object,
            chatHistory,
            settings,
            kernel,
            sessionId,
            step: 1,
            tracePath: null,
            traceMode: false,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("Calling native function", content);
        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("SearchPlugin-RunSearch", toolCalls[0].ActionName);
        StringAssert.Contains(toolCalls[0].ActionInput, "test");
    }

    [TestMethod]
    public async Task CallLlmWithRetryAsync_TraceModeEnabled_WritesTraceToFile()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var chatHistory = new ChatHistory("System");
        chatHistory.AddUserMessage("Hello");

        var responseMessage = new ChatMessageContent(AuthorRole.Assistant, "Trace response content");
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                chatHistory,
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { responseMessage });

        var kernel = new Kernel();
        var settings = new PromptExecutionSettings();
        var sessionId = Guid.NewGuid();
        var tracePath = Path.Combine(_workspaceFixture.WorkspacePath, "trace.log");

        // Act
        var (content, toolCalls, wasTruncated) = await _llmService.CallLlmWithRetryAsync(
            chatServiceMock.Object,
            chatHistory,
            settings,
            kernel,
            sessionId,
            step: 1,
            tracePath: tracePath,
            traceMode: true,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert
        Assert.IsTrue(File.Exists(tracePath));
        var traceContent = await File.ReadAllTextAsync(tracePath);
        StringAssert.Contains(traceContent, "Trace response content");
    }

    [TestMethod]
    public async Task CallLlmWithRetryAsync_FinishReasonLength_SetsWasTruncatedTrue()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var chatHistory = new ChatHistory("System");
        chatHistory.AddUserMessage("Hello");

        var responseMessage = new ChatMessageContent(
            AuthorRole.Assistant,
            "Truncated reasoning...",
            metadata: new Dictionary<string, object?> { ["FinishReason"] = "length" });

        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                chatHistory,
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { responseMessage });

        var kernel = new Kernel();
        var settings = new PromptExecutionSettings();
        var sessionId = Guid.NewGuid();

        // Act
        var (content, toolCalls, wasTruncated) = await _llmService.CallLlmWithRetryAsync(
            chatServiceMock.Object,
            chatHistory,
            settings,
            kernel,
            sessionId,
            step: 1,
            tracePath: null,
            traceMode: false,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert
        Assert.IsTrue(wasTruncated);
    }

    [TestMethod]
    public async Task CallLlmWithRetryAsync_FinishReasonMaxTokens_SetsWasTruncatedTrue()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var chatHistory = new ChatHistory("System");
        chatHistory.AddUserMessage("Hello");

        var responseMessage = new ChatMessageContent(
            AuthorRole.Assistant,
            "Truncated reasoning tokens...",
            metadata: new Dictionary<string, object?> { ["FinishReason"] = "max_tokens" });

        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                chatHistory,
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { responseMessage });

        var kernel = new Kernel();
        var settings = new PromptExecutionSettings();
        var sessionId = Guid.NewGuid();

        // Act
        var (content, toolCalls, wasTruncated) = await _llmService.CallLlmWithRetryAsync(
            chatServiceMock.Object,
            chatHistory,
            settings,
            kernel,
            sessionId,
            step: 1,
            tracePath: null,
            traceMode: false,
            debugMode: false,
            ct: CancellationToken.None);

        // Assert
        Assert.IsTrue(wasTruncated);
    }

    [TestMethod]
    public async Task CallLlmWithRetryAsync_NonTransientHttp400_FailsImmediatelyWithoutRetry()
    {
        // Arrange
        var chatServiceMock = new Mock<IChatCompletionService>();
        var chatHistory = new ChatHistory("System");
        chatHistory.AddUserMessage("Query");

        int callCount = 0;
        chatServiceMock
            .Setup(c => c.GetChatMessageContentsAsync(
                chatHistory,
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                throw new HttpRequestException("400 Bad Request", null, System.Net.HttpStatusCode.BadRequest);
            });

        var kernel = new Kernel();
        var settings = new PromptExecutionSettings();
        var sessionId = Guid.NewGuid();

        // Act & Assert - Permanent 400 must fail immediately without exhausting retries
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _llmService.CallLlmWithRetryAsync(
                chatServiceMock.Object,
                chatHistory,
                settings,
                kernel,
                sessionId,
                step: 1,
                tracePath: null,
                traceMode: false,
                debugMode: false,
                ct: CancellationToken.None);
        });

        Assert.AreEqual(1, callCount, "Non-transient HTTP 400 error should not trigger retries.");
    }
}
