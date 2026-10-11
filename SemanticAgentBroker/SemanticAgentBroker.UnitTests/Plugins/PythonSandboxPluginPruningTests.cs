using SemanticAgentBroker.Plugins.BuiltIn.Required;
using System.Text;

namespace SemanticAgentBroker.UnitTests.Plugins;

[TestClass]
public class PythonSandboxPluginPruningTests
{
    [TestMethod]
    public async Task ReadBoundedOutputAsync_WithStandardStream_ExtractsStdout()
    {
        // Arrange
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Calculated mean: 42.5\nStatus: SUCCESS"));

        // Act
        var (stdout, stderr) = await ExecutePythonScriptMethod.ReadBoundedOutputAsync(stream, maxBytes: 1024 * 1024, CancellationToken.None);

        // Assert
        StringAssert.Contains(stdout, "Calculated mean: 42.5");
        StringAssert.Contains(stdout, "Status: SUCCESS");
        Assert.AreEqual(string.Empty, stderr);
    }

    [TestMethod]
    public async Task ReadBoundedOutputAsync_WhenOutputExceedsMaxBytes_TruncatesAndAppendsWarning()
    {
        // Arrange - Max bytes = 100 bytes
        string largeOutput = new string('X', 500);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(largeOutput));

        // Act
        var (stdout, stderr) = await ExecutePythonScriptMethod.ReadBoundedOutputAsync(stream, maxBytes: 100, CancellationToken.None);

        // Assert
        Assert.IsTrue(stdout.Length <= 300); // 100 chars + warning message
        StringAssert.Contains(stdout, "[OUTPUT TRUNCATED: Output exceeded");
    }

    [TestMethod]
    public void PythonSandboxPlugin_ConfigurationSchema_ContainsAllExpectedKeys()
    {
        // Arrange
        var plugin = new PythonSandboxAgentPlugin();

        // Act
        var schema = plugin.GetConfigurationSchema();

        // Assert
        Assert.IsTrue(schema.Any(c => c.Key == "ImageName"));
        Assert.IsTrue(schema.Any(c => c.Key == "TimeoutSeconds"));
        Assert.IsTrue(schema.Any(c => c.Key == "MemoryLimitMb"));
        Assert.IsTrue(schema.Any(c => c.Key == "MaxOutputMb"));
        Assert.IsTrue(schema.Any(c => c.Key == "DockerUri"));
        Assert.IsTrue(schema.Any(c => c.Key == "ContainerDataPath"));
        Assert.IsTrue(schema.Any(c => c.Key == "CpuQuota"));
        Assert.IsTrue(schema.Any(c => c.Key == "CpuPeriod"));
    }
}
