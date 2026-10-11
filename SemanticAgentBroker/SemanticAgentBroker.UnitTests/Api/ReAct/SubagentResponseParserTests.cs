using SemanticAgentBroker.Api.Features.ReAct.Services;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Api.ReAct;

[TestClass]
public class SubagentResponseParserTests
{
    private readonly AiProviderOptions _defaultOptions = new();
    private readonly List<string> _availableTools = ["FileIO-SaveToWorkspace", "FileIO-ReadFromWorkspace", "PythonSandbox-ExecuteScript"];

    [TestMethod]
    public void ParseResponse_WithStandardReactTokens_ExtractsAllComponents()
    {
        // Arrange
        string responseText = """
            Thought: I need to write data to a file.
            Plan:
            1. Save the items to items.json.
            2. Verify file exists.
            Action: FileIO-SaveToWorkspace
            ActionInput: {"filename": "items.json", "content": "[]"}
            """;

        // Act
        var result = SubagentResponseParser.ParseResponse(responseText, _defaultOptions, null, _availableTools);

        // Assert
        Assert.AreEqual("I need to write data to a file.", result.Thought);
        Assert.IsNotNull(result.Plan);
        StringAssert.Contains(result.Plan, "Save the items");
        Assert.AreEqual(1, result.ToolCalls.Count);
        Assert.AreEqual("FileIO-SaveToWorkspace", result.ToolCalls[0].ActionName);
        StringAssert.Contains(result.ToolCalls[0].ActionInput, "items.json");
        Assert.IsNull(result.FinalAnswer);
    }

    [TestMethod]
    public void ParseResponse_WithFinalAnswer_ExtractsFinalAnswer()
    {
        // Arrange
        string responseText = """
            Thought: The task is now complete.
            FinalAnswer: All operations were executed successfully. Total items: 42.
            """;

        // Act
        var result = SubagentResponseParser.ParseResponse(responseText, _defaultOptions, null, _availableTools);

        // Assert
        Assert.AreEqual("The task is now complete.", result.Thought);
        Assert.AreEqual(0, result.ToolCalls.Count);
        Assert.AreEqual("All operations were executed successfully. Total items: 42.", result.FinalAnswer);
    }

    [TestMethod]
    public void ParseResponse_WithMarkdownFormattedTokens_ExtractsCleanContent()
    {
        // Arrange
        string responseText = """
            Thought: We should execute python script.
            Action: PythonSandbox-ExecuteScript
            ActionInput: {"script": "print('hello')"}
            """;

        // Act
        var result = SubagentResponseParser.ParseResponse(responseText, _defaultOptions, null, _availableTools);

        // Assert
        Assert.AreEqual("We should execute python script.", result.Thought);
        Assert.AreEqual(1, result.ToolCalls.Count);
        Assert.AreEqual("PythonSandbox-ExecuteScript", result.ToolCalls[0].ActionName);
        StringAssert.Contains(result.ToolCalls[0].ActionInput, "print('hello')");
    }

    [TestMethod]
    public void ParseResponse_WithMultipleActions_ExtractsAllToolCalls()
    {
        // Arrange
        string responseText = """
            Thought: I need to write two files in parallel.
            Action: FileIO-SaveToWorkspace
            ActionInput: {"filename": "file1.txt", "content": "1"}
            Action: FileIO-SaveToWorkspace
            ActionInput: {"filename": "file2.txt", "content": "2"}
            """;

        // Act
        var result = SubagentResponseParser.ParseResponse(responseText, _defaultOptions, null, _availableTools);

        // Assert
        Assert.AreEqual(2, result.ToolCalls.Count);
        Assert.AreEqual("FileIO-SaveToWorkspace", result.ToolCalls[0].ActionName);
        StringAssert.Contains(result.ToolCalls[0].ActionInput, "file1.txt");
        Assert.AreEqual("FileIO-SaveToWorkspace", result.ToolCalls[1].ActionName);
        StringAssert.Contains(result.ToolCalls[1].ActionInput, "file2.txt");
    }

    [TestMethod]
    public void ParseResponse_WithoutExplicitActionKeyword_FallbacksToIntentExtraction()
    {
        // Arrange
        string responseText = "I will use PythonSandbox-ExecuteScript to calculate the fibonacci numbers.";

        // Act
        var result = SubagentResponseParser.ParseResponse(responseText, _defaultOptions, null, _availableTools);

        // Assert
        Assert.AreEqual(1, result.ToolCalls.Count);
        Assert.AreEqual("PythonSandbox-ExecuteScript", result.ToolCalls[0].ActionName);
    }

