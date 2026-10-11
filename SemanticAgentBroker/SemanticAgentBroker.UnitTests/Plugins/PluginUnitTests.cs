using Moq;
using SemanticAgentBroker.Plugins.BuiltIn.Optional;
using SemanticAgentBroker.Plugins.BuiltIn.Required;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Plugins;

[TestClass]
public class PluginUnitTests
{
    private TestWorkspaceFixture _workspaceFixture = null!;
    private Mock<IPluginContext> _contextMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _workspaceFixture = new TestWorkspaceFixture();
        _contextMock = new Mock<IPluginContext>();
        _contextMock.Setup(c => c.WorkspacePath).Returns(_workspaceFixture.WorkspacePath);
        _contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string>());
    }

    [TestCleanup]
    public void Cleanup()
    {
        _workspaceFixture.Dispose();
    }

    [TestMethod]
    public async Task SystemInfoAgentPlugin_GetCurrentDateTime_ReturnsValidIsoString()
    {
        // Arrange
        var plugin = new SystemInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetCurrentDateTime");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.IsTrue(DateTimeOffset.TryParse(result, out _));
    }

    [TestMethod]
    public async Task SystemInfoAgentPlugin_GetCurrentUtcDateTime_ReturnsValidUtcIsoString()
    {
        // Arrange
        var plugin = new SystemInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetCurrentUtcDateTime");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.IsTrue(DateTimeOffset.TryParse(result, out var parsed));
        Assert.AreEqual(TimeSpan.Zero, parsed.Offset);
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_GetWorkspaceTree_ReturnsFormattedFileTree()
    {
        // Arrange - create test files in workspace
        var subDir = Path.Combine(_workspaceFixture.WorkspacePath, "subfolder");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(_workspaceFixture.WorkspacePath, "root.txt"), "hello root");
        File.WriteAllText(Path.Combine(subDir, "nested.txt"), "hello nested");

        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetWorkspaceTree");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "root.txt");
        StringAssert.Contains(result, "subfolder");
        StringAssert.Contains(result, "nested.txt");
        StringAssert.Contains(result, "Total: 2 files");
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_GetWorkspaceTree_EmptyWorkspace_ReturnsEmptyMessage()
    {
        // Arrange
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetWorkspaceTree");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("Workspace directory is empty.", result);
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_GetWorkspaceTree_WithMaxDepth_LimitsRecursion()
    {
        // Arrange - create deep directory hierarchy and a root file
        File.WriteAllText(Path.Combine(_workspaceFixture.WorkspacePath, "root.txt"), "root");
        var deepDir = Path.Combine(_workspaceFixture.WorkspacePath, "level1", "level2", "level3");
        Directory.CreateDirectory(deepDir);
        File.WriteAllText(Path.Combine(deepDir, "deep.txt"), "deep content");

        _contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string> { ["MaxDepth"] = "1" });

        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetWorkspaceTree");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "max depth 1 reached");
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_SearchFiles_FindsMatchingFiles()
    {
        // Arrange - create test files
        File.WriteAllText(Path.Combine(_workspaceFixture.WorkspacePath, "data1.csv"), "col1,col2");
        File.WriteAllText(Path.Combine(_workspaceFixture.WorkspacePath, "data2.json"), "{}");
        File.WriteAllText(Path.Combine(_workspaceFixture.WorkspacePath, "readme.txt"), "readme");

        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "SearchFiles");

        // Act
        var args = new Dictionary<string, object?> { ["pattern"] = "*.csv" };
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "data1.csv");
        Assert.IsFalse(result.Contains("readme.txt"));
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_GetDiskUsage_ReturnsUsageSummary()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_workspaceFixture.WorkspacePath, "file.bin"), new string('A', 1024));

        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetDiskUsage");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Workspace Disk Usage:");
        StringAssert.Contains(result, "Total Files: 1");
    }

    [TestMethod]
    public async Task HttpClientAgentPlugin_MissingFactory_ReturnsGracefulError()
    {
        // Arrange - context with no IHttpClientFactory registered
        _contextMock.Setup(c => c.GetService<System.Net.Http.IHttpClientFactory>()).Returns((System.Net.Http.IHttpClientFactory?)null);

        var plugin = new HttpClientAgentPlugin();
        var getMethod = plugin.GetMethods().First(m => m.Id == "HttpGet");

        // Act
        var result = await getMethod.ExecuteAsync(
            new Dictionary<string, object?> { ["url"] = "https://example.com" },
            _contextMock.Object,
            CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "IHttpClientFactory is not available");
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_GetWorkspaceTree_NonExistentDirectory_ReturnsEmptyOrNotExists()
    {
        // Arrange - point context to non-existent directory
        _contextMock.Setup(c => c.WorkspacePath).Returns(Path.Combine(_workspaceFixture.WorkspacePath, "missing_dir"));

        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetWorkspaceTree");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("Workspace directory is empty or does not exist.", result);
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_GetDiskUsage_WhenWorkspaceEmpty_ReturnsZeroBytes()
    {
        // Arrange - empty workspace directory
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetDiskUsage");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("Workspace is empty (0 bytes).", result);
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_SearchFiles_WhenNoMatchingFiles_ReturnsNoFilesFound()
    {
        // Arrange
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        File.WriteAllText(Path.Combine(_workspaceFixture.WorkspacePath, "notes.txt"), "hello");

        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "SearchFiles");

        // Act
        var args = new Dictionary<string, object?> { ["pattern"] = "*.parquet" };
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("No files found matching pattern '*.parquet'.", result);
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_GetWorkspaceTree_WithMaxDepthReached_OutputsTruncationNotice()
    {
        // Arrange - nested directory structure exceeding MaxDepth=1
        var subDir = Path.Combine(_workspaceFixture.WorkspacePath, "subfolder");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(_workspaceFixture.WorkspacePath, "root.txt"), "root");
        File.WriteAllText(Path.Combine(subDir, "file.txt"), "content");

        _contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string>
        {
            ["MaxDepth"] = "1"
        });

        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetWorkspaceTree");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "workspace/");
        StringAssert.Contains(result, "subfolder/");
        StringAssert.Contains(result, "└── ... (max depth 1 reached)");
        StringAssert.Contains(result, "root.txt");
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_GetDiskUsage_RespectsMaxDepthAndFileCountLimit()
    {
        // Arrange
        var subDir = Path.Combine(_workspaceFixture.WorkspacePath, "level1", "level2");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(_workspaceFixture.WorkspacePath, "root.txt"), "12345");
        File.WriteAllText(Path.Combine(_workspaceFixture.WorkspacePath, "level1", "mid.txt"), "1234567890");
        File.WriteAllText(Path.Combine(subDir, "deep.txt"), "123456789012345");

        _contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string>
        {
            ["MaxDepth"] = "1" // Should only include root.txt and mid.txt, not deep.txt
        });

        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetDiskUsage");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Workspace Disk Usage:");
        StringAssert.Contains(result, "Total Files: 2");
        StringAssert.Contains(result, "mid.txt");
        Assert.IsFalse(result.Contains("deep.txt"));
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_SearchFiles_FindsNestedFilesAcrossDirectories()
    {
        // Arrange
        var nestedDir = Path.Combine(_workspaceFixture.WorkspacePath, "nested", "sub");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(Path.Combine(nestedDir, "deep_match.log"), "sample log");
        File.WriteAllText(Path.Combine(_workspaceFixture.WorkspacePath, "root_match.log"), "root log");

        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "SearchFiles");

        // Act
        var args = new Dictionary<string, object?> { ["pattern"] = "*.log" };
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "root_match.log");
        StringAssert.Contains(result, "deep_match.log");
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_GetDiskUsage_WhenMaxDepthIsNegative_ClampsToZeroWithoutThrowingException()
    {
        // Arrange
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        File.WriteAllText(Path.Combine(_workspaceFixture.WorkspacePath, "test.txt"), "hello");

        _contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string>
        {
            ["MaxDepth"] = "-1",
            ["MaxFiles"] = "-5"
        });

        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetDiskUsage");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Workspace Disk Usage:");
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_SearchFiles_WithTraversalPattern_RejectsUnsafePattern()
    {
        // Arrange
        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "SearchFiles");

        // Act
        var args = new Dictionary<string, object?> { ["pattern"] = "../*.txt" };
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Error searching files");
        StringAssert.Contains(result, "traversal");
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_SearchFiles_WithRootedPattern_RejectsUnsafePattern()
    {
        // Arrange
        var plugin = new WorkspaceInfoAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "SearchFiles");

        // Act
        var args = new Dictionary<string, object?> { ["pattern"] = OperatingSystem.IsWindows() ? "C:\\Windows\\*.dll" : "/etc/*" };
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Error searching files");
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_GetWorkspaceTree_WithConcurrentFileDeletions_DoesNotThrow()
    {
        // Arrange
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        var plugin = new WorkspaceInfoAgentPlugin();
        var treeMethod = plugin.GetMethods().First(m => m.Id == "GetWorkspaceTree");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var churnTask = Task.Run(async () =>
        {
            int counter = 0;
            while (!cts.IsCancellationRequested)
            {
                var subPath = Path.Combine(_workspaceFixture.WorkspacePath, $"sub_{counter++}");
                try
                {
                    Directory.CreateDirectory(subPath);
                    await File.WriteAllTextAsync(Path.Combine(subPath, "temp.txt"), "temp", cts.Token);
                    Directory.Delete(subPath, recursive: true);
                }
                catch { }
            }
        });

        // Act & Assert
        for (int i = 0; i < 100; i++)
        {
            var result = await treeMethod.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);
            Assert.IsNotNull(result);
        }

        cts.Cancel();
        await churnTask;
    }

    [TestMethod]
    public async Task WorkspaceInfoAgentPlugin_GetDiskUsage_WithConcurrentFileDeletions_DoesNotThrow()
    {
        // Arrange
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        var plugin = new WorkspaceInfoAgentPlugin();
        var usageMethod = plugin.GetMethods().First(m => m.Id == "GetDiskUsage");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var churnTask = Task.Run(async () =>
        {
            int counter = 0;
            while (!cts.IsCancellationRequested)
            {
                var subPath = Path.Combine(_workspaceFixture.WorkspacePath, $"sub_{counter++}");
                try
                {
                    Directory.CreateDirectory(subPath);
                    await File.WriteAllTextAsync(Path.Combine(subPath, "temp.txt"), "temp", cts.Token);
                    Directory.Delete(subPath, recursive: true);
                }
                catch { }
            }
        });

        // Act & Assert
        for (int i = 0; i < 100; i++)
        {
            var result = await usageMethod.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);
            Assert.IsNotNull(result);
        }

        cts.Cancel();
        await churnTask;
    }
}
