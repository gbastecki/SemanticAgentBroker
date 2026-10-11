using Moq;
using SemanticAgentBroker.Plugins.BuiltIn.Required;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Plugins;

[TestClass]
public class FileIOPluginGuardTests
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
    public async Task GrepWorkspaceFile_InvalidRegexPattern_ReturnsClearErrorMessage()
    {
        // Arrange
        var filePath = Path.Combine(_workspaceFixture.WorkspacePath, "test.txt");
        await File.WriteAllTextAsync(filePath, "sample line 1\nsample line 2");

        var method = new GrepWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "test.txt",
            ["pattern"] = "[unclosed bracket"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Error: Invalid regular expression pattern");
    }

    [TestMethod]
    public async Task GrepWorkspaceFile_WithContextLines_ShowsSurroundingLinesAndHighlight()
    {
        // Arrange
        var filePath = Path.Combine(_workspaceFixture.WorkspacePath, "code.py");
        var lines = new[]
        {
            "line 1: import os",
            "line 2: import sys",
            "line 3: def main():",
            "line 4:     print('TARGET_MATCH')",
            "line 5:     return 0",
            "line 6: if __name__ == '__main__':",
            "line 7:     main()"
        };
        await File.WriteAllLinesAsync(filePath, lines);

        var method = new GrepWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "code.py",
            ["pattern"] = "TARGET_MATCH",
            ["contextLines"] = "1"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "1 match(es) in code.py");
        StringAssert.Contains(result, "3: line 3: def main():");
        StringAssert.Contains(result, "▶ 4: line 4:     print('TARGET_MATCH')");
        StringAssert.Contains(result, "5: line 5:     return 0");
    }

    [TestMethod]
    public async Task GrepWorkspaceFile_MaxResultsExceeded_AppendsTruncationMessage()
    {
        // Arrange
        var filePath = Path.Combine(_workspaceFixture.WorkspacePath, "items.txt");
        var lines = Enumerable.Range(1, 20).Select(i => $"item_{i}_match").ToArray();
        await File.WriteAllLinesAsync(filePath, lines);

        var method = new GrepWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "items.txt",
            ["pattern"] = "match",
            ["maxResults"] = "3"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "... [TRUNCATED at 3 matches");
    }

    [TestMethod]
    public async Task GrepWorkspaceFile_MaxResultsExceeded_ReportsAccurateMatchCount()
    {
        // Arrange - 20 matching lines, maxResults=3
        var filePath = Path.Combine(_workspaceFixture.WorkspacePath, "many.txt");
        var lines = Enumerable.Range(1, 20).Select(i => $"item_{i}_match").ToArray();
        await File.WriteAllLinesAsync(filePath, lines);

        var method = new GrepWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "many.txt",
            ["pattern"] = "match",
            ["maxResults"] = "3"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert - Header must report 3+ match(es), not 4 match(es)
        StringAssert.StartsWith(result, "3+ match(es) in many.txt:",
            $"Actual header: {result.Split('\n')[0]}");
    }

    [TestMethod]
    public async Task ReplaceInWorkspaceFile_ValidTarget_ReplacesAndReturnsCount()
    {
        // Arrange
        var filePath = Path.Combine(_workspaceFixture.WorkspacePath, "config.json");
        await File.WriteAllTextAsync(filePath, "{\"port\": 8080, \"debug\": true, \"backup_port\": 8080}");

        var method = new ReplaceInWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "config.json",
            ["searchText"] = "8080",
            ["replaceText"] = "9090"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Replaced 2 occurrence(s)");
        var updatedContent = await File.ReadAllTextAsync(filePath);
        Assert.AreEqual("{\"port\": 9090, \"debug\": true, \"backup_port\": 9090}", updatedContent);
    }

    [TestMethod]
    public async Task ReplaceInWorkspaceFile_TextNotFound_ReturnsNoOccurrencesMessage()
    {
        // Arrange
        var filePath = Path.Combine(_workspaceFixture.WorkspacePath, "config.json");
        await File.WriteAllTextAsync(filePath, "{\"port\": 8080}");

        var method = new ReplaceInWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "config.json",
            ["searchText"] = "non_existent_key",
            ["replaceText"] = "new_value"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "No occurrences of the search text found in 'config.json'");
    }

    [TestMethod]
    public async Task ReplaceInWorkspaceFile_FileExceeds50MB_ReturnsErrorMessageWithoutOOM()
    {
        // Arrange - Create a file exceeding 50MB
        var filePath = Path.Combine(_workspaceFixture.WorkspacePath, "huge.txt");
        using (var fs = File.Create(filePath))
        {
            fs.SetLength(50_000_001);
        }

        var method = new ReplaceInWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "huge.txt",
            ["searchText"] = "test",
            ["replaceText"] = "replacement"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert - Should return error without loading into memory
        StringAssert.StartsWith(result, "Error: File 'huge.txt' is too large");
        StringAssert.Contains(result, "Use Python to modify it");
    }
}
