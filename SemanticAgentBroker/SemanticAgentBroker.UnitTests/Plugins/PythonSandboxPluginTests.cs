using Moq;
using SemanticAgentBroker.Plugins.BuiltIn.Required;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Reflection;

namespace SemanticAgentBroker.UnitTests.Plugins;

[TestClass]
public class PythonSandboxPluginTests
{
    [TestMethod]
    public void PythonSandboxAgentPlugin_DeclaresCorrectMetadataAndConfigSchema()
    {
        // Arrange
        var plugin = new PythonSandboxAgentPlugin();

        // Act
        var methods = plugin.GetMethods();
        var schema = plugin.GetConfigurationSchema();

        // Assert
        Assert.AreEqual("PythonSandbox", plugin.Id);
        Assert.AreEqual(PluginCategory.BuiltInRequired, plugin.Category);
        Assert.AreEqual(1, methods.Count);
        Assert.AreEqual("ExecutePythonScript", methods[0].Id);

        var keys = schema.Select(s => s.Key).ToHashSet();
        Assert.IsTrue(keys.Contains("ImageName"));
        Assert.IsTrue(keys.Contains("TimeoutSeconds"));
        Assert.IsTrue(keys.Contains("MemoryLimitMb"));
        Assert.IsTrue(keys.Contains("MaxOutputMb"));
        Assert.IsTrue(keys.Contains("CpuQuota"));
    }

    private static byte[] MakeDockerFrame(byte streamType, byte[] payload)
    {
        var header = new byte[8];
        header[0] = streamType; // 1 = stdout, 2 = stderr
        header[4] = (byte)((payload.Length >> 24) & 0xFF);
        header[5] = (byte)((payload.Length >> 16) & 0xFF);
        header[6] = (byte)((payload.Length >> 8) & 0xFF);
        header[7] = (byte)(payload.Length & 0xFF);
        return header.Concat(payload).ToArray();
    }

    [TestMethod]
    public async Task ReadBoundedOutputAsync_WithShortStreams_ReadsCompleteOutput()
    {
        // Arrange
        var stdoutBytes = "Hello from Python!\n"u8.ToArray();
        var frame = MakeDockerFrame(1, stdoutBytes);
        using var stream = new MemoryStream(frame);

        // Act
        var (stdout, stderr) = await ExecutePythonScriptMethod.ReadBoundedOutputAsync(
            stream, 1024 * 1024, CancellationToken.None);

        // Assert
        StringAssert.Contains(stdout, "Hello from Python!\n");
        Assert.IsTrue(string.IsNullOrEmpty(stderr));
    }

    [TestMethod]
    public async Task ReadBoundedOutputAsync_WhenTruncated_AppendsTruncationNotice()
    {
        // Arrange
        var longBytes = new byte[2048];
        Array.Fill(longBytes, (byte)'A');
        var frame = MakeDockerFrame(1, longBytes);
        using var stream = new MemoryStream(frame);

        // Act - Max 512 bytes limit
        var (stdout, _) = await ExecutePythonScriptMethod.ReadBoundedOutputAsync(
            stream, 512, CancellationToken.None);

        // Assert
        StringAssert.Contains(stdout, "[OUTPUT TRUNCATED");
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenDockerDaemonUnreachable_ReturnsErrorMessage()
    {
        // Arrange
        using var fixture = new TestWorkspaceFixture();
        var contextMock = new Mock<IPluginContext>();
        contextMock.Setup(c => c.WorkspacePath).Returns(fixture.WorkspacePath);
        contextMock.Setup(c => c.SessionId).Returns(fixture.SessionId);
        contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string>
        {
            ["DockerUri"] = "http://127.0.0.1:59999" // Non-existent docker port
        });

        var plugin = new PythonSandboxAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "ExecutePythonScript");

        var args = new Dictionary<string, object?>
        {
            ["script"] = "print('Hello world')"
        };

        // Act
        var result = await method.ExecuteAsync(args, contextMock.Object, CancellationToken.None);

