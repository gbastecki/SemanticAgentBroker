using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using SemanticAgentBroker.Client.Features.Shared;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class InteropServiceTests
{
    private Mock<IJSRuntime> _jsRuntimeMock = null!;
    private Mock<IJSObjectReference> _moduleMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _jsRuntimeMock = new Mock<IJSRuntime>();
        _moduleMock = new Mock<IJSObjectReference>();

        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.Is<object?[]>(args => (string?)args[0] == "/js/interop.js")))
            .ReturnsAsync(_moduleMock.Object);
    }

    [TestMethod]
    public async Task ScrollToBottomAsync_InvokesJsModuleMethod()
    {
        // Arrange
        var service = new InteropService(_jsRuntimeMock.Object);

        // Act
        await service.ScrollToBottomAsync("#chat-container");

        // Assert
        _moduleMock.Verify(m => m.InvokeAsync<IJSVoidResult>("scrollToBottom", It.Is<object?[]>(args => (string?)args[0] == "#chat-container")), Times.Once);
    }

    [TestMethod]
    public async Task CopyToClipboardAsync_WhenSuccessful_ReturnsTrue()
    {
        // Arrange
        _moduleMock
            .Setup(m => m.InvokeAsync<bool>("copyToClipboard", It.IsAny<object?[]>()))
            .ReturnsAsync(true);

        var service = new InteropService(_jsRuntimeMock.Object);

        // Act
        var result = await service.CopyToClipboardAsync("Hello World");

        // Assert
        Assert.IsTrue(result);
        _moduleMock.Verify(m => m.InvokeAsync<bool>("copyToClipboard", It.Is<object?[]>(args => (string?)args[0] == "Hello World")), Times.Once);
    }

    [TestMethod]
    public async Task CopyToClipboardAsync_WhenExceptionOccurs_ReturnsFalse()
    {
        // Arrange
        _moduleMock
            .Setup(m => m.InvokeAsync<bool>("copyToClipboard", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("Clipboard permission denied"));

        var service = new InteropService(_jsRuntimeMock.Object);

        // Act
        var result = await service.CopyToClipboardAsync("Sensitive data");

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task ConfirmAsync_InvokesWindowConfirm()
    {
        // Arrange
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<bool>("confirm", It.Is<object?[]>(args => (string?)args[0] == "Are you sure?")))
            .ReturnsAsync(true);

        var service = new InteropService(_jsRuntimeMock.Object);

        // Act
        var confirmed = await service.ConfirmAsync("Are you sure?");

        // Assert
        Assert.IsTrue(confirmed);
        _jsRuntimeMock.Verify(js => js.InvokeAsync<bool>("confirm", It.Is<object?[]>(args => (string?)args[0] == "Are you sure?")), Times.Once);
    }

    [TestMethod]
    public async Task AlertAsync_InvokesWindowAlert()
    {
        // Arrange
        var service = new InteropService(_jsRuntimeMock.Object);

        // Act
        await service.AlertAsync("Notice message");

        // Assert
        _jsRuntimeMock.Verify(js => js.InvokeAsync<IJSVoidResult>("alert", It.Is<object?[]>(args => (string?)args[0] == "Notice message")), Times.Once);
    }

    [TestMethod]
    public async Task SetThemeAsync_InvokesThemeManager()
    {
        // Arrange
        var service = new InteropService(_jsRuntimeMock.Object);

        // Act
        await service.SetThemeAsync("dark");

        // Assert
        _jsRuntimeMock.Verify(js => js.InvokeAsync<IJSVoidResult>("window.themeManager.setTheme", It.Is<object?[]>(args => (string?)args[0] == "dark")), Times.Once);
    }

    [TestMethod]
    public async Task GetThemeAsync_WhenThemeManagerReturnsDark_ReturnsDark()
    {
        // Arrange
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string>("window.themeManager.getTheme", It.IsAny<object?[]>()))
            .ReturnsAsync("dark");

        var service = new InteropService(_jsRuntimeMock.Object);

        // Act
        var theme = await service.GetThemeAsync();

        // Assert
        Assert.AreEqual("dark", theme);
    }

    [TestMethod]
    public async Task DisposeAsync_DisposesLoadedModuleCleanly()
    {
        // Arrange
        var service = new InteropService(_jsRuntimeMock.Object);
        await service.ScrollToBottomAsync("#dummy"); // Triggers module lazy load

        // Act
        await service.DisposeAsync();

        // Assert
        _moduleMock.Verify(m => m.DisposeAsync(), Times.Once);
    }

    [TestMethod]
    public async Task DisposeAsync_WhenModuleNotLoaded_DoesNotTriggerModuleImport()
    {
        // Arrange - fresh service without any interop method calls (module not loaded)
        var service = new InteropService(_jsRuntimeMock.Object);

        // Act
        await service.DisposeAsync();

        // Assert - import should never have been invoked because module was never used
        _jsRuntimeMock.Verify(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()), Times.Never);
    }

    [TestMethod]
    public async Task GetThemeAsync_WhenWindowThemeManagerFails_FallsBackToModuleAndDefault()
    {
        // Arrange - window.themeManager throws, module throws -> returns "light"
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string>("window.themeManager.getTheme", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("themeManager not found"));

        _moduleMock
            .Setup(m => m.InvokeAsync<string>("getTheme", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("module getTheme failed"));

        var service = new InteropService(_jsRuntimeMock.Object);

        // Act
        var theme = await service.GetThemeAsync();

        // Assert
        Assert.AreEqual("light", theme);
    }

    [TestMethod]
    public async Task SetThemeAsync_WhenWindowThemeManagerFails_FallsBackToModule()
    {
        // Arrange - window.themeManager throws, falls back to module.setTheme
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<IJSVoidResult>("window.themeManager.setTheme", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSException("window.themeManager undefined"));

        var service = new InteropService(_jsRuntimeMock.Object);

        // Act
        await service.SetThemeAsync("dark");

        // Assert
        _moduleMock.Verify(m => m.InvokeAsync<IJSVoidResult>("setTheme", It.Is<object?[]>(args => (string?)args[0] == "dark")), Times.Once);
    }

    [TestMethod]
    public async Task GetModule_WhenImportThrowsTransientError_RetriesOnSubsequentCall()
    {
        // Arrange
        int importCalls = 0;
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.Is<object?[]>(args => (string?)args[0] == "/js/interop.js")))
            .Returns(() =>
            {
                importCalls++;
                if (importCalls == 1)
                {
                    return ValueTask.FromException<IJSObjectReference>(new JSException("Failed to fetch dynamically imported module"));
                }
                return ValueTask.FromResult(_moduleMock.Object);
            });

        var service = new InteropService(_jsRuntimeMock.Object);

        // Act 1 - first call fails
        await Assert.ThrowsAsync<JSException>(async () =>
        {
            await service.ScrollToBottomAsync("#chat");
        });

        // Act 2 - second call should RETRY import and succeed
        await service.ScrollToBottomAsync("#chat");

        // Assert - import should have been called twice, not cached forever
        Assert.AreEqual(2, importCalls);
        _moduleMock.Verify(m => m.InvokeAsync<IJSVoidResult>("scrollToBottom", It.IsAny<object?[]>()), Times.Once);
    }
}
