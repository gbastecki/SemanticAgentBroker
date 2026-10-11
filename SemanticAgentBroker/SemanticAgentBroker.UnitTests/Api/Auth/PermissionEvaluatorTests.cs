using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.Auth;

[TestClass]
public class PermissionEvaluatorTests
{
    private TestDatabaseFixture _dbFixture = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbFixture.Dispose();
    }

    [TestMethod]
    public async Task GetEffectivePermissionsAsync_UnionsDirectAndGroupPermissions()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        using (var db = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = userId, Username = "permuser" };
            var group = new UserGroup { Id = groupId, Name = "Analysts", Users = [user] };

            // Direct user permission
            db.UserTaskPermissions.Add(new UserTaskPermission
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PluginId = "FileIO",
                PluginTaskId = "SaveTask",
                AllowedOptions = ["UploadFiles"]
            });

            // Group permission
            db.UserGroupTaskPermissions.Add(new UserGroupTaskPermission
            {
                Id = Guid.NewGuid(),
                UserGroupId = groupId,
                PluginId = "FileIO",
                PluginTaskId = "SaveTask",
                AllowedOptions = ["AutoExecution", "UploadFiles"]
            });

            db.Users.Add(user);
            db.UserGroups.Add(group);
            await db.SaveChangesAsync();
        }

        // Act
        using (var db = _dbFixture.CreateAuthDbContext())
        {
            var effective = await db.GetEffectivePermissionsAsync(userId);

            // Assert
            Assert.AreEqual(1, effective.Count);
            var perm = effective[0];
            Assert.AreEqual("FileIO", perm.PluginId);
            Assert.AreEqual("SaveTask", perm.PluginTaskId);
            Assert.AreEqual(2, perm.AllowedOptions.Count);
            CollectionAssert.Contains(perm.AllowedOptions, "UploadFiles");
            CollectionAssert.Contains(perm.AllowedOptions, "AutoExecution");
        }
    }

    [TestMethod]
    public void HasPermission_And_GetAllowedOptions_CombinesWildcardAndSpecificPermissions()
    {
        // Arrange
        var permissions = new List<EvaluatedPermission>
        {
            new() { PluginId = "SampleData", PluginTaskId = "*", AllowedOptions = ["AutoExecution"] },
            new() { PluginId = "SampleData", PluginTaskId = "SalesAnalysis", AllowedOptions = ["UploadFiles", "EditFiles"] },
            new() { PluginId = "OtherPlugin", PluginTaskId = "Task1", AllowedOptions = ["ConsoleView"] }
        };

        // Act & Assert HasPermission
        Assert.IsTrue(permissions.HasPermission("SampleData", "SalesAnalysis"));
        Assert.IsTrue(permissions.HasPermission("SampleData", "SalesAnalysis", "AutoExecution"));
        Assert.IsTrue(permissions.HasPermission("SampleData", "SalesAnalysis", "UploadFiles"));
        Assert.IsTrue(permissions.HasPermission("SampleData", "SalesAnalysis", "EditFiles"));
        Assert.IsFalse(permissions.HasPermission("SampleData", "SalesAnalysis", "Delete"));

        // Non-existent task under SampleData still gets wildcard permission
        Assert.IsTrue(permissions.HasPermission("SampleData", "UnknownTask"));
        Assert.IsTrue(permissions.HasPermission("SampleData", "UnknownTask", "AutoExecution"));
        Assert.IsFalse(permissions.HasPermission("SampleData", "UnknownTask", "UploadFiles"));

        // OtherPlugin
        Assert.IsTrue(permissions.HasPermission("OtherPlugin", "Task1", "ConsoleView"));
        Assert.IsFalse(permissions.HasPermission("OtherPlugin", "Task2"));

        // Act & Assert GetAllowedOptions
        var options = permissions.GetAllowedOptions("SampleData", "SalesAnalysis");
        Assert.AreEqual(3, options.Count);
        CollectionAssert.Contains(options, "AutoExecution");
        CollectionAssert.Contains(options, "UploadFiles");
        CollectionAssert.Contains(options, "EditFiles");
    }

    [TestMethod]
    public async Task GetEffectivePermissionsAsync_WhenUserHasNoPermissions_ReturnsEmptyList()
    {
        // Arrange
        var userId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = userId, Username = "noperm_user" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        // Act
        using (var db = _dbFixture.CreateAuthDbContext())
        {
            var effective = await db.GetEffectivePermissionsAsync(userId);

            // Assert
            Assert.AreEqual(0, effective.Count);
        }
    }

    [TestMethod]
    public void HasPermission_And_GetAllowedOptions_WithEmptyPermissions_ReturnsFalseAndEmpty()
    {
        // Arrange
        var emptyPermissions = new List<EvaluatedPermission>();

        // Act & Assert
        Assert.IsFalse(emptyPermissions.HasPermission("AnyPlugin", "AnyTask"));
        Assert.IsFalse(emptyPermissions.HasPermission("AnyPlugin", "AnyTask", "AnyOption"));

        var options = emptyPermissions.GetAllowedOptions("AnyPlugin", "AnyTask");
        Assert.AreEqual(0, options.Count);
    }
}
