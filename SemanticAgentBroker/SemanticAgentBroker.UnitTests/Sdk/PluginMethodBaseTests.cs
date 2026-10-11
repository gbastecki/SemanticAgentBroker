using Moq;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Sdk;

public class TestablePluginMethod : PluginMethodBase
{
    public override string Id => "TestableMethod";
    public override string DisplayName => "Testable Method";
    public override string Description => "Method for testing PluginMethodBase.";

    public override Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct = default)
    {
        var req = GetRequiredString(arguments, "requiredParam");
        var opt = GetOptionalString(arguments, "optionalParam", "defaultValue");
        return Task.FromResult($"req={req};opt={opt}");
    }

    public async Task<string> TestSaveLargeResultAsync(string result, string prefix, IPluginContext context, int maxLength = 20, string extension = "txt")
    {
        return await SaveLargeResultAsync(result, prefix, context, maxLength, extension);
    }

    public string TestGetOptionalString(IDictionary<string, object?> arguments, string key, string defaultValue = "")
    {
        return GetOptionalString(arguments, key, defaultValue);
    }
}

[TestClass]
public class PluginMethodBaseTests
{
    [TestMethod]
    public async Task ExecuteAsync_WithValidRequiredAndOptionalArgs_ExecutesSuccessfully()
    {
        // Arrange
        var method = new TestablePluginMethod();
        var contextMock = new Mock<IPluginContext>();
        var args = new Dictionary<string, object?>
        {
            ["requiredParam"] = "value1",
            ["optionalParam"] = "customValue"
        };

        // Act
        var result = await method.ExecuteAsync(args, contextMock.Object);

        // Assert
        Assert.AreEqual("req=value1;opt=customValue", result);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithMissingOptionalArg_UsesDefaultValue()
    {
        // Arrange
        var method = new TestablePluginMethod();
        var contextMock = new Mock<IPluginContext>();
        var args = new Dictionary<string, object?>
        {
            ["requiredParam"] = "value1"
        };

        // Act
        var result = await method.ExecuteAsync(args, contextMock.Object);

        // Assert
        Assert.AreEqual("req=value1;opt=defaultValue", result);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithMissingRequiredArg_ThrowsArgumentException()
    {
        // Arrange
        var method = new TestablePluginMethod();
        var contextMock = new Mock<IPluginContext>();
        var args = new Dictionary<string, object?>();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            method.ExecuteAsync(args, contextMock.Object));
    }

    [TestMethod]
    public async Task SaveLargeResultAsync_WhenBelowThreshold_ReturnsResultDirectly()
    {
        // Arrange
        var method = new TestablePluginMethod();
        var contextMock = new Mock<IPluginContext>();

        // Act
        var result = await method.TestSaveLargeResultAsync("short", "test", contextMock.Object, maxLength: 50);

        // Assert
        Assert.AreEqual("short", result);
    }

    [TestMethod]
    public async Task SaveLargeResultAsync_WhenAboveThreshold_SavesToWorkspaceAndReturnsPointer()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        var contextMock = new Mock<IPluginContext>();
        contextMock.Setup(c => c.WorkspacePath).Returns(workspaceFixture.WorkspacePath);

        var method = new TestablePluginMethod();
        string largeContent = new string('x', 100);

        // Act
        var result = await method.TestSaveLargeResultAsync(largeContent, "test_prefix", contextMock.Object, maxLength: 20);

        // Assert
        StringAssert.StartsWith(result, "Data saved to /app/workdir/test_prefix_output.txt");
        string expectedFilePath = Path.Combine(workspaceFixture.WorkspacePath, "test_prefix_output.txt");
        Assert.IsTrue(File.Exists(expectedFilePath));
        Assert.AreEqual(largeContent, await File.ReadAllTextAsync(expectedFilePath));
    }

    [TestMethod]
    public async Task SaveLargeResultAsync_WhenWorkspaceDirectoryDoesNotExist_CreatesDirectoryAndSaves()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        var nonExistentDir = Path.Combine(workspaceFixture.WorkspacePath, "uninitialized_subdir");
        Assert.IsFalse(Directory.Exists(nonExistentDir));

        var contextMock = new Mock<IPluginContext>();
        contextMock.Setup(c => c.WorkspacePath).Returns(nonExistentDir);

        var method = new TestablePluginMethod();
        string largeContent = new string('z', 100);

        // Act
        var result = await method.TestSaveLargeResultAsync(largeContent, "test_prefix", contextMock.Object, maxLength: 20);

