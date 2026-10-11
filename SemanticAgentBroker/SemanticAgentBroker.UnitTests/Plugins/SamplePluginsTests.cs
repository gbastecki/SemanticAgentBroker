using Moq;
using SemanticAgentBroker.Plugins.MockData;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Plugins;

[TestClass]
public class SamplePluginsTests
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

    // ── SampleDataAgentPlugin ────────────────────────────────────────────────

    [TestMethod]
    public async Task SampleData_GetSalesData_ReturnsCsvByDefault()
    {
        // Arrange
        var plugin = new SampleDataAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetSalesData");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Data saved to /app/workdir/GetSalesData_output.csv");
        var savedFile = Path.Combine(_workspaceFixture.WorkspacePath, "GetSalesData_output.csv");
        Assert.IsTrue(File.Exists(savedFile));
        var content = await File.ReadAllTextAsync(savedFile);
        StringAssert.Contains(content, "Date,Product,Category,Quantity,UnitPrice,Revenue,Region");
        StringAssert.Contains(content, "Widget A");
    }

    [TestMethod]
    public async Task SampleData_GetSalesData_ReturnsJsonWhenRequested()
    {
        // Arrange
        var plugin = new SampleDataAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetSalesData");
        var args = new Dictionary<string, object?> { ["format"] = "json" };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Data saved to /app/workdir/GetSalesData_output.json");
        var savedFile = Path.Combine(_workspaceFixture.WorkspacePath, "GetSalesData_output.json");
        Assert.IsTrue(File.Exists(savedFile));
        var content = await File.ReadAllTextAsync(savedFile);
        StringAssert.Contains(content, "\"Product\":");
        StringAssert.Contains(content, "\"Revenue\":");
    }

    [TestMethod]
    public async Task SampleData_GetServerLogs_ReturnsLogsMatchingSeverity()
    {
        // Arrange
        var plugin = new SampleDataAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetServerLogs");
        var args = new Dictionary<string, object?> { ["level"] = "ERROR" };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Data saved to /app/workdir/GetServerLogs_output.log");
        var savedFile = Path.Combine(_workspaceFixture.WorkspacePath, "GetServerLogs_output.log");
        Assert.IsTrue(File.Exists(savedFile));
        var content = await File.ReadAllTextAsync(savedFile);
        StringAssert.Contains(content, "[ERROR]");
    }

    [TestMethod]
    public async Task SampleData_GetUserList_ReturnsUsers()
    {
        // Arrange
        var plugin = new SampleDataAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetUserList");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Alice");
        StringAssert.Contains(result, "@example.com");
        StringAssert.Contains(result, "Department");
    }

    [TestMethod]
    public async Task SampleData_GetProductCatalog_ReturnsCatalog()
    {
        // Arrange
        var plugin = new SampleDataAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetProductCatalog");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Wireless Headphones Pro");
        StringAssert.Contains(result, "USB-C Hub 7-in-1");
        StringAssert.Contains(result, "Categories");
    }

    // ── SampleSensorsPlugin ──────────────────────────────────────────────────

    [TestMethod]
    public async Task SampleSensors_GetSensors_ReturnsAllOrFilteredSensors()
    {
        // Arrange
        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetSensors");
        var args = new Dictionary<string, object?> { ["sensorType"] = "Temperature" };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Data saved to /app/workdir/GetSensors_output.json");
        var savedFile = Path.Combine(_workspaceFixture.WorkspacePath, "GetSensors_output.json");
        Assert.IsTrue(File.Exists(savedFile));
        var content = await File.ReadAllTextAsync(savedFile);
        StringAssert.Contains(content, "TEMP-");
        Assert.IsFalse(content.Contains("\"SensorType\": \"Humidity\""));
    }

    [TestMethod]
    public async Task SampleSensors_SearchSensors_FindsRoomSensors()
    {
        // Arrange
        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "SearchSensors");
        var args = new Dictionary<string, object?> { ["query"] = "Server Room" };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "Data saved to /app/workdir/SearchSensors_output.json");
        var savedFile = Path.Combine(_workspaceFixture.WorkspacePath, "SearchSensors_output.json");
        Assert.IsTrue(File.Exists(savedFile));
        var content = await File.ReadAllTextAsync(savedFile);
        StringAssert.Contains(content, "Server Room");
        StringAssert.Contains(content, "TEMP-");
    }

    [TestMethod]
    public async Task SampleSensors_GetReadings_InvalidDates_ReturnsError()
    {
        // Arrange
        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetReadings");
        var args = new Dictionary<string, object?>
        {
            ["sensorIdsOrFilePath"] = "TEMP-1101",
            ["fromUtc"] = "2026-04-25T00:00:00Z",
            ["toUtc"] = "2026-04-24T00:00:00Z" // to < from
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Error: 'toUtc' must be after 'fromUtc'.");
    }

    [TestMethod]
    public async Task SampleSensors_GetReadings_ValidRange_ReturnsDeterministicReadings()
    {
        // Arrange
        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetReadings");
        var args = new Dictionary<string, object?>
        {
            ["sensorIdsOrFilePath"] = "TEMP-1101",
            ["fromUtc"] = "2026-04-24T00:00:00Z",
            ["toUtc"] = "2026-04-24T02:00:00Z"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "SensorId,Timestamp,Value");
        StringAssert.Contains(result, "TEMP-1101");
    }

    [TestMethod]
    public async Task SampleSensors_GetReadings_DateWithoutZ_AssumesUniversalTime()
    {
        // Arrange
        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetReadings");
        var args = new Dictionary<string, object?>
        {
            ["sensorIdsOrFilePath"] = "TEMP-1101",
            ["fromUtc"] = "2026-04-24T00:00:00",
            ["toUtc"] = "2026-04-24T02:00:00"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "SensorId,Timestamp,Value");
        StringAssert.Contains(result, "TEMP-1101");
    }

    [TestMethod]
    public async Task SampleSensors_GetReadings_InvalidDateFormat_ReturnsError()
    {
        // Arrange
        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetReadings");
        var args = new Dictionary<string, object?>
        {
            ["sensorIdsOrFilePath"] = "TEMP-1101",
            ["fromUtc"] = "invalid-date-format",
            ["toUtc"] = "2026-04-24T02:00:00Z"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Error: Invalid fromUtc format");
    }

    [TestMethod]
    public async Task SampleSensors_GetReadings_ExceedingSevenDaysLimit_ReturnsError()
    {
        // Arrange
        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetReadings");
        var args = new Dictionary<string, object?>
        {
            ["sensorIdsOrFilePath"] = "TEMP-1101",
            ["fromUtc"] = "2026-04-01T00:00:00Z",
            ["toUtc"] = "2026-04-20T00:00:00Z" // 19 days (> 7 days)
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Error: Maximum range is 7 days.");
    }

    [TestMethod]
    public async Task SampleSensors_GetReadings_FromFileInWorkspace_ParsesSensorIdsAndReturnsReadings()
    {
        // Arrange - write sensor list to workspace file
        var fileContent = "Target sensors to analyze:\n- TEMP-1101\n- HUM-1101\n";
        var fileName = "target_sensors.txt";
        await File.WriteAllTextAsync(Path.Combine(_workspaceFixture.WorkspacePath, fileName), fileContent);

        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetReadings");
        var args = new Dictionary<string, object?>
        {
            ["sensorIdsOrFilePath"] = $"/app/workdir/{fileName}",
            ["fromUtc"] = "2026-04-24T00:00:00Z",
            ["toUtc"] = "2026-04-24T02:00:00Z"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "SensorId,Timestamp,Value");
        StringAssert.Contains(result, "TEMP-1101");
        StringAssert.Contains(result, "HUM-1101");
    }

    [TestMethod]
    public async Task SampleSensors_GetReadings_WithRelativeWorkspaceFileName_ParsesSensorIdsAndReturnsReadings()
    {
        // Arrange - write sensor list to workspace file with relative name
        var fileContent = "Target sensors to analyze:\n- TEMP-1101\n- HUM-1101\n";
        var fileName = "relative_sensors.txt";
        await File.WriteAllTextAsync(Path.Combine(_workspaceFixture.WorkspacePath, fileName), fileContent);

        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetReadings");
        var args = new Dictionary<string, object?>
        {
            ["sensorIdsOrFilePath"] = fileName, // Relative filename without /app/workdir/ or C:\
            ["fromUtc"] = "2026-04-24T00:00:00Z",
            ["toUtc"] = "2026-04-24T02:00:00Z"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "SensorId,Timestamp,Value");
        StringAssert.Contains(result, "TEMP-1101");
        StringAssert.Contains(result, "HUM-1101");
    }

    [TestMethod]
    public async Task SampleSensors_GetReadings_MissingWorkspaceFile_ReturnsFileNotFoundError()
    {
        // Arrange
        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetReadings");
        var args = new Dictionary<string, object?>
        {
            ["sensorIdsOrFilePath"] = "/app/workdir/missing_sensor_list.txt",
            ["fromUtc"] = "2026-04-24T00:00:00Z",
            ["toUtc"] = "2026-04-24T02:00:00Z"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Error: File '/app/workdir/missing_sensor_list.txt' not found in workspace.");
    }

    [TestMethod]
    public async Task SampleSensors_SearchSensors_WhenNoMatchingSensors_ReturnsNoSensorsFoundMessage()
    {
        // Arrange
        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "SearchSensors");
        var args = new Dictionary<string, object?>
        {
            ["query"] = "NonExistentRoomXYZ999"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "No sensors found matching 'NonExistentRoomXYZ999'.");
    }

    // ── SampleImagesPlugin ───────────────────────────────────────────────────

    [TestMethod]
    public void SampleImages_Tasks_DefinesWorkspaceImageAnalysisTask()
    {
        // Arrange
        var plugin = new SampleImagesPlugin();

        // Act
        var tasks = plugin.GetTasks();

        // Assert
        Assert.AreEqual(1, tasks.Count);
        Assert.AreEqual("AnalyzeWorkspaceImages", tasks[0].Id);
        Assert.AreEqual("ComputerVision", tasks[0].ExternalMethods[0].PluginId);
        Assert.AreEqual("AnalyzeImage", tasks[0].ExternalMethods[0].MethodId);
    }

    [TestMethod]
    public void SampleData_GetTasks_DefinesExpectedTasks()
    {
        // Arrange
        var plugin = new SampleDataAgentPlugin();

        // Act
        var tasks = plugin.GetTasks();

        // Assert
        Assert.AreEqual(2, tasks.Count);
        Assert.IsTrue(tasks.Any(t => t.Id == "SalesAnalysis" && t.AllowedMethods.Contains("GetSalesData")));
        Assert.IsTrue(tasks.Any(t => t.Id == "ServerLogDiag" && t.AllowedMethods.Contains("GetServerLogs")));
    }

    [TestMethod]
    public void SampleSensors_GetTasks_DefinesSensorAnalysisTask()
    {
        // Arrange
        var plugin = new SampleSensorsPlugin();

        // Act
        var tasks = plugin.GetTasks();

        // Assert
        Assert.AreEqual(1, tasks.Count);
        var task = tasks[0];
        Assert.AreEqual("SensorAnalysis", task.Id);
        CollectionAssert.AreEqual(new[] { "GetSensors", "SearchSensors", "GetReadings" }, (System.Collections.ICollection)task.AllowedMethods);
        Assert.IsFalse(string.IsNullOrEmpty(task.SystemInstruction));
    }

    [TestMethod]
    public async Task SampleSensors_GetReadings_WithBackslashPath_ReadsSensorIdsFromWorkspaceFile()
    {
        // Arrange
        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetReadings");

        var sensorsFile = Path.Combine(_workspaceFixture.WorkspacePath, "sensors.txt");
        await File.WriteAllTextAsync(sensorsFile, "TEMP-1001, HUM-2001");

        var args = new Dictionary<string, object?>
        {
            ["sensorIdsOrFilePath"] = @"workdir\nested\sensors.txt",
            ["fromUtc"] = "2026-04-24T00:00:00Z",
            ["toUtc"] = "2026-04-25T00:00:00Z"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert - should successfully find sensors.txt and extract readings, not error with file not found
        StringAssert.Contains(result, "SensorId,Timestamp,Value");
    }

    [TestMethod]
    public async Task SampleSensors_GetReadings_FileInSubdirectory_ReadsSensorIdsCorrectly()
    {
        // Arrange
        var plugin = new SampleSensorsPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "GetReadings");

        var subDir = Path.Combine(_workspaceFixture.WorkspacePath, "nested");
        Directory.CreateDirectory(subDir);
        var sensorsFile = Path.Combine(subDir, "sub_sensors.txt");
        await File.WriteAllTextAsync(sensorsFile, "TEMP-9999");

        var args = new Dictionary<string, object?>
        {
            ["sensorIdsOrFilePath"] = @"nested/sub_sensors.txt",
            ["fromUtc"] = "2026-04-24T00:00:00Z",
            ["toUtc"] = "2026-04-25T00:00:00Z"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "TEMP-9999");
    }
}
