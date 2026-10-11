using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SemanticAgentBroker.Core.Features.TaskState;
using SemanticAgentBroker.Core.Features.TaskState.Implementations;
using SemanticAgentBroker.Core.Features.TokenCounting;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Core.Features.TaskState;

[TestClass]
public class TaskStateManagerTests
{
    private TestWorkspaceFixture _workspaceFixture = null!;
    private Mock<ITokenCounter> _tokenCounterMock = null!;
    private TaskStateManager _stateManager = null!;

    [TestInitialize]
    public void Setup()
    {
        _workspaceFixture = new TestWorkspaceFixture();
        _tokenCounterMock = new Mock<ITokenCounter>();
        _tokenCounterMock.Setup(t => t.CountTokens(It.IsAny<string>(), It.IsAny<string>())).Returns(100);

        var aiOptions = new AiProviderOptions
        {
            ContextCompaction = new ContextCompactionOptions
            {
                MaxStateInjectionTokens = 4000,
                RecentStepFullObservationChars = 1000,
                RecentObservationWindowSize = 5
            }
        };

        _stateManager = new TaskStateManager(
            _workspaceFixture.SystemOptions,
            aiOptions,
            _tokenCounterMock.Object,
            NullLogger<TaskStateManager>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _stateManager.CleanupSession(_workspaceFixture.SessionId);
        _workspaceFixture.Dispose();
    }

    [TestMethod]
    public async Task InitializeStateAsync_CreatesStateFilesAndStoresObjective()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        string objective = "Analyze sensor telemetry dataset";

        // Act
        await _stateManager.InitializeStateAsync(sessionId, objective, CancellationToken.None);

        // Assert
        var snapshot = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.AreEqual(objective, snapshot.Objective);
        Assert.AreEqual(0, snapshot.CompletedSteps.Count);

        string jsonPath = Path.Combine(_workspaceFixture.WorkspacePath, "_task_state.json");
        string mdPath = Path.Combine(_workspaceFixture.WorkspacePath, "_task_state.md");
        Assert.IsTrue(File.Exists(jsonPath));
        Assert.IsTrue(File.Exists(mdPath));
    }

    [TestMethod]
    public async Task RecordStepAsync_AppendsStepAndExtractsKeyFacts()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Test Objective", CancellationToken.None);

        // Act
        await _stateManager.RecordStepAsync(
            sessionId,
            step: 1,
            action: "FileIO-SaveToWorkspace",
            actionInput: "{\"filename\": \"output.csv\"}",
            resultSummary: "File saved to /app/workdir/output.csv with 500 rows.",
            ct: CancellationToken.None);

