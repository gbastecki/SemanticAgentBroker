using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using SemanticAgentBroker.Client.Features.Shared;
using SemanticAgentBroker.Client.Pages;
using System.Reflection;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class ClientFileUploadGuardTests
{
    private static InputFileChangeEventArgs CreateEventArgsWithFileCount(int count)
    {
        var files = new List<IBrowserFile>();
        for (int i = 0; i < count; i++)
        {
            var mockFile = new Mock<IBrowserFile>();
            mockFile.Setup(f => f.Name).Returns($"file_{i}.txt");
            mockFile.Setup(f => f.Size).Returns(100);
            mockFile.Setup(f => f.ContentType).Returns("text/plain");
            files.Add(mockFile.Object);
        }

        return new InputFileChangeEventArgs(files);
    }

    [TestMethod]
    public async Task Home_HandleFilesSelected_WhenMoreThan10FilesSelected_DoesNotThrowAndSetsErrorMessage()
    {
        // Arrange
        var home = new Home();
        var eventArgs = CreateEventArgsWithFileCount(11);

        var handleMethod = typeof(Home).GetMethod("HandleFilesSelected", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.IsNotNull(handleMethod, "HandleFilesSelected method must exist on Home");

        // Act
        var task = (Task)handleMethod.Invoke(home, new object[] { eventArgs })!;
        await task;

        // Assert
        var errorField = typeof(Home).GetField("errorMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var errorMessage = (string?)errorField.GetValue(home);

        Assert.IsNotNull(errorMessage, "Error message should be set when selecting more than 10 files.");
        StringAssert.Contains(errorMessage, "10");
    }

    [TestMethod]
    public async Task LiveExecution_HandleFileUpload_WhenMoreThan10FilesSelected_DoesNotThrowAndAlertsUser()
    {
        // Arrange
        var liveExecution = new LiveExecution();
        var mockJs = new Mock<IJSRuntime>();
        string? alertedMessage = null;
        mockJs.Setup(j => j.InvokeAsync<IJSVoidResult>(
            It.Is<string>(s => s == "alert"),
            It.IsAny<object?[]>()
        )).Callback<string, object?[]>((method, args) =>
        {
            if (args.Length > 0 && args[0] is string msg)
            {
                alertedMessage = msg;
            }
        }).Returns(ValueTask.FromResult(Mock.Of<IJSVoidResult>()));

        var interopService = new InteropService(mockJs.Object);
        var interopProp = typeof(LiveExecution).GetProperty("InteropService", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        interopProp.SetValue(liveExecution, interopService);

        var eventArgs = CreateEventArgsWithFileCount(11);
        var handleMethod = typeof(LiveExecution).GetMethod("HandleFileUpload", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.IsNotNull(handleMethod, "HandleFileUpload method must exist on LiveExecution");

        // Act
        var task = (Task)handleMethod.Invoke(liveExecution, new object[] { eventArgs })!;
        await task;

        // Assert
        Assert.IsNotNull(alertedMessage, "User alert should be triggered when selecting more than 10 files.");
        StringAssert.Contains(alertedMessage, "10");
    }
}