    [TestMethod]
    [DataRow("<channel|>Thought I am analyzing data", "I am analyzing data")]
    [DataRow("<|thought|> Running simulation", "Running simulation")]
    [DataRow("[THOUGHT] Validating outputs", "Validating outputs")]
    [DataRow("Thought: Processing request", "Processing request")]
    public void CleanThoughtText_StripsModelSpecificPrefixes(string input, string expected)
    {
        // Act
        string cleaned = SubagentResponseParser.CleanThoughtText(input, _defaultOptions, null);

        // Assert
        Assert.AreEqual(expected, cleaned);
    }

    [TestMethod]
    public void TryWrapRawCodeInput_WithPythonFencedBlock_WrapsIntoJsonScriptParam()
    {
        // Arrange
        string actionName = "PythonSandbox-ExecutePythonScript";
        string rawInput = "```python\nimport sys\nprint('Done')\n```";

        // Act
        string? wrapped = SubagentResponseParser.TryWrapRawCodeInput(actionName, rawInput);

        // Assert
        Assert.IsNotNull(wrapped);
        Assert.IsTrue(JsonSanitizer.IsValidJson(wrapped));
        StringAssert.Contains(wrapped, "\"script\"");
        StringAssert.Contains(wrapped, "import sys");
        StringAssert.Contains(wrapped, "Done");
    }

    [TestMethod]
    public void TryWrapRawCodeInput_WithExistingJsonObject_ReturnsNull()
    {
        // Arrange
        string actionName = "PythonSandbox-ExecutePythonScript";
        string rawInput = "{\"script\": \"print(1)\"}";

        // Act
        string? wrapped = SubagentResponseParser.TryWrapRawCodeInput(actionName, rawInput);

        // Assert
        Assert.IsNull(wrapped);
    }

    [TestMethod]
    public void ExtractToolNameFromText_WithPastTenseMentions_IgnoresCompletedTools()
    {
        // Arrange - Mentions two tools with past tense context on the last mention
        string content = "I already used FileIO-SaveToWorkspace previously. The results from PythonSandbox-ExecuteScript showed success.";

        // Act
        string? extracted = SubagentResponseParser.ExtractToolNameFromText(content, null, _availableTools);

        // Assert
        Assert.IsNull(extracted);
    }

    [TestMethod]
    public void ParseResponse_WithNonBreakingSpacesAndCorruptedPath_NormalizesContent()
    {
        // Arrange
        string responseText = """
            Thought: I need to process the data using Python.
            Action: PythonSandbox-ExecuteScript
            ActionInput: {"script": "import\u00A0pandas\u202Fas\u00A0pd\ndf = pd.read_csv('/app/<0xC2>dir/data.csv')"}
            """;

        // Act
        var result = SubagentResponseParser.ParseResponse(responseText, _defaultOptions, null, _availableTools);

        // Assert
        Assert.AreEqual(1, result.ToolCalls.Count);
        StringAssert.Contains(result.ToolCalls[0].ActionInput, "import pandas as pd");
        StringAssert.Contains(result.ToolCalls[0].ActionInput, "/app/workdir/data.csv");
    }

    [TestMethod]
    public void CleanThoughtText_RepetitiveLines_CollapsesExcessiveDuplicates()
    {
        // Arrange - degenerate repetition in thought
        string thought = """
            I am analyzing the columns.
            pivot.columns = [f'{col}' for col in pivot.columns]
            pivot.columns = [f'{col}' for col in pivot.columns]
            pivot.columns = [f'{col}' for col in pivot.columns]
            pivot.columns = [f'{col}' for col in pivot.columns]
            pivot.columns = [f'{col}' for col in pivot.columns]
            Now proceed to execute.
            """;

        // Act
        string cleaned = SubagentResponseParser.CleanThoughtText(thought, _defaultOptions, null);

        // Assert
        StringAssert.Contains(cleaned, "... [repetitive reasoning lines collapsed] ...");
        StringAssert.Contains(cleaned, "Now proceed to execute.");
    }

    [TestMethod]
    public void CleanThoughtText_StripsDeepSeekReasoningTokens()
    {
        // Arrange
        var customOptions = new AiProviderOptions
        {
            Inference = new InferenceOptions
            {
                ThoughtPrefixesToStrip = ["<think>", "</think>"]
            }
        };
        string thought = "<think>Analyzing the user prompt and figuring out the strategy.</think>I will now proceed with the plan.";

        // Act
        string cleaned = SubagentResponseParser.CleanThoughtText(thought, customOptions, null);

        // Assert
        Assert.IsFalse(cleaned.Contains("<think>"));
        Assert.IsFalse(cleaned.Contains("</think>"));
        StringAssert.Contains(cleaned, "I will now proceed with the plan.");
    }