        // Assert
        var snapshot = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.AreEqual(1, snapshot.CompletedSteps.Count);
        Assert.AreEqual("FileIO-SaveToWorkspace", snapshot.CompletedSteps[0].Action);
        Assert.IsFalse(snapshot.CompletedSteps[0].IsError);
        Assert.IsTrue(snapshot.KeyFacts.Any(f => f.Contains("/app/workdir/output.csv")));
    }

    [TestMethod]
    public async Task RecordStepAsync_DetectsErrorResults()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Error Test", CancellationToken.None);

        // Act
        await _stateManager.RecordStepAsync(
            sessionId,
            step: 1,
            action: "PythonSandbox-ExecuteScript",
            actionInput: "{}",
            resultSummary: "Error: ZeroDivisionError: division by zero",
            ct: CancellationToken.None);

        // Assert
        var snapshot = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.AreEqual(1, snapshot.CompletedSteps.Count);
        Assert.IsTrue(snapshot.CompletedSteps[0].IsError);
    }

    [TestMethod]
    public async Task RecordStepAsync_WhenPythonTracebackOrSyntaxError_DetectsError()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Python Error Test", CancellationToken.None);

        // Act - Step 1: Warning/Error output with traceback
        await _stateManager.RecordStepAsync(
            sessionId,
            step: 1,
            action: "PythonSandbox-ExecutePythonScript",
            actionInput: "{}",
            resultSummary: "Warning/Error output:\nTraceback (most recent call last):\n  File \"<string>\", line 8\nAttributeError: module 'os' has no attribute 'ps'",
            ct: CancellationToken.None);

        // Step 2: Exit code error with SyntaxError
        await _stateManager.RecordStepAsync(
            sessionId,
            step: 2,
            action: "PythonSandbox-ExecutePythonScript",
            actionInput: "{}",
            resultSummary: "Error: Python script execution failed (exit code 1):\nSyntaxError: f-string: expecting '}'",
            ct: CancellationToken.None);

        // Step 3: Success with harmless warnings
        await _stateManager.RecordStepAsync(
            sessionId,
            step: 3,
            action: "PythonSandbox-ExecutePythonScript",
            actionInput: "{}",
            resultSummary: "Warnings:\nFutureWarning: Some feature is deprecated\n\nStandard output:\nReport generated",
            ct: CancellationToken.None);

        // Assert
        var snapshot = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.AreEqual(3, snapshot.CompletedSteps.Count);
        Assert.IsTrue(snapshot.CompletedSteps[0].IsError, "Step 1 with traceback must be marked IsError=true");
        Assert.IsTrue(snapshot.CompletedSteps[1].IsError, "Step 2 with SyntaxError must be marked IsError=true");
        Assert.IsFalse(snapshot.CompletedSteps[2].IsError, "Step 3 with harmless warnings must be marked IsError=false");
    }

    [TestMethod]
    public async Task RecordStepAsync_DeduplicatesIdenticalSteps()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Dedup Test", CancellationToken.None);

        // Act - Record the same step twice
        await _stateManager.RecordStepAsync(sessionId, 1, "FileIO-SaveToWorkspace", "{}", "Success", CancellationToken.None);
        await _stateManager.RecordStepAsync(sessionId, 1, "FileIO-SaveToWorkspace", "{}", "Success", CancellationToken.None);

        // Assert
        var snapshot = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.AreEqual(1, snapshot.CompletedSteps.Count);
    }

    [TestMethod]
    public async Task BuildContextInjectionAsync_FormatsStructuredContextString()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Full Cycle", CancellationToken.None);
        await _stateManager.RecordStepAsync(sessionId, 1, "FileIO-SaveToWorkspace", "{}", "Saved", CancellationToken.None);

        // Act
        var context = await _stateManager.BuildContextInjectionAsync(sessionId, currentStep: 2, tokensUsed: 1000, maxTokens: 16000, compactionCount: 0, ct: CancellationToken.None);

        // Assert
        StringAssert.Contains(context, "[TASK PROGRESS - Step 2");
        StringAssert.Contains(context, "Objective: Full Cycle");
        StringAssert.Contains(context, "FileIO-SaveToWorkspace");
    }

    [TestMethod]
    public void SafeTruncate_PreservesSurrogatePairs()
    {
        // Arrange - contains emoji (surrogate pair)
        string text = "Processing task completed ✅ successfully";

        // Act
        string truncated = TaskStateManager.SafeTruncate(text, 28);

        // Assert
        Assert.IsTrue(truncated.Length <= 30);
        Assert.IsTrue(truncated.EndsWith("…"));
    }

    [TestMethod]
    public async Task RecordStepAsync_ExtractsKeyFactsWithComplexPathsAndPunctuation()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Complex Paths Test", CancellationToken.None);

        // Act
        await _stateManager.RecordStepAsync(
            sessionId,
            step: 1,
            action: "FileIO-SaveToWorkspace",
            actionInput: "{}",
            resultSummary: "Saved report to /app/workdir/annual report (2026).csv, and also /app/workdir/summary [final].json.",
            ct: CancellationToken.None);

        // Assert
        var snapshot = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.AreEqual(1, snapshot.CompletedSteps.Count);
        Assert.IsTrue(snapshot.KeyFacts.Any(f => f.Equals("File: /app/workdir/annual report (2026).csv", StringComparison.OrdinalIgnoreCase)),
            $"Actual facts: {string.Join(", ", snapshot.KeyFacts)}");
        Assert.IsTrue(snapshot.KeyFacts.Any(f => f.Equals("File: /app/workdir/summary [final].json", StringComparison.OrdinalIgnoreCase)),
            $"Actual facts: {string.Join(", ", snapshot.KeyFacts)}");
    }

    [TestMethod]
    public async Task AppendObjectiveAsync_AppendsNewObjectiveToExistingState()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Initial Objective", CancellationToken.None);

        // Act
        await _stateManager.AppendObjectiveAsync(sessionId, "Follow-up question to answer", CancellationToken.None);

        // Assert
        var snapshot = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        StringAssert.Contains(snapshot.Objective, "Initial Objective");
        StringAssert.Contains(snapshot.Objective, "Follow-up question to answer");
    }

    [TestMethod]
    public async Task GetMaxStepNumberAsync_ReturnsHighestStepRecorded()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Steps Test", CancellationToken.None);
        await _stateManager.RecordStepAsync(sessionId, 1, "ToolA", "{}", "Result 1", CancellationToken.None);
        await _stateManager.RecordStepAsync(sessionId, 3, "ToolB", "{}", "Result 3", CancellationToken.None);
        await _stateManager.RecordStepAsync(sessionId, 2, "ToolC", "{}", "Result 2", CancellationToken.None);

        // Act
        int maxStep = await _stateManager.GetMaxStepNumberAsync(sessionId, CancellationToken.None);

        // Assert
        Assert.AreEqual(3, maxStep);
    }

    [TestMethod]
    public async Task ReadStateAsync_WhenStateFileDoesNotExist_ReturnsEmptySnapshotWithoutThrowing()
    {
        // Arrange
        var nonExistentSessionId = Guid.NewGuid();

        // Act
        var snapshot = await _stateManager.ReadStateAsync(nonExistentSessionId, CancellationToken.None);

        // Assert
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(string.Empty, snapshot.Objective);
        Assert.AreEqual(0, snapshot.CompletedSteps.Count);
    }

    [TestMethod]
    public async Task FinalizeStateAsync_CreatesWalkthroughMarkdown()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Finalize Test", CancellationToken.None);
        await _stateManager.RecordStepAsync(sessionId, 1, "FileIO-Save", "{}", "Saved", CancellationToken.None);

        // Act
        await _stateManager.FinalizeStateAsync(sessionId, "Completed successfully with all calculations done.", CancellationToken.None);

        // Assert
        string walkthroughPath = Path.Combine(_workspaceFixture.WorkspacePath, "_walkthrough.md");
        Assert.IsTrue(File.Exists(walkthroughPath));
        string content = await File.ReadAllTextAsync(walkthroughPath);
        StringAssert.Contains(content, "Completed successfully with all calculations done.");
        StringAssert.Contains(content, "Finalize Test");
    }

    [TestMethod]
    public async Task UpdateFilesManifestAsync_ScansWorkspaceAndFormatsFileSizes()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Manifest Test", CancellationToken.None);

        // Create sample files in workspace: a byte-sized file and a 2KB file, plus an internal _ file
        await File.WriteAllTextAsync(Path.Combine(_workspaceFixture.WorkspacePath, "small.txt"), "hello world");
        var twoKbBytes = new byte[2048];
        await File.WriteAllBytesAsync(Path.Combine(_workspaceFixture.WorkspacePath, "two_kb.bin"), twoKbBytes);
        await File.WriteAllTextAsync(Path.Combine(_workspaceFixture.WorkspacePath, "_internal.tmp"), "skip me");

        // Act
        await _stateManager.UpdateFilesManifestAsync(sessionId, CancellationToken.None);

        // Assert
        var snapshot = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.IsTrue(snapshot.FilesManifest.ContainsKey("small.txt"));
        Assert.IsTrue(snapshot.FilesManifest.ContainsKey("two_kb.bin"));
        Assert.IsFalse(snapshot.FilesManifest.ContainsKey("_internal.tmp"));
        Assert.IsFalse(snapshot.FilesManifest.ContainsKey("_task_state.json"));

        Assert.AreEqual("11B", snapshot.FilesManifest["small.txt"]);
        Assert.AreEqual("2KB", snapshot.FilesManifest["two_kb.bin"]);
    }

    [TestMethod]
    public async Task IncrementCompactionCountAsync_IncrementsCompactionCounter()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Compaction Test", CancellationToken.None);

        var initial = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.AreEqual(0, initial.CompactionCount);

        // Act
        await _stateManager.IncrementCompactionCountAsync(sessionId, CancellationToken.None);
        await _stateManager.IncrementCompactionCountAsync(sessionId, CancellationToken.None);

        // Assert
        var updated = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.AreEqual(2, updated.CompactionCount);
    }

    [TestMethod]
    public async Task FinalizeStateAsync_DoesNotPrematurelyRemoveSessionLock()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Lock Retention Test", CancellationToken.None);
        await _stateManager.RecordStepAsync(sessionId, 1, "ToolA", "in", "out", CancellationToken.None);

        // Act - finalize state
        await _stateManager.FinalizeStateAsync(sessionId, "Done", CancellationToken.None);

        // Subsequent read/write operations must still be safe and not throw
        var state = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.AreEqual("Completed", state.CurrentStatus);
        Assert.AreEqual("Lock Retention Test", state.Objective);

        // Concurrent operations should execute without concurrency exceptions
        var tasks = Enumerable.Range(0, 10).Select(_ => _stateManager.ReadStateAsync(sessionId, CancellationToken.None));
        await Task.WhenAll(tasks);
    }

    [TestMethod]
    public async Task CleanupSession_DuringConcurrentOperations_DoesNotThrowOrCorruptState()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Concurrent Cleanup Test", CancellationToken.None);

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Act: Run multiple concurrent state operations while invoking CleanupSession repeatedly
        var tasks = Enumerable.Range(1, 20).Select(async step =>
        {
            try
            {
                await _stateManager.RecordStepAsync(sessionId, step, $"Tool_{step}", "{}", $"Result_{step}", CancellationToken.None);
                if (step % 5 == 0)
                {
                    _stateManager.CleanupSession(sessionId);
                }
                await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        await Task.WhenAll(tasks);

        // Assert: No ObjectDisposedException or concurrency exceptions should have occurred
        Assert.AreEqual(0, exceptions.Count, $"Expected 0 exceptions but got: {string.Join(", ", exceptions.Select(e => e.ToString()))}");
    }

    [TestMethod]
    public async Task CleanupSession_DisposesSemaphoreSlim()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        await _stateManager.InitializeStateAsync(sessionId, "Cleanup Test", CancellationToken.None);

        // Inspect _sessionLocks via reflection
        var field = typeof(TaskStateManager).GetField("_sessionLocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var locksDict = (System.Collections.IDictionary)field.GetValue(null)!;
        Assert.IsTrue(locksDict.Contains(sessionId));

        var entry = locksDict[sessionId]!;
        var semField = entry.GetType().GetField("Semaphore", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        var sem = (SemaphoreSlim)semField.GetValue(entry)!;

        // Act - cleanup
        _stateManager.CleanupSession(sessionId);

        // Assert - entry removed and semaphore disposed
        Assert.IsFalse(locksDict.Contains(sessionId));
        Assert.Throws<ObjectDisposedException>(() => sem.Release());
    }

    [TestMethod]
    public async Task RecordStepAsync_WhenKeyFactsNullInDeserializedSnapshot_DoesNotThrow()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        var jsonStatePath = Path.Combine(_workspaceFixture.WorkspacePath, "_task_state.json");
        var jsonWithNullFacts = """
            {
                "Objective": "Test null facts",
                "CompletedSteps": [],
                "FilesManifest": {},
                "KeyFacts": null,
                "CompactionCount": 0,
                "CurrentStatus": "Active",
                "RecentObservations": []
            }
            """;
        await File.WriteAllTextAsync(jsonStatePath, jsonWithNullFacts);

        // Act & Assert - RecordStepAsync should safely handle null KeyFacts and not throw NullReferenceException
        await _stateManager.RecordStepAsync(sessionId, 1, "FileIO-SaveToWorkspace", "{}", "Saved /app/workdir/result.txt", CancellationToken.None);

        var state = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.IsNotNull(state.KeyFacts);
        Assert.IsTrue(state.KeyFacts.Any(f => f.Contains("result.txt")));
    }

    [TestMethod]
    public async Task WriteState_UsesAtomicWriteAndLeavesNoTempFiles()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        string objective = "Test atomic state write";

        // Act
        await _stateManager.InitializeStateAsync(sessionId, objective, CancellationToken.None);
        await _stateManager.RecordStepAsync(sessionId, 1, "Action1", "{}", "Result1", CancellationToken.None);

        // Assert
        var jsonStatePath = Path.Combine(_workspaceFixture.WorkspacePath, "_task_state.json");
        var mdStatePath = Path.Combine(_workspaceFixture.WorkspacePath, "_task_state.md");
        Assert.IsTrue(File.Exists(jsonStatePath), "Primary state file must exist.");
        Assert.IsTrue(File.Exists(mdStatePath), "Markdown state file must exist.");

        var json = await File.ReadAllTextAsync(jsonStatePath);
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<TaskStateSnapshot>(json);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(objective, snapshot.Objective);
        Assert.AreEqual(1, snapshot.CompletedSteps.Count);

        var tempFiles = Directory.GetFiles(_workspaceFixture.WorkspacePath, "*.tmp");
        Assert.AreEqual(0, tempFiles.Length, "Atomic writes must not leave temporary files behind.");
    }

    [TestMethod]
    public async Task UpdateFilesManifest_WhenFilesPresent_UpdatesManifestSafely()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Test Manifest", CancellationToken.None);

        var file1 = Path.Combine(_workspaceFixture.WorkspacePath, "keep.txt");
        await File.WriteAllTextAsync(file1, "hello world content");

        // Act
        await _stateManager.UpdateFilesManifestAsync(sessionId, CancellationToken.None);

        // Assert
        var state = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.IsTrue(state.FilesManifest.ContainsKey("keep.txt"));
        Assert.AreEqual("19B", state.FilesManifest["keep.txt"]);
    }

    [TestMethod]
    public async Task BuildContextInjection_Strategy3_CollapsesSubLinesButRetainsStepHeaders()
    {
        // Arrange - Record 7 steps to trigger compaction (>3 steps, token budget exceeded)
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Compaction Header Test", CancellationToken.None);

        for (int i = 1; i <= 7; i++)
        {
            await _stateManager.RecordStepAsync(sessionId, i, $"Tool_{i}",
                $"{{\"input\": \"value_{i}\"}}", $"Result from step {i}", CancellationToken.None);
        }

        // Configure token counter to return a value exceeding maxStateTokens to trigger Strategy 3
        _tokenCounterMock.Setup(t => t.CountTokens(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(5000); // Exceeds MaxStateInjectionTokens (4000)

        // Act
        var context = await _stateManager.BuildContextInjectionAsync(
            sessionId, currentStep: 8, tokensUsed: 1000, maxTokens: 16000, compactionCount: 0,
            ct: CancellationToken.None);

        // Assert - Old step headers (<= maxStep-3 = 4) must still be present
        StringAssert.Contains(context, "Tool_1", "Step 1 header must be retained after compaction.");
        StringAssert.Contains(context, "Tool_2", "Step 2 header must be retained after compaction.");
        StringAssert.Contains(context, "Tool_3", "Step 3 header must be retained after compaction.");
        StringAssert.Contains(context, "Tool_4", "Step 4 header must be retained after compaction.");

        // Recent steps (5, 6, 7) should also be present
        StringAssert.Contains(context, "Tool_5");
        StringAssert.Contains(context, "Tool_7");
    }

    [TestMethod]
    public async Task BuildContextInjection_MultipleActionsInStep_LabelsSucceededAndFailedAccurately()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Multi Action Status Test", CancellationToken.None);

        // Record two actions for step 1: one successful, one failed
        await _stateManager.RecordStepAsync(sessionId, 1, "FileIO-SaveToWorkspace", "{\"path\":\"a.txt\"}", "File saved successfully", CancellationToken.None);
        await _stateManager.RecordStepAsync(sessionId, 1, "FileIO-ReadFromWorkspace", "{\"path\":\"missing.txt\"}", "Error: File not found", CancellationToken.None);

        // Act
        var context = await _stateManager.BuildContextInjectionAsync(
            sessionId, currentStep: 2, tokensUsed: 100, maxTokens: 16000, compactionCount: 0,
            ct: CancellationToken.None);

        // Assert - Each action must be matched accurately by Action name, not just step number
        StringAssert.Contains(context, "Step 1 (FileIO-SaveToWorkspace) (Succeeded):");
        StringAssert.Contains(context, "Step 1 (FileIO-ReadFromWorkspace) (Failed):");
    }

    [TestMethod]
    public async Task RecordStepAsync_UserInterruption_PersistsInUserDirectivesAndIsNotError()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Directives test", CancellationToken.None);

        // Act
        await _stateManager.RecordStepAsync(
            sessionId,
            step: 1,
            action: "UserInterruption",
            actionInput: "",
            resultSummary: "Please focus only on CSV files.",
            ct: CancellationToken.None);

        // Assert
        var snapshot = await _stateManager.ReadStateAsync(sessionId, CancellationToken.None);
        Assert.AreEqual(1, snapshot.CompletedSteps.Count);
        Assert.IsFalse(snapshot.CompletedSteps[0].IsError, "UserInterruption must never be flagged as an error.");
        Assert.IsNotNull(snapshot.UserDirectives);
        Assert.AreEqual(1, snapshot.UserDirectives.Count);
        Assert.AreEqual("Please focus only on CSV files.", snapshot.UserDirectives[0]);

        // Verify markdown state file contains the User Directives section
        var mdPath = Path.Combine(_workspaceFixture.WorkspacePath, "_task_state.md");
        Assert.IsTrue(File.Exists(mdPath));
        var mdContent = await File.ReadAllTextAsync(mdPath);
        StringAssert.Contains(mdContent, "## User Directives");
        StringAssert.Contains(mdContent, "- Please focus only on CSV files.");

        // Verify JSON state file preserves UserDirectives on disk
        var jsonPath = Path.Combine(_workspaceFixture.WorkspacePath, "_task_state.json");
        Assert.IsTrue(File.Exists(jsonPath));
        var jsonContent = await File.ReadAllTextAsync(jsonPath);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<TaskStateSnapshot>(jsonContent);
        Assert.IsNotNull(deserialized);
        Assert.IsNotNull(deserialized.UserDirectives);
        Assert.AreEqual(1, deserialized.UserDirectives.Count);
        Assert.AreEqual("Please focus only on CSV files.", deserialized.UserDirectives[0]);
    }

    [TestMethod]
    public async Task BuildContextInjection_ActiveUserDirectives_RendersDirectivesProminently()
    {
        // Arrange
        var sessionId = _workspaceFixture.SessionId;
        await _stateManager.InitializeStateAsync(sessionId, "Active Directives Test", CancellationToken.None);
        await _stateManager.RecordStepAsync(
            sessionId,
            step: 0,
            action: "UserInterruption",
            actionInput: "",
            resultSummary: "Stop downloading PDFs, only use JSON.",
            ct: CancellationToken.None);

        // Act
        var context = await _stateManager.BuildContextInjectionAsync(
            sessionId, currentStep: 1, tokensUsed: 100, maxTokens: 16000, compactionCount: 0,
            ct: CancellationToken.None);

        // Assert - Active user directives must appear in the rendered context injection
        StringAssert.Contains(context, "## 🚨 Active User Directives");
        StringAssert.Contains(context, "- [USER DIRECTIVE]: Stop downloading PDFs, only use JSON.");
    }
}

