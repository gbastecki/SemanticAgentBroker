using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Moq;
using SemanticAgentBroker.Core.Builders;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.UnitTests.Core.Builders;

[TestClass]
public class AgentBuilderTests
{
    private IServiceProvider _services = null!;
    private Mock<IPluginRegistry> _pluginRegistryMock = null!;
    private Mock<IPluginContextFactory> _contextFactoryMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _services = new ServiceCollection().BuildServiceProvider();
        _pluginRegistryMock = new Mock<IPluginRegistry>();
        _contextFactoryMock = new Mock<IPluginContextFactory>();
    }

    [TestMethod]
    public void BuildSystemPrompt_IncludesSystemInstructionAndAvailableTools()
    {
        // Arrange
        var builder = new AgentBuilder(_services, "TestAgent")
            .WithSystemInstruction("You are a specialized math solver.")
            .WithTaskObjective("Solve the quadratic equation.");

        var kernel = new Kernel();

        // Act
        string systemPrompt = builder.BuildSystemPrompt(kernel, maxTokens: 16000);

        // Assert
        StringAssert.Contains(systemPrompt, "You are a specialized math solver.");
        StringAssert.Contains(systemPrompt, "## Task Objective");
        StringAssert.Contains(systemPrompt, "Solve the quadratic equation.");
        StringAssert.Contains(systemPrompt, "## Response Format");
        StringAssert.Contains(systemPrompt, "Context budget: ~16000 tokens.");
    }

    [TestMethod]
    public void BuildSystemPrompt_WithDataTools_IncludesDataRulesAndFileDiscipline()
    {
        // Arrange
        var builder = new AgentBuilder(_services, "DataAgent")
            .WithSystemInstruction("You process files.")
            .WithTaskObjective("Analyze dataset");

        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("FileIO", [KernelFunctionFactory.CreateFromMethod(() => "read", "ReadFromWorkspace")]);

        // Act
        string systemPrompt = builder.BuildSystemPrompt(kernel);

        // Assert
        StringAssert.Contains(systemPrompt, "## Data Rules");
        StringAssert.Contains(systemPrompt, "WORKSPACE_PATH: /app/workdir/");
        StringAssert.Contains(systemPrompt, "## File Discipline");
        StringAssert.Contains(systemPrompt, "## Python Scripting Best Practices");
    }

    [TestMethod]
    public void BuildSystemPrompt_IncludesPythonFStringQuoteGuidance()
    {
        // Arrange
        var builder = new AgentBuilder(_services, "CoderAgent")
            .WithSystemInstruction("You write code.");

        var kernel = new Kernel();

        // Act
        string systemPrompt = builder.BuildSystemPrompt(kernel);

        // Assert
        StringAssert.Contains(systemPrompt, "## Tool Argument Rules");
        StringAssert.Contains(systemPrompt, "f-strings");
        StringAssert.Contains(systemPrompt, "Python 3.12");
    }

    [TestMethod]
    public void BuildSystemPrompt_IncludesPythonBestPracticesForNoTryExceptAndSingleScript()
    {
        // Arrange
        var builder = new AgentBuilder(_services, "DataAgent")
            .WithSystemInstruction("You process files.")
            .WithTaskObjective("Analyze dataset");

        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("FileIO", [KernelFunctionFactory.CreateFromMethod(() => "read", "ReadFromWorkspace")]);

        // Act
        string systemPrompt = builder.BuildSystemPrompt(kernel);

        // Assert
        StringAssert.Contains(systemPrompt, "Target runtime: Python 3.12");
        StringAssert.Contains(systemPrompt, "No top-level try-except");
        StringAssert.Contains(systemPrompt, "Single script per turn");
        StringAssert.Contains(systemPrompt, "tabulate");
    }

    [TestMethod]
    public void BuildSystemPrompt_WithoutDataTools_ExcludesDataRules()
    {
        // Arrange
        var builder = new AgentBuilder(_services, "MathAgent")
            .WithSystemInstruction("You calculate sums.")
            .WithTaskObjective("Sum numbers");

        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("Math", [KernelFunctionFactory.CreateFromMethod((int a, int b) => a + b, "Add")]);

        // Act
        string systemPrompt = builder.BuildSystemPrompt(kernel);

        // Assert
        Assert.IsFalse(systemPrompt.Contains("## Data Rules"));
        Assert.IsFalse(systemPrompt.Contains("## File Discipline"));
    }

    [TestMethod]
    public void BuildSystemPrompt_WithComplexTaskObjectiveOver30Words_IncludesTaskContinuity()
    {
        // Arrange
        string longObjective = "Please perform a very detailed and thorough end to end analysis of the customer sentiment dataset across all regional clusters, computing quarterly trends and generating actionable recommendations for the executive leadership team to review in their next quarterly board meeting.";
        var builder = new AgentBuilder(_services, "StrategyAgent")
            .WithSystemInstruction("You are a strategist.")
            .WithTaskObjective(longObjective);

        var kernel = new Kernel();

        // Act
        string systemPrompt = builder.BuildSystemPrompt(kernel);

        // Assert
        StringAssert.Contains(systemPrompt, "## Task Continuity");
        StringAssert.Contains(systemPrompt, "## Token Efficiency");
    }

    [TestMethod]
    public void WithMethodBindings_And_RegisterPlugins_RegistersBoundMethods()
    {
        // Arrange
        var builder = new AgentBuilder(_services, "TestAgent")
            .WithSystemInstruction("Instruction")
            .WithMethodBindings(new[]
            {
                new AgentMethodBinding { PluginId = "TestPlugin", MethodId = "RunMethod" }
            });

        var methodMock = new Mock<IPluginMethod>();
        methodMock.Setup(m => m.Id).Returns("RunMethod");
        methodMock.Setup(m => m.Description).Returns("Method description");
        methodMock.Setup(m => m.Parameters).Returns(Array.Empty<PluginParameter>());

        _pluginRegistryMock.Setup(r => r.GetMethods(It.IsAny<IEnumerable<(string PluginId, string MethodId)>>()))
            .Returns([("TestPlugin", methodMock.Object)]);

        var kernel = new Kernel();

        // Act
        builder.RegisterPlugins(kernel, _pluginRegistryMock.Object, _contextFactoryMock.Object, "C:\\work", Guid.NewGuid());

        // Assert
        Assert.IsTrue(kernel.Plugins.Contains("TestPlugin"));
        Assert.IsTrue(kernel.Plugins["TestPlugin"].Contains("RunMethod"));
    }

    [TestMethod]
    public void WithRawMethodBindings_RegistersRawBindings()
    {
        // Arrange
        var builder = new AgentBuilder(_services, "TestAgent")
            .WithRawMethodBindings(new[] { ("CustomPlugin", "CustomMethod") });

        var methodMock = new Mock<IPluginMethod>();
        methodMock.Setup(m => m.Id).Returns("CustomMethod");
        methodMock.Setup(m => m.Description).Returns("Custom description");
        methodMock.Setup(m => m.Parameters).Returns(Array.Empty<PluginParameter>());

        _pluginRegistryMock.Setup(r => r.GetMethods(It.IsAny<IEnumerable<(string PluginId, string MethodId)>>()))
            .Returns([("CustomPlugin", methodMock.Object)]);

        var kernel = new Kernel();

        // Act
        builder.RegisterPlugins(kernel, _pluginRegistryMock.Object, _contextFactoryMock.Object, "C:\\work", Guid.NewGuid());

        // Assert
        Assert.IsTrue(kernel.Plugins.Contains("CustomPlugin"));
        Assert.IsTrue(kernel.Plugins["CustomPlugin"].Contains("CustomMethod"));
    }

    [TestMethod]
    public void WithSafetyOverrides_PassesOverridesProperly()
    {
        // Arrange
        var safetyOverrides = new Dictionary<string, bool?>
        {
            ["CustomPlugin:CustomMethod"] = false
        };

        var builder = new AgentBuilder(_services, "TestAgent")
            .WithSafetyOverrides(safetyOverrides);

        var methodMock = new Mock<IPluginMethod>();
        methodMock.Setup(m => m.Id).Returns("CustomMethod");
        methodMock.Setup(m => m.Description).Returns("Custom description");
        methodMock.Setup(m => m.Parameters).Returns(Array.Empty<PluginParameter>());

        _pluginRegistryMock.Setup(r => r.GetMethods(It.IsAny<IEnumerable<(string PluginId, string MethodId)>>()))
            .Returns([("CustomPlugin", methodMock.Object)]);

        var kernel = new Kernel();

        // Act
        builder.RegisterPlugins(kernel, _pluginRegistryMock.Object, _contextFactoryMock.Object, "C:\\work", Guid.NewGuid());

        // Assert
        Assert.IsTrue(kernel.Plugins.Contains("CustomPlugin"));
    }

    [TestMethod]
    public void RegisterPlugins_WhenNoMethodBindingsSpecified_ResolvesAllAvailableMethods()
    {
        // Arrange - AgentBuilder with NO method bindings
        var builder = new AgentBuilder(_services, "FullAgent");

        var methodMock = new Mock<IPluginMethod>();
        methodMock.Setup(m => m.Id).Returns("AllMethod");
        methodMock.Setup(m => m.Description).Returns("Desc");
        methodMock.Setup(m => m.Parameters).Returns(Array.Empty<PluginParameter>());

        _pluginRegistryMock.Setup(r => r.GetMethods(null))
            .Returns([("FileIO", methodMock.Object)]);

        var kernel = new Kernel();

        // Act
        builder.RegisterPlugins(kernel, _pluginRegistryMock.Object, _contextFactoryMock.Object, "C:\\work", Guid.NewGuid());

        // Assert - GetMethods was called with null (all methods requested)
        _pluginRegistryMock.Verify(r => r.GetMethods(null), Times.Once);
        Assert.IsTrue(kernel.Plugins.Contains("FileIO"));
    }

    [TestMethod]
    public void SystemInstruction_Getter_ReturnsConfiguredInstruction()
    {
        // Arrange
        var builder = new AgentBuilder(_services, "InspectAgent")
            .WithSystemInstruction("Custom agent instructions here.");

        // Act & Assert
        Assert.AreEqual("Custom agent instructions here.", builder.SystemInstruction);
    }

    [TestMethod]
    public void BuildSystemPrompt_WhenToolHasReturnDescription_IncludesReturnsMetadata()
    {
        // Arrange
        var builder = new AgentBuilder(_services, "SchemaAgent")
            .WithSystemInstruction("Instruction");

        var kernel = new Kernel();
        var returnParam = new KernelReturnParameterMetadata
        {
            Description = "JSON array of sensor objects with Id, Name, SensorType."
        };
        var fn = KernelFunctionFactory.CreateFromMethod(
            () => "[]",
            functionName: "GetSensors",
            description: "Gets all sensors",
            returnParameter: returnParam);
        kernel.Plugins.AddFromFunctions("SensorsPlugin", [fn]);

        // Act
        var systemPrompt = builder.BuildSystemPrompt(kernel);

        // Assert
        StringAssert.Contains(systemPrompt, "Returns: JSON array of sensor objects with Id, Name, SensorType.");
    }

    [TestMethod]
    public void BuildSystemPrompt_IncludesConsoleOutputDisciplineAndLaTeXGuidance()
    {
        // Arrange
        var builder = new AgentBuilder(_services, "PythonAgent")
            .WithSystemInstruction("Instruction");

        var kernel = new Kernel();
        kernel.Plugins.AddFromFunctions("PythonSandbox", [
            KernelFunctionFactory.CreateFromMethod(() => "res", "ExecutePythonScript")
        ]);

        // Act
        var systemPrompt = builder.BuildSystemPrompt(kernel);

        // Assert
        StringAssert.Contains(systemPrompt, "Do not flood console output: NEVER print full DataFrames");
        StringAssert.Contains(systemPrompt, "Do NOT use LaTeX math delimiters ($)");
    }
}