    [TestMethod]
    public void TryWrapRawCodeInput_WithNonPythonAction_ReturnsNull()
    {
        // Arrange
        string actionName = "FileIO-SaveToWorkspace";
        string rawInput = "```json\n{\"test\": 1}\n```";

        // Act
        string? wrapped = SubagentResponseParser.TryWrapRawCodeInput(actionName, rawInput);

        // Assert
        Assert.IsNull(wrapped);
    }

    [TestMethod]
    public void TryWrapRawCodeInput_WithExecuteCommandAction_WrapsIntoCommandParameter()
    {
        // Arrange
        string actionName = "HostOperations-ExecuteCommand";
        string rawInput = "```bash\nls -la /tmp\n```";

        // Act
        string? wrapped = SubagentResponseParser.TryWrapRawCodeInput(actionName, rawInput);

        // Assert
        Assert.IsNotNull(wrapped);
        Assert.IsTrue(JsonSanitizer.IsValidJson(wrapped));
        StringAssert.Contains(wrapped, "\"command\"");
        StringAssert.Contains(wrapped, "ls -la /tmp");
    }

    [TestMethod]
    public void ExtractAllActionPairs_WithActionWithoutActionInput_ExtractsActionWithEmptyJsonObject()
    {
        // Arrange - LLM calls a parameterless tool without providing an ActionInput: line
        string responseText = """
            Thought: I need to list the available files first.
            Action: FileIO-ListWorkspaceFiles
            """;

        // Act
        var pairs = SubagentResponseParser.ExtractAllActionPairs(responseText);

        // Assert
        Assert.AreEqual(1, pairs.Count);
        Assert.AreEqual("FileIO-ListWorkspaceFiles", pairs[0].ActionName);
        Assert.AreEqual("{}", pairs[0].ActionInput);
    }

    [TestMethod]
    public void ExtractAllActionPairs_WithMultipleActionsSomeWithoutActionInput_ExtractsAllActions()
    {
        // Arrange - Mixed parameterless and parameterized actions in a single turn
        string responseText = """
            Thought: I will check the time and then save the report.
            Action: SystemInfo-GetCurrentDateTime
            Action: FileIO-SaveToWorkspace
            ActionInput: {"filename": "report.txt", "content": "Done"}
            Action: WorkspaceInfo-GetWorkspaceTree
            """;

        // Act
        var pairs = SubagentResponseParser.ExtractAllActionPairs(responseText);

        // Assert
        Assert.AreEqual(3, pairs.Count);
        Assert.AreEqual("SystemInfo-GetCurrentDateTime", pairs[0].ActionName);
        Assert.AreEqual("{}", pairs[0].ActionInput);
        Assert.AreEqual("FileIO-SaveToWorkspace", pairs[1].ActionName);
        StringAssert.Contains(pairs[1].ActionInput, "report.txt");
        Assert.AreEqual("WorkspaceInfo-GetWorkspaceTree", pairs[2].ActionName);
        Assert.AreEqual("{}", pairs[2].ActionInput);
    }

    [TestMethod]
    public void ExtractAllActionPairs_WithBacktickEnclosedToolName_ExtractsCleanActionName()
    {
        // Arrange - LLM wraps tool name in backticks
        string responseText = """
            Thought: I need to run a python script.
            Action: `PythonSandbox-ExecutePythonScript`
            ActionInput: {"script": "print('hello')"}
            """;

        // Act
        var pairs = SubagentResponseParser.ExtractAllActionPairs(responseText);

        // Assert
        Assert.AreEqual(1, pairs.Count);
        Assert.AreEqual("PythonSandbox-ExecutePythonScript", pairs[0].ActionName);
        StringAssert.Contains(pairs[0].ActionInput, "print('hello')");
    }

    [TestMethod]
    public void ExtractAllActionPairs_WithBacktickEnclosedActionToken_ExtractsActionCorrectly()
    {
        // Arrange - LLM wraps `Action:` and `ActionInput:` in backticks
        string responseText = """
            Thought: Using the file tool.
            `Action:` FileIO-ListWorkspaceFiles
            `ActionInput:` {}
            """;

        // Act
        var pairs = SubagentResponseParser.ExtractAllActionPairs(responseText);

        // Assert
        Assert.AreEqual(1, pairs.Count);
        Assert.AreEqual("FileIO-ListWorkspaceFiles", pairs[0].ActionName);
    }

