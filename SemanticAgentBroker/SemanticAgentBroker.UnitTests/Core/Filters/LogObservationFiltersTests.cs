using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Moq;
using SemanticAgentBroker.Core.Constants;
using SemanticAgentBroker.Core.Features.Agents;
using SemanticAgentBroker.Core.Filters;

namespace SemanticAgentBroker.UnitTests.Core.Filters;

[TestClass]
public class LogObservationFiltersTests
{
    private Mock<IAgentTaskStore> _taskStoreMock = null!;
    private Mock<IAgentTaskContext> _contextMock = null!;
    private Guid _taskId;

    [TestInitialize]
    public void Setup()
    {
        _taskId = Guid.NewGuid();
        _taskStoreMock = new Mock<IAgentTaskStore>();
        _contextMock = new Mock<IAgentTaskContext>();
        _contextMock.Setup(c => c.TaskId).Returns(_taskId);
    }

    [TestMethod]
    public async Task FunctionInvocationLoggerFilter_WithValidTaskId_LogsBeforeAndAfterInvocation()
    {
        // Arrange
        var filter = new FunctionInvocationLoggerFilter(_taskStoreMock.Object, _contextMock.Object);
        var kernel = new Kernel();
        kernel.FunctionInvocationFilters.Add(filter);

        kernel.Plugins.AddFromFunctions("MathPlugin", [
            KernelFunctionFactory.CreateFromMethod((int a, int b) => a + b, "Add")
        ]);

        var arguments = new KernelArguments { ["a"] = 5, ["b"] = 10 };

        // Act
        var result = await kernel.InvokeAsync("MathPlugin", "Add", arguments);

        // Assert
        Assert.AreEqual(15, result.GetValue<int>());
        _taskStoreMock.Verify(t => t.LogAsync(
            _taskId,
            LogLevelNames.Tool,
            It.Is<string>(msg => msg.Contains("MathPlugin.Add") && msg.Contains("a=5") && msg.Contains("b=10"))),
            Times.Once);

        _taskStoreMock.Verify(t => t.LogAsync(
            _taskId,
            LogLevelNames.System,
            It.Is<string>(msg => msg.Contains("Add returned: 15"))),
            Times.Once);
    }

    [TestMethod]
    public async Task FunctionInvocationLoggerFilter_WithEmptyTaskId_DoesNotLog()
    {
        // Arrange
        _contextMock.Setup(c => c.TaskId).Returns(Guid.Empty);
        var filter = new FunctionInvocationLoggerFilter(_taskStoreMock.Object, _contextMock.Object);

        var kernel = new Kernel();
        kernel.FunctionInvocationFilters.Add(filter);
        kernel.Plugins.AddFromFunctions("MathPlugin", [
            KernelFunctionFactory.CreateFromMethod(() => 42, "Answer")
        ]);

        // Act
        var result = await kernel.InvokeAsync("MathPlugin", "Answer");

        // Assert
        Assert.AreEqual(42, result.GetValue<int>());
        _taskStoreMock.Verify(t => t.LogAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task PromptRenderLoggerFilter_WithValidTaskId_LogsRenderedPrompt()
    {
        // Arrange
        var chatServiceMock = new Mock<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
        chatServiceMock.Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<Microsoft.SemanticKernel.ChatCompletion.ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Microsoft.SemanticKernel.ChatMessageContent(Microsoft.SemanticKernel.ChatCompletion.AuthorRole.Assistant, "OK")]);

        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(chatServiceMock.Object);
        var kernel = builder.Build();

        var filter = new PromptRenderLoggerFilter(_taskStoreMock.Object, _contextMock.Object);
        kernel.PromptRenderFilters.Add(filter);

        var promptFunction = KernelFunctionFactory.CreateFromPrompt("Hello {{$name}}!");

        // Act
        await promptFunction.InvokeAsync(kernel, new KernelArguments { ["name"] = "Alice" });

        // Assert
        _taskStoreMock.Verify(t => t.LogAsync(
            _taskId,
            LogLevelNames.Reasoning,
            It.Is<string>(msg => msg.Contains("Prompt Sent to LLM:") && msg.Contains("Hello Alice!"))),
            Times.Once);
    }

    [TestMethod]
    public async Task PromptRenderLoggerFilter_WithEmptyTaskId_DoesNotLog()
    {
        // Arrange
        _contextMock.Setup(c => c.TaskId).Returns(Guid.Empty);

        var chatServiceMock = new Mock<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
        chatServiceMock.Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<Microsoft.SemanticKernel.ChatCompletion.ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Microsoft.SemanticKernel.ChatMessageContent(Microsoft.SemanticKernel.ChatCompletion.AuthorRole.Assistant, "OK")]);

        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(chatServiceMock.Object);
        var kernel = builder.Build();

        var filter = new PromptRenderLoggerFilter(_taskStoreMock.Object, _contextMock.Object);
        kernel.PromptRenderFilters.Add(filter);

        var promptFunction = KernelFunctionFactory.CreateFromPrompt("Test prompt without task context");

        // Act
        await promptFunction.InvokeAsync(kernel);

        // Assert
        _taskStoreMock.Verify(t => t.LogAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task FunctionInvocationLoggerFilter_WhenFunctionThrows_LogsBeforeAndPropagatesException()
    {
        // Arrange
        var filter = new FunctionInvocationLoggerFilter(_taskStoreMock.Object, _contextMock.Object);
        var kernel = new Kernel();
        kernel.FunctionInvocationFilters.Add(filter);

        kernel.Plugins.AddFromFunctions("FailingPlugin", [
            KernelFunctionFactory.CreateFromMethod((Action)(() => throw new InvalidOperationException("Plugin exploded!")), "FailingMethod")
        ]);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            kernel.InvokeAsync("FailingPlugin", "FailingMethod"));

        // Verify initial invocation was still logged before throwing
        _taskStoreMock.Verify(t => t.LogAsync(
            _taskId,
            LogLevelNames.Tool,
            It.Is<string>(msg => msg.Contains("FailingPlugin.FailingMethod"))),
            Times.Once);
    }
}

