using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.UnitTests.Sdk;

public class TestCustomPlugin : PluginBase
{
    public override string Id => "TestPlugin";
    public override string DisplayName => "Test Custom Plugin";
    public override string Description => "A sample plugin for testing SDK base classes.";
    public override string Version => "2.1.0";
    public override string Author => "TestAuthor";
    public override string Icon => "extension";
    public override IReadOnlyList<string> Tags => ["test", "sdk"];
    public override PluginCategory Category => PluginCategory.Custom;

    public override IReadOnlyList<IPluginMethod> GetMethods() =>
    [
        new TestablePluginMethod()
    ];

    public override IReadOnlyList<PluginConfigEntry> GetConfigurationSchema() =>
    [
        new() { Key = "ApiKey", DisplayName = "API Key", IsRequired = true, IsSensitive = true }
    ];

    public override IReadOnlyList<PluginTaskDef> GetTasks() =>
    [
        new()
        {
            Id = "SampleTask",
            Name = "Sample Predefined Task",
            Description = "A predefined test task",
            AllowedMethods = ["TestableMethod"]
        }
    ];
}

[TestClass]
public class SdkUnitTests
{
    [TestMethod]
    public void PluginBase_DefaultPropertiesAndMethods_ReturnsExpectedValues()
    {
        // Arrange
        var plugin = new TestCustomPlugin();

        // Assert
        Assert.AreEqual("TestPlugin", plugin.Id);
        Assert.AreEqual("Test Custom Plugin", plugin.DisplayName);
        Assert.AreEqual("2.1.0", plugin.Version);
        Assert.AreEqual("TestAuthor", plugin.Author);
        Assert.AreEqual(PluginCategory.Custom, plugin.Category);

        var methods = plugin.GetMethods();
        Assert.AreEqual(1, methods.Count);
        Assert.AreEqual("TestableMethod", methods[0].Id);

        var configs = plugin.GetConfigurationSchema();
        Assert.AreEqual(1, configs.Count);
        Assert.AreEqual("ApiKey", configs[0].Key);
        Assert.IsTrue(configs[0].IsSensitive);

        var tasks = plugin.GetTasks();
        Assert.AreEqual(1, tasks.Count);
        Assert.AreEqual("SampleTask", tasks[0].Id);
        CollectionAssert.AreEqual(new[] { "TestableMethod" }, (System.Collections.ICollection)tasks[0].AllowedMethods);
    }

    [TestMethod]
    public void PluginParameter_SetsPropertiesCorrectly()
    {
        // Arrange & Act
        var param = new PluginParameter
        {
            Name = "count",
            Description = "The count parameter",
            ParameterType = typeof(int),
            IsRequired = false,
            DefaultValue = 10
        };

        // Assert
        Assert.AreEqual("count", param.Name);
        Assert.AreEqual(typeof(int), param.ParameterType);
        Assert.IsFalse(param.IsRequired);
        Assert.AreEqual(10, param.DefaultValue);
    }

    [TestMethod]
    public void PluginTaskDef_SetsPropertiesCorrectly()
    {
        // Arrange & Act
        var taskDef = new PluginTaskDef
        {
            Id = "task_01",
            Name = "Task 01",
            Description = "Task description",
            AllowedMethods = ["MethodA", "MethodB"],
            ExternalMethods = [new ExternalMethodRef { PluginId = "ExtPlugin", MethodId = "ExtMethod" }],
            SafetyOverrides = [new MethodSafetyOverride { MethodId = "MethodA", IsDangerous = false }]
        };

        // Assert
        Assert.AreEqual("task_01", taskDef.Id);
        Assert.AreEqual(2, taskDef.AllowedMethods.Count);
        Assert.AreEqual(1, taskDef.ExternalMethods.Count);
        Assert.AreEqual("ExtPlugin", taskDef.ExternalMethods[0].PluginId);
        Assert.AreEqual(1, taskDef.SafetyOverrides.Count);
        Assert.IsFalse(taskDef.SafetyOverrides[0].IsDangerous);
    }

    [TestMethod]
    public void PluginBase_Context_WhenNotInitialized_ThrowsInvalidOperationException()
    {
        // Arrange
        var plugin = new MinimalPlugin();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => plugin.GetContextForTesting());
    }

    [TestMethod]
    public async Task PluginBase_InitializeAsync_WithNullContext_ThrowsArgumentNullException()
    {
        // Arrange
        var plugin = new MinimalPlugin();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await plugin.InitializeAsync(null!);
        });
    }

    [TestMethod]
    public void PluginBase_DefaultVirtuals_ProvideSensibleFallbacks()
    {
        // Arrange
        using var plugin = new MinimalPlugin();

        // Assert
        Assert.AreEqual("1.0.0", plugin.Version);
        Assert.AreEqual("Unknown", plugin.Author);
        Assert.AreEqual("extension", plugin.Icon);
        Assert.AreEqual(0, plugin.Tags.Count);
        Assert.AreEqual(0, plugin.RequiredPlugins.Count);
        Assert.AreEqual(PluginCategory.Custom, plugin.Category);
        Assert.AreEqual(0, plugin.GetTasks().Count);
        Assert.AreEqual(0, plugin.GetConfigurationSchema().Count);
    }
}

public class MinimalPlugin : PluginBase
{
    public override string Id => "Minimal";
    public override string DisplayName => "Minimal Plugin";
    public override string Description => "Minimal Plugin Description";

    public override IReadOnlyList<IPluginMethod> GetMethods() => [];

    public IPluginContext GetContextForTesting() => Context;
}