    [TestMethod]
    public void ExtractAllActionPairs_WithFullLineCodeSpan_ExtractsActionCorrectly()
    {
        // Arrange - LLM puts entire line in code span
        string responseText = """
            Thought: Listing files.
            `Action: FileIO-ListWorkspaceFiles`
            `ActionInput: {}`
            """;

        // Act
        var pairs = SubagentResponseParser.ExtractAllActionPairs(responseText);

        // Assert
        Assert.AreEqual(1, pairs.Count);
        Assert.AreEqual("FileIO-ListWorkspaceFiles", pairs[0].ActionName);
    }

    [TestMethod]
    public void ExtractToolNameFromText_WithPastTenseCalled_DoesNotTriggerToolExecution()
    {
        // Arrange - Model says "I called tool", should NOT trigger re-execution of the tool
        string content = "I called FileIO-SaveToWorkspace in the previous turn and it was successful.";

        // Act
        string? tool = SubagentResponseParser.ExtractToolNameFromText(content, null, _availableTools);

        // Assert
        Assert.IsNull(tool);
    }

    [TestMethod]
    public void ExtractToolNameFromText_WithPastTenseCalledAndNewIntent_ExtractsNewIntentTool()
    {
        // Arrange - Model says "I called tool1, so now I am going to use tool2"
        string content = "I called FileIO-SaveToWorkspace, so now I am going to use PythonSandbox-ExecuteScript to process the results.";

        // Act
        string? tool = SubagentResponseParser.ExtractToolNameFromText(content, null, _availableTools);

        // Assert
        Assert.AreEqual("PythonSandbox-ExecuteScript", tool);
    }

    [TestMethod]
    public void ParseResponse_WithHallucinatedObservation_TruncatesAtObservation()
    {
        // Arrange - LLM hallucinates Observation: and fake FinalAnswer:
        string responseText = """
            Thought: Saving the results.
            Action: FileIO-SaveToWorkspace
            ActionInput: {"filename": "data.txt", "content": "123"}
            Observation: File was saved successfully.
            Thought: Now I can provide the answer.
            FinalAnswer: All done!
            """;

        // Act
        var result = SubagentResponseParser.ParseResponse(responseText, _defaultOptions, null, _availableTools);

        // Assert
        Assert.AreEqual(1, result.ToolCalls.Count);
        Assert.AreEqual("FileIO-SaveToWorkspace", result.ToolCalls[0].ActionName);
        Assert.AreEqual("{\"filename\": \"data.txt\", \"content\": \"123\"}", result.ToolCalls[0].ActionInput);
        Assert.IsNull(result.FinalAnswer, "Hallucinated FinalAnswer after Observation must be discarded.");
    }

    [TestMethod]
    public void ParseResponse_WithActionNoneInFinalAnswer_StripsTrailingActionFromFinalAnswer()
    {
        // Arrange - Model wrote FinalAnswer followed by Action: None
        string responseText = """
            Thought: Task complete.
            FinalAnswer: The computation result is 42.
            Action: None
            ActionInput: {}
            """;

        // Act
        var result = SubagentResponseParser.ParseResponse(responseText, _defaultOptions, null, _availableTools);

        // Assert
        Assert.AreEqual(0, result.ToolCalls.Count);
        Assert.AreEqual("The computation result is 42.", result.FinalAnswer);
    }

    [TestMethod]
    public void ParseResponse_WithFinalAnswerReferencingFileContainingToolName_DoesNotExtractPhantomToolCall()
    {
        // Arrange - Model provides FinalAnswer referencing a file auto-saved with the tool name
        string responseText = """
            Thought: I have successfully completed all the steps in the plan. The final report is saved in the workspace.
            FinalAnswer: The sensor data analysis for yesterday (2026-10-08) is complete. The detailed Markdown report, containing the minimum, maximum, and average temperature and dampness readings for each room, can be found at: `/app/workdir/PythonSandbox-ExecuteScript_step7_2fce6f_output.md`
            """;

        // Act
        var result = SubagentResponseParser.ParseResponse(responseText, _defaultOptions, null, _availableTools);

        // Assert
        Assert.AreEqual(0, result.ToolCalls.Count, "FinalAnswer must not be hijacked by tool name in file path.");
        Assert.IsNotNull(result.FinalAnswer);
        StringAssert.Contains(result.FinalAnswer, "/app/workdir/PythonSandbox-ExecuteScript_step7_2fce6f_output.md");
    }

