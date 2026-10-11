using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Moq;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.ReAct.Services;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Api.Hubs;
using SemanticAgentBroker.Core.Features.Agents.Implementations;
using SemanticAgentBroker.Core.Features.TaskState;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.ReAct;

[TestClass]
public class ToolExecutionServiceTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private TestWorkspaceFixture _workspaceFixture = null!;
    private AppDbContext _db = null!;
    private Mock<Microsoft.AspNetCore.SignalR.IHubContext<AgentLogHub>> _hubMock = null!;
    private AgentTaskStore _taskStore = null!;
    private Mock<ITaskStateManager> _stateManagerMock = null!;
    private AgentSystemOptions _sysOptions = null!;
    private AiProviderOptions _aiOptions = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();
        _workspaceFixture = new TestWorkspaceFixture();
        _db = _dbFixture.CreateAppDbContext();

        _hubMock = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<AgentLogHub>>();
        var clientProxyMock = new Mock<Microsoft.AspNetCore.SignalR.IClientProxy>();
        var hubClientsMock = new Mock<Microsoft.AspNetCore.SignalR.IHubClients>();
        _hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
        hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxyMock.Object);

        _sysOptions = _dbFixture.CreateSystemOptions();
        _sysOptions.TaskWorkspacesPath = Path.GetDirectoryName(_workspaceFixture.WorkspacePath)!;
        _aiOptions = new AiProviderOptions();

        _taskStore = new AgentTaskStore(_db, _hubMock.Object, _sysOptions);
        _stateManagerMock = new Mock<ITaskStateManager>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _dbFixture.Dispose();
        _workspaceFixture.Dispose();
    }

    private ToolExecutionService CreateService()
    {
        return new ToolExecutionService(
            _taskStore,
            _stateManagerMock.Object,
            _aiOptions,
            _sysOptions,
            NullLogger<ToolExecutionService>.Instance);
    }

    [TestMethod]
    public void IsDangerousAction_WithExplicitIsDangerousMetadata_ReturnsExpected()
    {
        // Arrange
        var kernel = new Kernel();
        var dangerousFunc = CreateFunctionWithSafety("DangerousMethod", true);
        var safeFunc = CreateFunctionWithSafety("SafeMethod", false);
        var unannotatedFunc = CreateFunctionWithSafety("UnannotatedMethod", null);

        kernel.Plugins.AddFromFunctions("TestPlugin", [dangerousFunc, safeFunc, unannotatedFunc]);

        // Act & Assert
        Assert.IsTrue(ToolExecutionService.IsDangerousAction(kernel, "TestPlugin-DangerousMethod"));
        Assert.IsFalse(ToolExecutionService.IsDangerousAction(kernel, "TestPlugin-SafeMethod"));
        Assert.IsFalse(ToolExecutionService.IsDangerousAction(kernel, "TestPlugin-UnannotatedMethod"));
    }

    private static KernelFunction CreateFunctionWithSafety(string name, bool? isDangerous)
    {
        var func = KernelFunctionFactory.CreateFromMethod(() => "ok", name);
        if (isDangerous.HasValue)
        {
            var props = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?> { ["IsDangerous"] = isDangerous.Value });
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
        }
        return func;
    }

    [TestMethod]
    public void IsDangerousAction_WithUnregisteredOrMalformedAction_ReturnsFalse()
    {
        // Arrange - empty kernel
        var kernel = new Kernel();

        // Act & Assert - unregistered and malformed actions must not fail-closed as dangerous so they don't block Auto mode
        Assert.IsFalse(ToolExecutionService.IsDangerousAction(kernel, "PythonSandbox-ExecutePython"));
        Assert.IsFalse(ToolExecutionService.IsDangerousAction(kernel, "UnknownPlugin-UnknownMethod"));
        Assert.IsFalse(ToolExecutionService.IsDangerousAction(kernel, "MalformedActionWithoutHyphen"));
    }

    [TestMethod]
    public void IsDangerousAction_WithBackticksInActionName_ResolvesDangerousStatusCorrectly()
    {
        // Arrange
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("FileIO", [
            KernelFunctionFactory.CreateFromMethod(() => "read", "ReadFromWorkspace")
        ]);

        // Act
        bool isDangerous = ToolExecutionService.IsDangerousAction(kernel, "`FileIO-ReadFromWorkspace`");

        // Assert - non-dangerous action wrapped in backticks should resolve to false, not fail closed due to backtick in plugin name
        Assert.IsFalse(isDangerous);
    }

    [TestMethod]
    public async Task ExecuteToolAsync_WithBackticksInActionName_ExecutesToolSuccessfully()
    {
        // Arrange
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod(() => "success_result", "TestFunction")
        ]);

        var service = CreateService();

        // Act
        var result = await service.ExecuteToolAsync(
            sessionId: Guid.NewGuid(),
            kernel: kernel,
            actionName: "`TestPlugin-TestFunction`",
            actionInputStr: "{}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("success_result", result);
    }

    [TestMethod]
    public async Task ExecuteToolAsync_WithMalformedActionFormat_ReturnsClearErrorMessage()
    {
        // Arrange
        var service = CreateService();
        var kernel = new Kernel();

        // Act
        var result = await service.ExecuteToolAsync(
            Guid.NewGuid(),
            kernel,
            actionName: "InvalidActionNoHyphen",
            actionInputStr: "{}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Invalid action format 'InvalidActionNoHyphen'.");
    }

    [TestMethod]
    public async Task ExecuteToolAsync_WithWorkdirParameterAutoLoad_LoadsFileContentFromDisk()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        var filePath = Path.Combine(_workspaceFixture.WorkspacePath, "input.txt");
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        await File.WriteAllTextAsync(filePath, "Loaded file content from disk");

        string? receivedParam = null;
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod((string text) => { receivedParam = text; return "processed"; }, "ProcessText")
        ]);

        var service = CreateService();

        // Act
        var result = await service.ExecuteToolAsync(
            sessionId,
            kernel,
            actionName: "TestPlugin-ProcessText",
            actionInputStr: "{\"text\":\"@/app/workdir/input.txt\"}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("processed", result);
        Assert.AreEqual("Loaded file content from disk", receivedParam);
    }

    [TestMethod]
    public async Task ExecuteToolAsync_WithWorkdirPathTraversal_DoesNotLoadTraversalFile()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        string? receivedParam = null;
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod((string text) => { receivedParam = text; return "processed"; }, "ProcessText")
        ]);

        var service = CreateService();

        // Act
        var result = await service.ExecuteToolAsync(
            sessionId,
            kernel,
            actionName: "TestPlugin-ProcessText",
            actionInputStr: "{\"text\":\"@/app/workdir/../secret.txt\"}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert - Path traversal rejected; raw parameter kept
        Assert.AreEqual("processed", result);
        Assert.AreEqual("@/app/workdir/../secret.txt", receivedParam);
    }

    [TestMethod]
    public async Task ExecuteToolAsync_LargeOutput_AutoSavesToWorkspaceFile()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        _aiOptions.AutoSaveToolOutputThreshold = 100;

        var largeString = new string('A', 500);
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod(() => largeString, "GetLargeData")
        ]);

        var service = CreateService();

        // Act
        var result = await service.ExecuteToolAsync(
            sessionId,
            kernel,
            actionName: "TestPlugin-GetLargeData",
            actionInputStr: "{}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert - Result was truncated and saved to a workspace file
        StringAssert.Contains(result, "Tool output was very large (500 chars) and has been auto-saved to /app/workdir/");
        StringAssert.Contains(result, "--- Output Preview ---");
    }

    [TestMethod]
    public void Truncate_ShortensLongStringsWithEllipsis()
    {
        // Arrange
        string text = "This is a long string that should be truncated.";

        // Act
        string truncated = ToolExecutionService.Truncate(text, 10);

        // Assert
        Assert.AreEqual("This is a …", truncated);
    }

    [TestMethod]
    public async Task ExecuteToolAsync_WhenToolThrowsArgumentException_ReturnsRetryGuidanceMessage()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("ValidationPlugin", [
            KernelFunctionFactory.CreateFromMethod(() =>
            {
                throw new ArgumentException("Parameter 'target' is required and cannot be empty.");
            }, "ValidateInput")
        ]);

        var service = CreateService();

        // Act
        var result = await service.ExecuteToolAsync(
            sessionId,
            kernel,
            actionName: "ValidationPlugin-ValidateInput",
            actionInputStr: "{}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Error: Parameter 'target' is required");
        StringAssert.Contains(result, "Please retry this tool call with all required parameters.");
    }

    [TestMethod]
    public void SafeTruncateIndex_SurrogatePairBoundary_DoesNotSplitEmoji()
    {
        // Arrange: Method via reflection to test internal helper directly
        var safeTruncateMethod = typeof(ToolExecutionService).GetMethod("SafeTruncateIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(safeTruncateMethod, "SafeTruncateIndex should exist on ToolExecutionService");

        // String with emoji (surrogate pair: high surrogate \uD83D, low surrogate \uDE00) at index 5 and 6
        string text = "Hello😀World"; // "Hello" is indices 0-4 (5 chars), '😀' is indices 5-6 (2 chars), "World" is 7-11

        // Act 1: Truncating exactly at index 6 (middle of the surrogate pair)
        int safeIndexAt6 = (int)safeTruncateMethod.Invoke(null, [text, 6])!;

        // Act 2: Truncating at index 5 (before surrogate pair)
        int safeIndexAt5 = (int)safeTruncateMethod.Invoke(null, [text, 5])!;

        // Assert: Index 6 should step back to 5 so high surrogate \uD83D is not orphaned
        Assert.AreEqual(5, safeIndexAt6);
        Assert.AreEqual(5, safeIndexAt5);
        Assert.AreEqual("Hello", text[..safeIndexAt6]);
    }

    [TestMethod]
    public async Task ExecuteToolAsync_WithNonExistentPlugin_ReturnsClearErrorMessage()
    {
        // Arrange
        var service = CreateService();
        var kernel = new Kernel();

        // Act
        var result = await service.ExecuteToolAsync(
            Guid.NewGuid(),
            kernel,
            actionName: "MissingPlugin-SomeMethod",
            actionInputStr: "{}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Tool execution failed:");
        StringAssert.Contains(result, "MissingPlugin not found");
    }

    [TestMethod]
    public async Task ExecuteToolAsync_WithNonExistentFunctionInPlugin_ReturnsClearErrorMessage()
    {
        // Arrange
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("ExistingPlugin", [
            KernelFunctionFactory.CreateFromMethod(() => "ok", "ValidMethod")
        ]);

        var service = CreateService();

        // Act
        var result = await service.ExecuteToolAsync(
            Guid.NewGuid(),
            kernel,
            actionName: "ExistingPlugin-MissingMethod",
            actionInputStr: "{}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Tool execution failed:");
        StringAssert.Contains(result, "MissingMethod");
    }

    [TestMethod]
    public async Task ExecuteToolAsync_WithStructuredStateEnabled_BroadcastsWithTaskStateMarker()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod(() => "Normal Output", "Execute")
        ]);

        var service = CreateService();

        // Act
        var result = await service.ExecuteToolAsync(
            sessionId,
            kernel,
            actionName: "TestPlugin-Execute",
            actionInputStr: "{}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: true,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("Normal Output", result);
        _hubMock.Verify(h => h.Clients.Group(sessionId.ToString()).SendCoreAsync(
            HubMethodNames.ToolExecution,
            It.Is<object[]>(args => args.Length > 0 && args[0] is ToolExecutionEvent && ((ToolExecutionEvent)args[0]).Result != null && ((ToolExecutionEvent)args[0]).Result!.Contains("[/app/workdir/_task_state.md]")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteToolAsync_BetweenMaxObservationAndAutoSaveThreshold_TruncatesWithWarning()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        _aiOptions.MaxToolObservationLength = 100;
        _aiOptions.AutoSaveToolOutputThreshold = 500;

        // Output of 200 chars (between 100 and 500)
        var mediumOutput = new string('X', 200);
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod(() => mediumOutput, "GetMediumData")
        ]);

        var service = CreateService();

        // Act
        var result = await service.ExecuteToolAsync(
            sessionId,
            kernel,
            actionName: "TestPlugin-GetMediumData",
            actionInputStr: "{}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "chars removed due to length");
        StringAssert.Contains(result, "[WARNING: Tool output was 200 chars");
    }

    [TestMethod]
    public async Task ExecuteToolAsync_WithBackslashWorkdirPrefix_NormalizesAndResolvesWorkspaceFile()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        var filePath = Path.Combine(_workspaceFixture.WorkspacePath, "testdata.txt");
        await File.WriteAllTextAsync(filePath, "Auto-loaded payload");

        string? receivedArg = null;
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod((string input) =>
            {
                receivedArg = input;
                return "OK";
            }, "EchoInput")
        ]);

        var service = CreateService();

        // Act - using Windows-style backslashes in @\app\workdir\
        var result = await service.ExecuteToolAsync(
            sessionId,
            kernel,
            actionName: "TestPlugin-EchoInput",
            actionInputStr: "{\"input\": \"@\\\\app\\\\workdir\\\\testdata.txt\"}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("OK", result);
        Assert.AreEqual("Auto-loaded payload", receivedArg);
    }

    [TestMethod]
    public async Task ExecuteToolAsync_WithBackslashDirectoryTraversal_RejectsSuspiciousParameter()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        string? receivedArg = null;
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod((string input) =>
            {
                receivedArg = input;
                return "OK";
            }, "EchoInput")
        ]);

        var service = CreateService();

        // Act - trying to traverse with backslashes
        var result = await service.ExecuteToolAsync(
            sessionId,
            kernel,
            actionName: "TestPlugin-EchoInput",
            actionInputStr: "{\"input\": \"@/app/workdir/subdir\\\\secret.txt\"}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert - should be rejected as suspicious and NOT substituted
        Assert.AreEqual("OK", result);
        Assert.AreEqual("@/app/workdir/subdir\\secret.txt", receivedArg);
    }

    [TestMethod]
    public async Task ExecuteToolAsync_SymlinkPointingOutsideWorkspace_DoesNotAutoLoadFile()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        var outsideDir = Path.Combine(Path.GetTempPath(), "outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var secretFile = Path.Combine(outsideDir, "secret.txt");
        await File.WriteAllTextAsync(secretFile, "TOP_SECRET_DATA");

        var linkPath = Path.Combine(_workspaceFixture.WorkspacePath, "symlink_secret.txt");
        try
        {
            File.CreateSymbolicLink(linkPath, secretFile);
        }
        catch
        {
            // If running on an environment without symlink creation privileges, skip gracefully
            try { Directory.Delete(outsideDir, true); } catch { }
            return;
        }

        try
        {
            string? receivedArg = null;
            var kernel = new Kernel();
            kernel.Plugins.AddFromFunctions("TestPlugin", [
                KernelFunctionFactory.CreateFromMethod((string input) =>
                {
                    receivedArg = input;
                    return "OK";
                }, "EchoInput")
            ]);

            var service = CreateService();

            // Act
            var result = await service.ExecuteToolAsync(
                sessionId,
                kernel,
                actionName: "TestPlugin-EchoInput",
                actionInputStr: "{\"input\": \"@/app/workdir/symlink_secret.txt\"}",
                step: 1,
                debugMode: false,
                structuredStateEnabled: false,
                ct: CancellationToken.None);

            // Assert - symlink pointing outside workspace must NOT be auto-loaded
            Assert.AreEqual("OK", result);
            Assert.AreNotEqual("TOP_SECRET_DATA", receivedArg, "Symlink target outside workspace should not be auto-loaded");
            Assert.AreEqual("@/app/workdir/symlink_secret.txt", receivedArg);
        }
        finally
        {
            try { File.Delete(linkPath); } catch { }
            try { Directory.Delete(outsideDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task ExecuteToolAsync_LargeOutput_UsesConfiguredToolOutputPreviewLength()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        _aiOptions.AutoSaveToolOutputThreshold = 1000;
        _aiOptions.ToolOutputPreviewLength = 200; // Custom preview length!

        string largeString = new string('A', 2000);
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod(() => largeString, "GetLargeData")
        ]);

        var service = CreateService();

        // Act
        var result = await service.ExecuteToolAsync(
            sessionId,
            kernel,
            actionName: "TestPlugin-GetLargeData",
            actionInputStr: "{}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "SUCCESS: Tool output was very large");
        StringAssert.Contains(result, "[1600 chars truncated]");
        var previewSection = result.Substring(result.IndexOf("--- Output Preview ---"));
        Assert.IsFalse(previewSection.Contains(new string('A', 201) + "\n\n..."), "Preview beginning should respect ToolOutputPreviewLength (200), not hardcoded 500");
    }

    [TestMethod]
    public async Task ExecuteToolAsync_ConcurrentExecutionsSameStep_GeneratesDistinctFilesWithoutOverwriting()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        _aiOptions.AutoSaveToolOutputThreshold = 100;

        var largeStringA = new string('A', 500);
        var largeStringB = new string('B', 500);

        var barrier = new Barrier(2);

        var kernelA = new Kernel();
        kernelA.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod(async () =>
            {
                barrier.SignalAndWait();
                await Task.Yield();
                return largeStringA;
            }, "GetLargeData")
        ]);

        var kernelB = new Kernel();
        kernelB.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod(async () =>
            {
                barrier.SignalAndWait();
                await Task.Yield();
                return largeStringB;
            }, "GetLargeData")
        ]);

        var lockManager = new WorkspaceLockManager();
        var service = new ToolExecutionService(
            _taskStore,
            _stateManagerMock.Object,
            _aiOptions,
            _sysOptions,
            NullLogger<ToolExecutionService>.Instance,
            lockManager);

        // Act: Run both tool executions concurrently in the same step
        var taskA = Task.Run(() => service.ExecuteToolAsync(
            sessionId,
            kernelA,
            actionName: "TestPlugin-GetLargeData",
            actionInputStr: "{}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None));

        var taskB = Task.Run(() => service.ExecuteToolAsync(
            sessionId,
            kernelB,
            actionName: "TestPlugin-GetLargeData",
            actionInputStr: "{}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None));

        await Task.WhenAll(taskA, taskB);

        // Assert - Both executions must have saved to distinct files without one overwriting the other
        var files = Directory.GetFiles(_workspaceFixture.WorkspacePath, "TestPlugin-GetLargeData_step1_*");
        Assert.AreEqual(2, files.Length, $"Expected 2 distinct output files for concurrent executions, but found {files.Length}. One file was overwritten!");

        var fileContents = files.Select(File.ReadAllText).ToList();
        Assert.IsTrue(fileContents.Any(c => c == largeStringA), "Output A was lost or overwritten!");
        Assert.IsTrue(fileContents.Any(c => c == largeStringB), "Output B was lost or overwritten!");
    }

    [TestMethod]
    public async Task ExecuteToolAsync_AutoLoadFileParameter_AcquiresWorkspaceFileLock()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        var fileName = "input_data.txt";
        var filePath = Path.Combine(_workspaceFixture.WorkspacePath, fileName);
        await File.WriteAllTextAsync(filePath, "Hello from file");

        var lockManagerMock = new Mock<SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager>();
        var disposableMock = new Mock<IDisposable>();
        lockManagerMock
            .Setup(m => m.AcquireLockAsync(sessionId, fileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(disposableMock.Object);

        string? receivedArg = null;
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", [
            KernelFunctionFactory.CreateFromMethod((string input) =>
            {
                receivedArg = input;
                return "OK";
            }, "EchoInput")
        ]);

        var service = new ToolExecutionService(
            _taskStore,
            _stateManagerMock.Object,
            _aiOptions,
            _sysOptions,
            NullLogger<ToolExecutionService>.Instance,
            lockManagerMock.Object);

        // Act
        var result = await service.ExecuteToolAsync(
            sessionId,
            kernel,
            actionName: "TestPlugin-EchoInput",
            actionInputStr: $"{{\"input\": \"@/app/workdir/{fileName}\"}}",
            step: 1,
            debugMode: false,
            structuredStateEnabled: false,
            ct: CancellationToken.None);

        // Assert
        Assert.AreEqual("OK", result);
        Assert.AreEqual("Hello from file", receivedArg);
        lockManagerMock.Verify(m => m.AcquireLockAsync(sessionId, fileName, It.IsAny<CancellationToken>()), Times.Once);
        disposableMock.Verify(d => d.Dispose(), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteToolAsync_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("TestPlugin", new[]
        {
            KernelFunctionFactory.CreateFromMethod((CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return "OK";
            }, "LongRunningFunction")
        });

        var service = new ToolExecutionService(
            _taskStore,
            _stateManagerMock.Object,
            _aiOptions,
            _sysOptions,
            NullLogger<ToolExecutionService>.Instance);

        var sessionId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel token

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await service.ExecuteToolAsync(
                sessionId,
                kernel,
                actionName: "TestPlugin-LongRunningFunction",
                actionInputStr: "{}",
                step: 1,
                debugMode: false,
                structuredStateEnabled: false,
                ct: cts.Token);
        });
    }
}

