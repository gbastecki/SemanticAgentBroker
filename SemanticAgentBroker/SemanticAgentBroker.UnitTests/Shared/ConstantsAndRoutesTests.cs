using SemanticAgentBroker.Core.Constants;
using SemanticAgentBroker.Shared.Constants;
using SemanticAgentBroker.Shared.Domain;
using System.Reflection;

namespace SemanticAgentBroker.UnitTests.Shared;

[TestClass]
public class ConstantsAndRoutesTests
{
    [TestMethod]
    public void ApiRoutes_AllRoutes_StartWithSlashApiPrefix()
    {
        var fields = typeof(ApiRoutes).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .ToList();

        Assert.IsTrue(fields.Count > 10, "ApiRoutes must expose routing constants.");

        foreach (var field in fields)
        {
            var value = (string)field.GetValue(null)!;
            Assert.IsTrue(value.StartsWith("/api/"), $"ApiRoute {field.Name} value '{value}' must start with '/api/'");
        }
    }

    [TestMethod]
    public void HubMethodNames_And_Consts_ContainExpectedStandardValues()
    {
        // HubMethodNames: Verify all string constants are non-empty and PascalCase
        var hubFields = typeof(HubMethodNames).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .ToList();

        Assert.IsTrue(hubFields.Count >= 15);
        foreach (var field in hubFields)
        {
            var value = (string)field.GetValue(null)!;
            Assert.IsFalse(string.IsNullOrWhiteSpace(value), $"HubMethodName {field.Name} should not be empty.");
            Assert.AreEqual(field.Name, value, $"HubMethodName field name {field.Name} should match its value.");
        }

        // Core Consts: Verify values via reflection
        var constsFields = typeof(Consts).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly)
            .ToDictionary(f => f.Name, f => f.GetValue(null));

        Assert.AreEqual("TaskWorkspaces", constsFields["DefaultWorkspacesPath"]);
        Assert.AreEqual("/app/workdir", constsFields["DefaultContainerDataPath"]);
        Assert.AreEqual("ai-sandbox-python:latest", constsFields["DefaultPythonImage"]);
        Assert.AreEqual("PythonSandbox", constsFields["PluginPythonSandbox"]);
        Assert.AreEqual("FileIO", constsFields["PluginFileIO"]);
        Assert.AreEqual("WorkspaceInfo", constsFields["PluginWorkspaceInfo"]);
        Assert.AreEqual(16384, constsFields["DefaultMaxContextTokens"]);
        Assert.AreEqual(20, constsFields["PerMessageTokenOverhead"]);
        Assert.AreEqual(3, constsFields["MaxFormatRetries"]);

        // EventTypes: Verify all event types via reflection
        var eventFields = typeof(EventTypes).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .ToList();

        Assert.IsTrue(eventFields.Count >= 15);
        foreach (var field in eventFields)
        {
            var value = (string)field.GetValue(null)!;
            Assert.IsFalse(string.IsNullOrWhiteSpace(value), $"EventType {field.Name} should not be empty.");
            Assert.AreEqual(field.Name, value, $"EventType field name {field.Name} should match its value.");
        }
    }
}