        // Assert - Should catch exception and return formatted error message
        StringAssert.Contains(result, "Failed to execute docker integration:");
    }

    [TestMethod]
    public async Task PythonSandboxAgentPlugin_InitializeAsync_WhenDockerUnreachable_DoesNotThrow()
    {
        // Arrange
        using var fixture = new TestWorkspaceFixture();
        var contextMock = new Mock<IPluginContext>();
        contextMock.Setup(c => c.WorkspacePath).Returns(fixture.WorkspacePath);
        contextMock.Setup(c => c.SessionId).Returns(fixture.SessionId);
        contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string>
        {
            ["DockerUri"] = "http://127.0.0.1:59998"
        });

        var plugin = new PythonSandboxAgentPlugin();

        // Act & Assert - InitializeAsync should complete cleanly without throwing
        await plugin.InitializeAsync(contextMock.Object);
    }

    [TestMethod]
    public async Task ExecuteAsync_MissingScriptArgument_ThrowsArgumentException()
    {
        // Arrange
        var plugin = new PythonSandboxAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "ExecutePythonScript");
        var contextMock = new Mock<IPluginContext>();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            method.ExecuteAsync(new Dictionary<string, object?>(), contextMock.Object, CancellationToken.None));
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenWorkspaceDirDoesNotExist_CreatesDirectoryOnHost()
    {
        // Arrange
        var tempNonExistentPath = Path.Combine(Path.GetTempPath(), "SabSandboxTest_" + Guid.NewGuid().ToString("N"));
        Assert.IsFalse(Directory.Exists(tempNonExistentPath));

        try
        {
            var contextMock = new Mock<IPluginContext>();
            contextMock.Setup(c => c.WorkspacePath).Returns(tempNonExistentPath);
            contextMock.Setup(c => c.SessionId).Returns(Guid.NewGuid());
            contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string>
            {
                ["DockerUri"] = "http://127.0.0.1:59997" // Unreachable docker to exit early after host prep
            });

            var plugin = new PythonSandboxAgentPlugin();
            var method = plugin.GetMethods().First(m => m.Id == "ExecutePythonScript");

            var args = new Dictionary<string, object?>
            {
                ["script"] = "print('Hello')"
            };

            // Act
            await method.ExecuteAsync(args, contextMock.Object, CancellationToken.None);

            // Assert: The directory MUST exist on the host now
            Assert.IsTrue(Directory.Exists(tempNonExistentPath), "Host workspace directory should have been created before Docker bind mount.");
        }
        finally
        {
            if (Directory.Exists(tempNonExistentPath))
            {
                Directory.Delete(tempNonExistentPath, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow(50, 52428800)]
    [DataRow(2048, int.MaxValue)]
    [DataRow(5000, int.MaxValue)]
    [DataRow(0, 1024 * 1024)]
    [DataRow(-10, 1024 * 1024)]
    public void CalculateMaxOutputBytes_HandlesExtremesAndAvoidsOverflow(int maxOutputMb, int expectedBytes)
    {
        // Act
        var result = ExecutePythonScriptMethod.CalculateMaxOutputBytes(maxOutputMb);

        // Assert
        Assert.AreEqual(expectedBytes, result);
    }

    [TestMethod]
    public void CleanupOrphanedContainers_UsesAtomicTicksField()
    {
        var field = typeof(ExecutePythonScriptMethod).GetField("_lastCleanupTicks", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(field, "Expected _lastCleanupTicks 64-bit integer field for atomic reads/writes.");
        Assert.AreEqual(typeof(long), field.FieldType, "_lastCleanupTicks must be of type long for atomic Interlocked operations.");
    }

    [TestMethod]
    public async Task CleanupOrphanedContainers_ConcurrentCalls_ThreadSafeAndAtomic()
    {
        using var client = new Docker.DotNet.DockerClientConfiguration(new Uri("http://127.0.0.1:59997")).CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            await ExecutePythonScriptMethod.CleanupOrphanedContainersAsync(client, cts.Token);
        }));

        await Task.WhenAll(tasks);

        var field = typeof(ExecutePythonScriptMethod).GetField("_lastCleanupTicks", BindingFlags.NonPublic | BindingFlags.Static)!;
        var ticks = (long)field.GetValue(null)!;
        Assert.IsTrue(ticks > 0, "Last cleanup ticks must be updated after cleanup execution.");
    }

    [TestMethod]
    public void GenerateDiagnosticHint_WhenFStringQuoteCollision_ReturnsActionableQuoteHint()
    {
        // Arrange
        var stderr = "  File \"<string>\", line 9\n    print(f'Error: {'.path' if os.path.exists(f) else 'False'}')\n                    ^\nSyntaxError: f-string: expecting '}'";

        // Act
        var hint = ExecutePythonScriptMethod.GenerateDiagnosticHint(stderr);

        // Assert
        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "cannot contain unescaped single quotes");
        StringAssert.Contains(hint, "Use double quotes");
    }

    [TestMethod]
    public void GenerateDiagnosticHint_WhenFStringBackslash_ReturnsBackslashHint()
    {
        // Arrange
        var stderr = "  File \"<string>\", line 4\n    s = f'{row[\"a\\nb\"]}'\nSyntaxError: f-string expression part cannot include a backslash";

        // Act
        var hint = ExecutePythonScriptMethod.GenerateDiagnosticHint(stderr);

        // Assert
        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "backslashes are not allowed inside f-string");
    }

    [TestMethod]
    public void GenerateDiagnosticHint_WhenIndentationError_ReturnsIndentHint()
    {
        // Arrange
        var stderr = "  File \"<string>\", line 35\n    df[id_col] = df[id_col].strip()\nIndentationError: expected an indented block after 'else' statement";

        // Act
        var hint = ExecutePythonScriptMethod.GenerateDiagnosticHint(stderr);

        // Assert
        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "cannot consist solely of comments");
        StringAssert.Contains(hint, "Add 'pass'");
    }

    [TestMethod]
    public void GenerateDiagnosticHint_WhenNameErrorOrKeyError_ReturnsSchemaInspectionHint()
    {
        // Arrange
        var nameErr = "NameError: name 'merged' is not defined";
        var keyErr = "KeyError: \"['ax'] not in index\"";

        // Act & Assert
        var hint1 = ExecutePythonScriptMethod.GenerateDiagnosticHint(nameErr);
        Assert.IsNotNull(hint1);
        StringAssert.Contains(hint1, "print(df.columns)");

        var hint2 = ExecutePythonScriptMethod.GenerateDiagnosticHint(keyErr);
        Assert.IsNotNull(hint2);
        StringAssert.Contains(hint2, "print(df.columns)");
    }

    [TestMethod]
    public void GenerateDiagnosticHint_WhenNullOrBenignWarning_ReturnsNull()
    {
        Assert.IsNull(ExecutePythonScriptMethod.GenerateDiagnosticHint(null));
        Assert.IsNull(ExecutePythonScriptMethod.GenerateDiagnosticHint(""));
        Assert.IsNull(ExecutePythonScriptMethod.GenerateDiagnosticHint("FutureWarning: The default of observed=False is deprecated"));
    }

    [TestMethod]
    public void GenerateDiagnosticHint_WhenExpectedColon_ReturnsColonHint()
    {
        var stderr = "  File \"<string>\", line 42\n    for _, row in df.iterrows()\n                              ^\nSyntaxError: expected ':'";
        var hint = ExecutePythonScriptMethod.GenerateDiagnosticHint(stderr);
        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "missing a trailing colon ':'");
    }

    [TestMethod]
    public void GenerateDiagnosticHint_WhenInvalidOpenMode_ReturnsOpenModeHint()
    {
        var stderr = "ValueError: invalid mode: 'n'";
        var hint = ExecutePythonScriptMethod.GenerateDiagnosticHint(stderr);
        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "Invalid file mode in open()");
    }

    [TestMethod]
    public void GenerateDiagnosticHint_WhenFStringQuoteMismatch_ReturnsFStringHint()
    {
        var stderr = "SyntaxError: closing parenthesis '}' does not match opening parenthesis '('";
        var hint = ExecutePythonScriptMethod.GenerateDiagnosticHint(stderr);
        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "f-strings");
    }

    [TestMethod]
    public void GenerateDiagnosticHint_WhenZeroDivisionOrFileNotFound_ReturnsAppropriateHints()
    {
        var zeroErr = "ZeroDivisionError: division by zero";
        var hint1 = ExecutePythonScriptMethod.GenerateDiagnosticHint(zeroErr);
        Assert.IsNotNull(hint1);
        StringAssert.Contains(hint1, "Division by zero");

        var fnfErr = "FileNotFoundError: [Errno 2] No such file or directory: '/app/workdir/data.csv'";
        var hint2 = ExecutePythonScriptMethod.GenerateDiagnosticHint(fnfErr);
        Assert.IsNotNull(hint2);
        StringAssert.Contains(hint2, "File not found");
    }
}