    [TestMethod]
    public void ExtractToolNameFromText_WithToolNameEmbeddedInFilePath_ReturnsNull()
    {
        // Arrange - Tool name is part of a file path, not an active tool invocation intent
        string content = "The report was saved to /app/workdir/PythonSandbox-ExecuteScript_step7_2fce6f_output.md for user inspection.";

        // Act
        string? tool = SubagentResponseParser.ExtractToolNameFromText(content, null, _availableTools);

        // Assert
        Assert.IsNull(tool, "Tool name embedded in a file path must not be extracted as a tool call.");
    }

    [TestMethod]
    public void ExtractToolNameFromText_WithToolNameEmbeddedInWindowsFilePath_ReturnsNull()
    {
        // Arrange - Tool name in Windows path
        string content = @"The output is at C:\data\PythonSandbox-ExecuteScript_results.txt";

        // Act
        string? tool = SubagentResponseParser.ExtractToolNameFromText(content, null, _availableTools);

        // Assert
        Assert.IsNull(tool, "Tool name embedded in a Windows file path must not be extracted.");
    }

    [TestMethod]
    public void ExtractToolNameFromText_WithToolNameInPlanSection_IgnoresPlanAndDoesNotExtract()
    {
        // Arrange - Tool name is mentioned only inside the plan section
        string plan = "1. I will use PythonSandbox-ExecuteScript to process data.";
        string content = $"Thought: I am ready.\nPlan:\n{plan}";

        // Act
        string? tool = SubagentResponseParser.ExtractToolNameFromText(content, plan, _availableTools);

        // Assert
        Assert.IsNull(tool, "Tool name mentioned inside plan section must not be extracted as active execution intent.");
    }

    [TestMethod]
    public void IsStandaloneToolMention_WithVariousDelimiters_DetectsCorrectly()
    {
        string tool = "PythonSandbox-ExecuteScript";

        // Standalone mentions
        string valid1 = $"I will use {tool} now.";
        Assert.IsTrue(SubagentResponseParser.IsStandaloneToolMention(valid1, valid1.IndexOf(tool), tool.Length));

        string valid2 = $"`{tool}`";
        Assert.IsTrue(SubagentResponseParser.IsStandaloneToolMention(valid2, valid2.IndexOf(tool), tool.Length));

        string valid3 = $"Next: {tool}.";
        Assert.IsTrue(SubagentResponseParser.IsStandaloneToolMention(valid3, valid3.IndexOf(tool), tool.Length));

        // Embedded in file paths / identifiers (should be false)
        string invalidPath = $"/app/workdir/{tool}_step1.md";
        Assert.IsFalse(SubagentResponseParser.IsStandaloneToolMention(invalidPath, invalidPath.IndexOf(tool), tool.Length));

        string invalidExt = $"{tool}.py";
        Assert.IsFalse(SubagentResponseParser.IsStandaloneToolMention(invalidExt, invalidExt.IndexOf(tool), tool.Length));

        string invalidWinPath = $@"C:\{tool}\file.txt";
        Assert.IsFalse(SubagentResponseParser.IsStandaloneToolMention(invalidWinPath, invalidWinPath.IndexOf(tool), tool.Length));
    }

    [TestMethod]
    public void ExtractAllActionPairs_WithMultiplePythonScriptDrafts_CollapsesToLastAction()
    {
        // Arrange - Model outputs multiple drafts with self-correction in thought process (Step 15 scenario)
        string responseText = """
            Thought: I need to process sensor data.
            Action: PythonSandbox-ExecutePythonScript
            ActionInput: {"script": "print(F)"}
            Wait, I made a mistake. Let me rewrite the script.
            Action: PythonSandbox-ExecutePythonScript
            ActionInput: {"script": "print(sensors_id)"}
            Actually, I will just write the correct script now.
            Action: PythonSandbox-ExecutePythonScript
            ActionInput: {"script": "import pandas as pd\nprint('Final correct script')"}
            """;

        // Act
        var pairs = SubagentResponseParser.ExtractAllActionPairs(responseText);

        // Assert
        Assert.AreEqual(1, pairs.Count, "Multiple Python script drafts must collapse to the single final action.");
        Assert.AreEqual("PythonSandbox-ExecutePythonScript", pairs[0].ActionName);
        StringAssert.Contains(pairs[0].ActionInput, "Final correct script");
    }
}
