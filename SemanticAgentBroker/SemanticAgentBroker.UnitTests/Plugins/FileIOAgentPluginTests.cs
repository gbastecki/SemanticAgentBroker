using Moq;
using SemanticAgentBroker.Plugins.BuiltIn.Required;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Plugins;

[TestClass]
public class FileIOAgentPluginTests
{
    private TestWorkspaceFixture _workspaceFixture = null!;
    private Mock<IPluginContext> _contextMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _workspaceFixture = new TestWorkspaceFixture();
        _contextMock = new Mock<IPluginContext>();
        _contextMock.Setup(c => c.WorkspacePath).Returns(_workspaceFixture.WorkspacePath);
        _contextMock.Setup(c => c.SessionId).Returns(_workspaceFixture.SessionId);
        _contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string>());
    }

    [TestCleanup]
    public void Cleanup()
    {
        _workspaceFixture.Dispose();
    }

    [TestMethod]
    public async Task SaveToWorkspace_WithValidContent_WritesFileSuccessfully()
    {
        // Arrange
        var method = new SaveToWorkspaceMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "test.txt",
            ["content"] = "Hello, world!"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "File saved successfully.");
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "test.txt");
        Assert.IsTrue(File.Exists(filePath));
        Assert.AreEqual("Hello, world!", await File.ReadAllTextAsync(filePath));
    }

    [TestMethod]
    public async Task SaveToWorkspace_WithSystemFilePrefixUnderscore_ReturnsPermissionDeniedError()
    {
        // Arrange - attempting to write to internal state file
        var method = new SaveToWorkspaceMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "_task_state.json",
            ["content"] = "{}"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Permission denied");
    }

    [TestMethod]
    public async Task AppendToWorkspace_AppendsContentWithNewline()
    {
        // Arrange
        var saveMethod = new SaveToWorkspaceMethod();
        await saveMethod.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "log.txt",
            ["content"] = "Line 1"
        }, _contextMock.Object, CancellationToken.None);

        var appendMethod = new AppendToWorkspaceMethod();

        // Act
        var result = await appendMethod.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "log.txt",
            ["content"] = "Line 2"
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Content appended to");
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "log.txt");
        string fileContent = await File.ReadAllTextAsync(filePath);
        Assert.AreEqual("Line 1\nLine 2", fileContent);
    }

    [TestMethod]
    public async Task ReadFromWorkspace_ReadsExistingFile()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "sample.txt");
        await File.WriteAllTextAsync(filePath, "Sample content here");

        var method = new ReadFromWorkspaceMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "sample.txt"
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Sample content here");
        StringAssert.Contains(result, "[File: sample.txt");
    }

    [TestMethod]
    public async Task ReadFromWorkspace_WhenFileExceedsMaxReadChars_TruncatesContent()
    {
        // Arrange - Configure MaxReadChars = 50
        _contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string> { ["MaxReadChars"] = "50" });
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "large.txt");
        await File.WriteAllTextAsync(filePath, "Line 1\nLine 2\nLine 3\nLine 4\nLine 5\nLine 6\nLine 7\nLine 8\nLine 9\nLine 10\n");

        var method = new ReadFromWorkspaceMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "large.txt"
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "[TRUNCATED");
    }

    [TestMethod]
    public async Task ListWorkspaceFiles_WithFiles_ReturnsListWithFileSizes()
    {
        // Arrange
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        await File.WriteAllTextAsync(Path.Combine(_workspaceFixture.WorkspacePath, "file1.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(_workspaceFixture.WorkspacePath, "file2.json"), "{}");

        var method = new ListWorkspaceFilesMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Files in workspace:");
        StringAssert.Contains(result, "file1.txt");
        StringAssert.Contains(result, "file2.json");
    }

    [TestMethod]
    public async Task ListWorkspaceFiles_EmptyWorkspace_ReturnsEmptyMessage()
    {
        // Arrange
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);

        var method = new ListWorkspaceFilesMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("Workspace directory is empty.", result);
    }

    [TestMethod]
    public async Task ViewWorkspaceFile_ReturnsRequestedLineRange()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "multiline.txt");
        await File.WriteAllLinesAsync(filePath, new[] { "Line 1", "Line 2", "Line 3", "Line 4", "Line 5" });

        var method = new ViewWorkspaceFileMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "multiline.txt",
            ["startLine"] = 2,
            ["endLine"] = 4
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "2: Line 2");
        StringAssert.Contains(result, "3: Line 3");
        StringAssert.Contains(result, "4: Line 4");
        Assert.IsFalse(result.Contains("1: Line 1"));
        Assert.IsFalse(result.Contains("5: Line 5"));
    }

    [TestMethod]
    public async Task ViewWorkspaceFile_NonExistentFile_ReturnsErrorMessage()
    {
        // Arrange
        var method = new ViewWorkspaceFileMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "missing_file.txt",
            ["startLine"] = 1,
            ["endLine"] = 10
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Error: File 'missing_file.txt' not found in workspace.");
    }

    [TestMethod]
    public async Task ViewWorkspaceFile_WhenLineParametersOmitted_DefaultsGracefullyWithoutThrowingKeyNotFoundException()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "test_view_defaults.txt");
        await File.WriteAllLinesAsync(filePath, new[] { "Line 1", "Line 2", "Line 3" });

        var method = new ViewWorkspaceFileMethod();

        // Act - omit startLine and endLine
        var result = await method.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "test_view_defaults.txt"
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "1: Line 1");
        StringAssert.Contains(result, "2: Line 2");
        StringAssert.Contains(result, "3: Line 3");
    }

    [TestMethod]
    public async Task GrepWorkspaceFile_FindsMatchingLines()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "code.py");
        await File.WriteAllLinesAsync(filePath, new[]
        {
            "import os",
            "def calculate_total():",
            "    return 42",
            "print(calculate_total())"
        });

        var method = new GrepWorkspaceFileMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "code.py",
            ["pattern"] = "calculate_total"
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "2 match(es) in code.py");
        StringAssert.Contains(result, "def calculate_total():");
        StringAssert.Contains(result, "print(calculate_total())");
    }

    [TestMethod]
    public async Task GrepWorkspaceFile_NoMatchesFound_ReturnsNoMatchesMessage()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "empty_search.txt");
        await File.WriteAllTextAsync(filePath, "Apples and Oranges");

        var method = new GrepWorkspaceFileMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "empty_search.txt",
            ["pattern"] = "Bananas"
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("No matches found.", result);
    }

    [TestMethod]
    public async Task GrepWorkspaceFile_InvalidRegexPattern_ReturnsErrorMessage()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "regex_test.txt");
        await File.WriteAllTextAsync(filePath, "Some text");

        var method = new GrepWorkspaceFileMethod();

        // Act - invalid regex (unmatched bracket)
        var result = await method.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "regex_test.txt",
            ["pattern"] = "[a-z"
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Error: Invalid regular expression pattern");
    }

    [TestMethod]
    public async Task ReplaceInWorkspaceFile_ReplacesOccurrences()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "replace.txt");
        await File.WriteAllTextAsync(filePath, "cat dog cat bird");

        var method = new ReplaceInWorkspaceFileMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "replace.txt",
            ["searchText"] = "cat",
            ["replaceText"] = "lion"
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Replaced 2 occurrence(s)");
        string updated = await File.ReadAllTextAsync(filePath);
        Assert.AreEqual("lion dog lion bird", updated);
    }

    [TestMethod]
    public async Task ReplaceInWorkspaceFile_InNestedDirectory_PreservesSubdirectoryInPath()
    {
        // Arrange
        var subDir = Path.Combine(_workspaceFixture.WorkspacePath, "subfolder", "nested");
        Directory.CreateDirectory(subDir);
        string filePath = Path.Combine(subDir, "replace.txt");
        await File.WriteAllTextAsync(filePath, "cat dog cat bird");

        var method = new ReplaceInWorkspaceFileMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "subfolder/nested/replace.txt",
            ["searchText"] = "cat",
            ["replaceText"] = "lion"
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("Replaced 2 occurrence(s) in /app/workdir/subfolder/nested/replace.txt.", result);
        string updated = await File.ReadAllTextAsync(filePath);
        Assert.AreEqual("lion dog lion bird", updated);
    }

    [TestMethod]
    public async Task ReplaceInWorkspaceFile_WhenPatternNotFound_ReturnsNoOccurrencesMessage()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "no_replace.txt");
        await File.WriteAllTextAsync(filePath, "cat dog bird");

        var method = new ReplaceInWorkspaceFileMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "no_replace.txt",
            ["searchText"] = "elephant",
            ["replaceText"] = "lion"
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "No occurrences of the search text found");
    }

    [TestMethod]
    public async Task ReadFromWorkspace_WithSymlinkPointingOutsideWorkspace_ThrowsPathTraversalError()
    {
        // Arrange
        var outsideDir = Path.Combine(Path.GetTempPath(), "outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var secretFile = Path.Combine(outsideDir, "secret.txt");
        await File.WriteAllTextAsync(secretFile, "TOP_SECRET_DATA");

        var linkPath = Path.Combine(_workspaceFixture.WorkspacePath, "symlink.txt");
        try
        {
            File.CreateSymbolicLink(linkPath, secretFile);
        }
        catch
        {
            // If running on an environment without symlink creation privileges, skip gracefully
            return;
        }

        try
        {
            var readMethod = new ReadFromWorkspaceMethod();

            // Act
            var result = await readMethod.ExecuteAsync(new Dictionary<string, object?>
            {
                ["filename"] = "symlink.txt"
            }, _contextMock.Object, CancellationToken.None);

            // Assert
            StringAssert.Contains(result, "Path traversal attempt");
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task ViewWorkspaceFile_StartLineExceedsTotalLines_ReturnsNoLinesFoundMessage()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "short.txt");
        await File.WriteAllLinesAsync(filePath, new[] { "Line 1", "Line 2" });

        var method = new ViewWorkspaceFileMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "short.txt",
            ["startLine"] = 50,
            ["endLine"] = 100
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("No lines found in the specified range.", result);
    }

    [TestMethod]
    public async Task AppendToWorkspace_WhenFileDoesNotExist_CreatesFileAndAppends()
    {
        // Arrange
        var appendMethod = new AppendToWorkspaceMethod();

        // Act
        var result = await appendMethod.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "new_append.txt",
            ["content"] = "Initial first line"
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Content appended to");
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "new_append.txt");
        Assert.IsTrue(File.Exists(filePath));
        Assert.AreEqual("Initial first line", await File.ReadAllTextAsync(filePath));
    }

    [TestMethod]
    public async Task SaveToWorkspace_WithLinuxWorkdirPrefix_ExtractsFilenameAndSaves()
    {
        // Arrange - LLM passing "/app/workdir/analytics_data.csv"
        var saveMethod = new SaveToWorkspaceMethod();

        // Act
        var result = await saveMethod.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "/app/workdir/analytics_data.csv",
            ["content"] = "col1,col2\nval1,val2"
        }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "File saved successfully.");
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "analytics_data.csv");
        Assert.IsTrue(File.Exists(filePath));
        Assert.AreEqual("col1,col2\nval1,val2", await File.ReadAllTextAsync(filePath));
    }

    [TestMethod]
    public async Task SaveToWorkspace_WithOnlyInvalidCharacters_FallsBackToDefaultFilename()
    {
        // Arrange - filename made entirely of invalid characters e.g. "?:*"
        var saveMethod = new SaveToWorkspaceMethod();

        // Act
        var result = await saveMethod.ExecuteAsync(new Dictionary<string, object?>
        {
            ["filename"] = "?:*",
            ["content"] = "Default filename fallback"
        }, _contextMock.Object, CancellationToken.None);

        // Assert - falls back to "output.txt"
        StringAssert.StartsWith(result, "File saved successfully.");
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "output.txt");
        Assert.IsTrue(File.Exists(filePath));
        Assert.AreEqual("Default filename fallback", await File.ReadAllTextAsync(filePath));
    }

    [TestMethod]
    public async Task SaveToWorkspace_ParentDirectorySymlinkTraversal_IsBlocked()
    {
        // Arrange
        var outsideDir = Path.Combine(_workspaceFixture.WorkspacePath, "..", "outside_fio_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);

        var symlinkDir = Path.Combine(_workspaceFixture.WorkspacePath, "sym_dir");
        if (OperatingSystem.IsWindows())
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{symlinkDir}\" \"{outsideDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit();
        }
        else
        {
            Directory.CreateSymbolicLink(symlinkDir, outsideDir);
        }

        try
        {
            var saveMethod = new SaveToWorkspaceMethod();
            var args = new Dictionary<string, object?>
            {
                ["filename"] = "sym_dir/hacked.txt",
                ["content"] = "malicious payload"
            };

            // Act
            var result = await saveMethod.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

            // Assert - SanitizeFilename flattens path to hacked.txt in root workspace, never writes into outsideDir
            Assert.IsFalse(File.Exists(Path.Combine(outsideDir, "hacked.txt")), "File must NOT be written to the symlinked target outside workspace.");
        }
        finally
        {
            try { Directory.Delete(symlinkDir); } catch { }
            try { Directory.Delete(outsideDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task GrepWorkspaceFile_WithConsecutiveMatchingLinesAndContext_ShowsMarkerForBothMatches()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "consecutive.txt");
        await File.WriteAllLinesAsync(filePath, new[]
        {
            "alpha apple one",
            "beta apple two",
            "gamma orange three"
        });

        var method = new GrepWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "consecutive.txt",
            ["pattern"] = "apple",
            ["contextLines"] = 1
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert - Both line 1 and line 2 must have the match marker '▶'
        StringAssert.Contains(result, "▶ 1: alpha apple one");
        StringAssert.Contains(result, "▶ 2: beta apple two");
    }

    [TestMethod]
    public async Task ReplaceInWorkspaceFile_ReplacesOccurrencesSuccessfully()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "replace_valid.txt");
        await File.WriteAllTextAsync(filePath, "foo bar foo baz");

        var method = new ReplaceInWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "replace_valid.txt",
            ["searchText"] = "foo",
            ["replaceText"] = "qux"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Replaced 2 occurrence(s)");
        var content = await File.ReadAllTextAsync(filePath);
        Assert.AreEqual("qux bar qux baz", content);
    }

    [TestMethod]
    [Timeout(3000)]
    public async Task ReplaceInWorkspaceFile_EmptySearchText_ReturnsErrorWithoutHanging()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "replace_empty.txt");
        await File.WriteAllTextAsync(filePath, "sample content");

        var method = new ReplaceInWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "replace_empty.txt",
            ["searchText"] = "",
            ["replaceText"] = "replacement"
        };

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await method.ExecuteAsync(args, _contextMock.Object, cts.Token);

        // Assert
        StringAssert.Contains(result, "cannot be empty");
    }

    [TestMethod]
    public async Task ReplaceInWorkspaceFile_WithEmptyReplaceText_DeletesOccurrencesSuccessfully()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "replace_delete.txt");
        await File.WriteAllTextAsync(filePath, "remove this tag now");

        var method = new ReplaceInWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "replace_delete.txt",
            ["searchText"] = " this",
            ["replaceText"] = ""
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Replaced 1 occurrence(s)");
        var content = await File.ReadAllTextAsync(filePath);
        Assert.AreEqual("remove tag now", content);
    }

    [TestMethod]
    public void SanitizeFilename_NormalizesMixedSeparatorsCrossPlatform()
    {
        Assert.AreEqual("subfolder/report.txt", SaveToWorkspaceMethod.SanitizeFilename(@"subfolder\report.txt"));
        Assert.AreEqual("subfolder/nested/report.txt", SaveToWorkspaceMethod.SanitizeFilename(@"subfolder/nested\report.txt"));
        Assert.AreEqual("sub/report.txt", SaveToWorkspaceMethod.SanitizeFilename(@"/app/workdir\sub/report.txt"));
        Assert.AreEqual("output.txt", SaveToWorkspaceMethod.SanitizeFilename(@"\"));
        Assert.AreEqual("output.txt", SaveToWorkspaceMethod.SanitizeFilename(@"/"));
        Assert.AreEqual("test/file.txt", SaveToWorkspaceMethod.SanitizeFilename(@"test\file.txt"));
        Assert.IsFalse(SaveToWorkspaceMethod.SanitizeFilename(@"test\file.txt").Contains('\\'));
        Assert.IsTrue(SaveToWorkspaceMethod.SanitizeFilename(@"test\file.txt").Contains('/'));
    }

    [TestMethod]
    public void SanitizeFilename_WithMultiDotSequences_StripsDotTraversalSegments()
    {
        Assert.AreEqual("subfolder/data.txt", SaveToWorkspaceMethod.SanitizeFilename("subfolder/..../data.txt"));
        Assert.AreEqual("data.txt", SaveToWorkspaceMethod.SanitizeFilename("..../data.txt"));
        Assert.AreEqual("subfolder/nested/data.txt", SaveToWorkspaceMethod.SanitizeFilename("subfolder/..../..../nested/data.txt"));
        Assert.AreEqual("output.txt", SaveToWorkspaceMethod.SanitizeFilename("...."));
        Assert.AreEqual("output.txt", SaveToWorkspaceMethod.SanitizeFilename("..."));
    }

    [TestMethod]
    public async Task SaveToWorkspace_WithSubdirectoryPath_PreservesSubdirectoryAndCreatesFile()
    {
        // Arrange
        var method = new SaveToWorkspaceMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = @"deep\nested\output.txt",
            ["content"] = "Cross-platform content"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "File saved successfully.");
        StringAssert.Contains(result, "/app/workdir/deep/nested/output.txt");
        string expectedPath = Path.Combine(_workspaceFixture.WorkspacePath, "deep", "nested", "output.txt");
        Assert.IsTrue(File.Exists(expectedPath));
        Assert.AreEqual("Cross-platform content", await File.ReadAllTextAsync(expectedPath));
    }

    [TestMethod]
    public async Task ReadFromWorkspace_WithSubdirectoryPath_ReadsFileFromSubdirectory()
    {
        // Arrange
        var subDir = Path.Combine(_workspaceFixture.WorkspacePath, "nested");
        Directory.CreateDirectory(subDir);
        string filePath = Path.Combine(subDir, "data.txt");
        await File.WriteAllTextAsync(filePath, "nested data content");

        var method = new ReadFromWorkspaceMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "nested/data.txt"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "nested data content");
    }

    [TestMethod]
    public async Task AppendToWorkspace_WithSubdirectoryPath_AppendsToFileInSubdirectory()
    {
        // Arrange
        var method = new AppendToWorkspaceMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "logs/app.log",
            ["content"] = "log line 1"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Content appended to /app/workdir/logs/app.log");
        string expectedPath = Path.Combine(_workspaceFixture.WorkspacePath, "logs", "app.log");
        Assert.IsTrue(File.Exists(expectedPath));
        Assert.AreEqual("log line 1", await File.ReadAllTextAsync(expectedPath));
    }

    [TestMethod]
    public async Task ListWorkspaceFiles_WithSubdirectories_ListsNestedFilesRecursively()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_workspaceFixture.WorkspacePath, "dir1"));
        Directory.CreateDirectory(Path.Combine(_workspaceFixture.WorkspacePath, "dir2", "nested"));
        await File.WriteAllTextAsync(Path.Combine(_workspaceFixture.WorkspacePath, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(_workspaceFixture.WorkspacePath, "dir1", "sub1.txt"), "sub1");
        await File.WriteAllTextAsync(Path.Combine(_workspaceFixture.WorkspacePath, "dir2", "nested", "sub2.txt"), "sub2");

        var method = new ListWorkspaceFilesMethod();

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Files in workspace:");
        StringAssert.Contains(result, "root.txt");
        StringAssert.Contains(result, "dir1/sub1.txt");
        StringAssert.Contains(result, "dir2/nested/sub2.txt");
    }

    [TestMethod]
    public async Task GrepWorkspaceFile_WithMatchesAndContext_ReturnsFormattedLines()
    {
        // Arrange
        var lines = new[]
        {
            "Line 1: Alpha",
            "Line 2: Beta",
            "Line 3: Gamma",
            "Line 4: Delta TargetLine",
            "Line 5: Epsilon",
            "Line 6: Zeta",
            "Line 7: Eta TargetLine",
            "Line 8: Theta",
            "Line 9: Iota",
            "Line 10: Kappa"
        };
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "search.txt");
        await File.WriteAllLinesAsync(filePath, lines);

        var method = new GrepWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "search.txt",
            ["pattern"] = "TargetLine",
            ["contextLines"] = 1,
            ["maxResults"] = 10
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "2 match(es) in search.txt:");
        StringAssert.Contains(result, "▶ 4: Line 4: Delta TargetLine");
        StringAssert.Contains(result, "  3: Line 3: Gamma");
        StringAssert.Contains(result, "  5: Line 5: Epsilon");
        StringAssert.Contains(result, "---");
        StringAssert.Contains(result, "▶ 7: Line 7: Eta TargetLine");
        StringAssert.Contains(result, "  6: Line 6: Zeta");
        StringAssert.Contains(result, "  8: Line 8: Theta");
    }

    [TestMethod]
    public async Task GrepWorkspaceFile_WithMaxResults_TruncatesMatches()
    {
        // Arrange
        var lines = new[]
        {
            "match one",
            "match two",
            "match three"
        };
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "trunc.txt");
        await File.WriteAllLinesAsync(filePath, lines);

        var method = new GrepWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "trunc.txt",
            ["pattern"] = "match",
            ["maxResults"] = 1
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "TRUNCATED at 1 matches");
        StringAssert.Contains(result, "▶ 1: match one");
        Assert.IsFalse(result.Contains("▶ 2: match two"));
    }

    [TestMethod]
    public async Task GrepWorkspaceFile_NoMatches_ReturnsNoMatchesFound()
    {
        // Arrange
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "empty_search.txt");
        await File.WriteAllLinesAsync(filePath, new[] { "foo", "bar", "baz" });

        var method = new GrepWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "empty_search.txt",
            ["pattern"] = "nonexistent"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("No matches found.", result);
    }

    [TestMethod]
    public async Task GrepWorkspaceFile_StreamsFileContentWithoutReadingAllLinesAtOnce()
    {
        // Arrange - Create a larger file with 1000 lines
        string filePath = Path.Combine(_workspaceFixture.WorkspacePath, "streamed.txt");
        using (var writer = new StreamWriter(filePath))
        {
            for (int i = 1; i <= 1000; i++)
            {
                if (i == 500)
                    await writer.WriteLineAsync($"Line {i} SPECIAL_KEYWORD found");
                else
                    await writer.WriteLineAsync($"Line {i} content text");
            }
        }

        var method = new GrepWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "streamed.txt",
            ["pattern"] = "SPECIAL_KEYWORD",
            ["contextLines"] = 1
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "1 match(es) in streamed.txt:");
        StringAssert.Contains(result, "▶ 500: Line 500 SPECIAL_KEYWORD found");
        StringAssert.Contains(result, "  499: Line 499 content text");
        StringAssert.Contains(result, "  501: Line 501 content text");
    }

    [TestMethod]
    public async Task GrepWorkspaceFile_WhenFileInSubdirectoryExceeds50Mb_ReturnsPathWithSubdirectoryInErrorMessage()
    {
        // Arrange
        var subDir = Path.Combine(_workspaceFixture.WorkspacePath, "sub");
        Directory.CreateDirectory(subDir);
        string filePath = Path.Combine(subDir, "huge.csv");
        using (var fs = File.Create(filePath))
        {
            fs.SetLength(50_000_001);
        }

        var method = new GrepWorkspaceFileMethod();
        var args = new Dictionary<string, object?>
        {
            ["filename"] = "sub/huge.csv",
            ["pattern"] = "test"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Use Python to search it at /app/workdir/sub/huge.csv");
    }
}