        // Assert
        StringAssert.StartsWith(result, "Data saved to /app/workdir/test_prefix_output.txt");
        string expectedFilePath = Path.Combine(nonExistentDir, "test_prefix_output.txt");
        Assert.IsTrue(Directory.Exists(nonExistentDir));
        Assert.IsTrue(File.Exists(expectedFilePath));
        Assert.AreEqual(largeContent, await File.ReadAllTextAsync(expectedFilePath));
    }

    [TestMethod]
    public async Task SaveLargeResultAsync_WhenAboveThreshold_AcquiresWorkspaceLock()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        var sessionId = Guid.NewGuid();
        var lockManagerMock = new Mock<IWorkspaceLockManager>();
        var disposableMock = new Mock<IDisposable>();
        bool lockAcquired = false;
        bool lockDisposed = false;

        disposableMock.Setup(d => d.Dispose()).Callback(() => lockDisposed = true);
        lockManagerMock.Setup(l => l.AcquireLockAsync(sessionId, "test_prefix_output.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                lockAcquired = true;
                return disposableMock.Object;
            });

        var contextMock = new Mock<IPluginContext>();
        contextMock.Setup(c => c.WorkspacePath).Returns(workspaceFixture.WorkspacePath);
        contextMock.Setup(c => c.SessionId).Returns(sessionId);
        contextMock.Setup(c => c.GetService<IWorkspaceLockManager>()).Returns(lockManagerMock.Object);

        var method = new TestablePluginMethod();
        string largeContent = new string('x', 100);

        // Act
        var result = await method.TestSaveLargeResultAsync(largeContent, "test_prefix", contextMock.Object, maxLength: 20);

        // Assert
        Assert.IsTrue(lockAcquired, "SaveLargeResultAsync must acquire a file lock via IWorkspaceLockManager when available.");
        Assert.IsTrue(lockDisposed, "The acquired file lock must be disposed after file writing.");
        StringAssert.StartsWith(result, "Data saved to /app/workdir/test_prefix_output.txt");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithNullArgumentsDictionary_ThrowsArgumentNullException()
    {
        // Arrange
        var method = new TestablePluginMethod();
        var contextMock = new Mock<IPluginContext>();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            method.ExecuteAsync(null!, contextMock.Object));
    }

    [TestMethod]
    public async Task ExecuteAsync_WithJsonElementValues_ExtractsStringProperly()
    {
        // Arrange
        var method = new TestablePluginMethod();
        var contextMock = new Mock<IPluginContext>();
        var jsonElem = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("\"jsonStringValue\"");
        var args = new Dictionary<string, object?>
        {
            ["requiredParam"] = jsonElem,
            ["optionalParam"] = 42
        };

        // Act
        var result = await method.ExecuteAsync(args, contextMock.Object);

        // Assert
        Assert.AreEqual("req=jsonStringValue;opt=42", result);
    }

    [TestMethod]
    public async Task SaveLargeResultAsync_WithCustomExtension_SavesWithSpecifiedExtension()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        var contextMock = new Mock<IPluginContext>();
        contextMock.Setup(c => c.WorkspacePath).Returns(workspaceFixture.WorkspacePath);

        var method = new CustomExtensionPluginMethod();
        string largeCsv = "col1,col2\n" + new string('a', 100);

        // Act
        var result = await method.TestSaveCsvAsync(largeCsv, "sales_report", contextMock.Object);

        // Assert
        StringAssert.StartsWith(result, "Data saved to /app/workdir/sales_report_output.csv");
        string expectedFilePath = Path.Combine(workspaceFixture.WorkspacePath, "sales_report_output.csv");
        Assert.IsTrue(File.Exists(expectedFilePath));
        Assert.AreEqual(largeCsv, await File.ReadAllTextAsync(expectedFilePath));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    public async Task GetRequiredString_WhenValueIsMissingOrNull_ThrowsArgumentException(string? val)
    {
        // Arrange
        var method = new TestablePluginMethod();
        var contextMock = new Mock<IPluginContext>();
        var args = new Dictionary<string, object?>
        {
            ["requiredParam"] = val
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            method.ExecuteAsync(args, contextMock.Object));
    }

    [TestMethod]
    public async Task GetOptionalString_WhenValueIsEmpty_ReturnsDefaultValue()
    {
        // Arrange
        var method = new TestablePluginMethod();
        var contextMock = new Mock<IPluginContext>();
        var args = new Dictionary<string, object?>
        {
            ["requiredParam"] = "valid",
            ["optionalParam"] = "" // empty string
        };

        // Act
        var result = await method.ExecuteAsync(args, contextMock.Object);

        // Assert - should fall back to defaultValue
        Assert.AreEqual("req=valid;opt=defaultValue", result);
    }

    [TestMethod]
    public async Task SaveLargeResultAsync_WhenResultOrContextIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var method = new TestablePluginMethod();
        var contextMock = new Mock<IPluginContext>();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            method.TestSaveLargeResultAsync(null!, "prefix", contextMock.Object));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            method.TestSaveLargeResultAsync("valid content", "prefix", null!));
    }

    [TestMethod]
    public void PluginMethodBase_DefaultVirtualProperties_ReturnsExpectedDefaults()
    {
        // Arrange
        var method = new TestablePluginMethod();

        // Assert
        Assert.AreEqual("String result.", method.ReturnDescription);
        Assert.IsFalse(method.IsDangerous);
        Assert.AreEqual(0, method.Parameters.Count);
    }

    [TestMethod]
    public void GetOptionalString_WithNullArgumentsDictionary_ThrowsArgumentNullException()
    {
        // Arrange
        var method = new TestablePluginMethod();

        Assert.Throws<ArgumentNullException>(() =>
            method.TestGetOptionalString(null!, "key"));
    }

    [TestMethod]
    public async Task SaveLargeResultAsync_WithTraversalInPrefix_SanitizesAndKeepsFileInWorkspace()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        var contextMock = new Mock<IPluginContext>();
        contextMock.Setup(c => c.WorkspacePath).Returns(workspaceFixture.WorkspacePath);

        var method = new TestablePluginMethod();
        string largeContent = new string('x', 100);
        string traversalPrefix = "../../escaped_file";

        // Act
        var result = await method.TestSaveLargeResultAsync(largeContent, traversalPrefix, contextMock.Object, maxLength: 20);

        // Assert
        string outsideEscapedFile = Path.GetFullPath(Path.Combine(workspaceFixture.WorkspacePath, "../../escaped_file_output.txt"));
        Assert.IsFalse(File.Exists(outsideEscapedFile), "File should not be written outside workspace");

        string expectedFileInWorkspace = Path.Combine(workspaceFixture.WorkspacePath, "escaped_file_output.txt");
        Assert.IsTrue(File.Exists(expectedFileInWorkspace), "Sanitized file should exist in workspace");
        StringAssert.Contains(result, "escaped_file_output.txt");
    }

    [TestMethod]
    public async Task SaveLargeResultAsync_WithTraversalInExtension_SanitizesAndKeepsFileInWorkspace()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        var contextMock = new Mock<IPluginContext>();
        contextMock.Setup(c => c.WorkspacePath).Returns(workspaceFixture.WorkspacePath);

        var method = new TestablePluginMethod();
        string largeContent = new string('x', 100);
        string traversalExt = "../escaped.txt";

        // Act
        var result = await method.TestSaveLargeResultAsync(largeContent, "prefix", contextMock.Object, maxLength: 20, extension: traversalExt);

        // Assert
        string outsideEscapedFile = Path.GetFullPath(Path.Combine(workspaceFixture.WorkspacePath, "../escaped.txt"));
        Assert.IsFalse(File.Exists(outsideEscapedFile), "File should not be written outside workspace");

        string expectedFileInWorkspace = Path.Combine(workspaceFixture.WorkspacePath, "prefix_output.txt");
        Assert.IsTrue(File.Exists(expectedFileInWorkspace), "Sanitized file should exist in workspace with clean extension");
        StringAssert.Contains(result, "prefix_output.txt");
    }

    [TestMethod]
    public async Task SaveLargeResultAsync_TrailingSeparatorGuardsAgainstPartialPrefixMatching()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        var contextMock = new Mock<IPluginContext>();
        // Set workspace path without trailing separator
        contextMock.Setup(c => c.WorkspacePath).Returns(workspaceFixture.WorkspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var method = new TestablePluginMethod();
        string largeContent = new string('x', 100);

        // Act
        var result = await method.TestSaveLargeResultAsync(largeContent, "prefix", contextMock.Object, maxLength: 20);

        // Assert
        StringAssert.StartsWith(result, "Data saved to /app/workdir/prefix_output.txt");
        string expectedFileInWorkspace = Path.Combine(workspaceFixture.WorkspacePath, "prefix_output.txt");
        Assert.IsTrue(File.Exists(expectedFileInWorkspace));
    }
}

public class CustomExtensionPluginMethod : PluginMethodBase
{
    public override string Id => "CustomExtMethod";
    public override string DisplayName => "Custom Ext";
    public override string Description => "Desc";

    public override Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct = default)
    {
        return Task.FromResult("ok");
    }

    public async Task<string> TestSaveCsvAsync(string data, string prefix, IPluginContext context)
    {
        return await SaveLargeResultAsync(data, prefix, context, maxLength: 10, extension: "csv");
    }
}
