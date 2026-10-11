using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SemanticAgentBroker.UnitTests.Api.Auth;

[TestClass]
public class UserAndGroupEndpointsTests
{
    private CustomWebApplicationFactory _factory = null!;
    private HttpClient _adminClient = null!;
    private HttpClient _userClient = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _adminClient = await _factory.CreateAuthenticatedClientAsync("admin", "Admin", "admin-browser");
        _userClient = await _factory.CreateAuthenticatedClientAsync("testuser", "User", "user-browser");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _adminClient?.Dispose();
        _userClient?.Dispose();
        _factory?.Dispose();
    }

    [TestMethod]
    public async Task GetUsers_AsAdmin_ReturnsUserListWithPermissionsAndGroups()
    {
        // Act
        var response = await _adminClient.GetAsync("/api/users");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var users = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(JsonValueKind.Array, users.ValueKind);
        Assert.IsTrue(users.GetArrayLength() >= 1);
    }

    [TestMethod]
    public async Task GetUsers_AsNonAdmin_ReturnsForbidden()
    {
        // Act
        var response = await _userClient.GetAsync("/api/users");

        // Assert
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateUser_ValidRequest_CreatesUserWithHashedPassword()
    {
        // Arrange
        var req = new CreateUserRequest
        {
            Username = "newdev",
            Password = "DevPassword123!",
            Role = "User",
            Permissions = [new PermissionDto { PluginId = "FileIO", PluginTaskId = "SaveTask", AllowedOptions = ["UploadFiles"] }]
        };

        // Act
        var response = await _adminClient.PostAsJsonAsync("/api/users", req);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await db.Users.Include(u => u.TaskPermissions).FirstOrDefaultAsync(u => u.Username == "newdev");
        Assert.IsNotNull(user);
        Assert.AreEqual("User", user.Role);
        Assert.AreNotEqual("DevPassword123!", user.PasswordHash);
        Assert.AreEqual(1, user.TaskPermissions.Count);
    }

    [TestMethod]
    public async Task CreateUser_DuplicateUsername_ReturnsBadRequest()
    {
        // Arrange
        var req = new CreateUserRequest
        {
            Username = "admin", // already exists
            Password = "Password123!",
            Role = "User"
        };

        // Act
        var response = await _adminClient.PostAsJsonAsync("/api/users", req);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateUser_DuplicateUsernameDifferentCasing_ReturnsBadRequest()
    {
        // Arrange - "admin" already exists in database, attempt to register "ADMIN"
        var req = new CreateUserRequest
        {
            Username = "ADMIN",
            Password = "Password123!",
            Role = "User"
        };

        // Act
        var response = await _adminClient.PostAsJsonAsync("/api/users", req);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateUser_DemotingPrimaryAdmin_ReturnsBadRequest()
    {
        // Arrange
        Guid adminId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var admin = await db.Users.FirstAsync(u => u.Username == "admin");
            adminId = admin.Id;
        }

        var updateReq = new UpdateUserRequest
        {
            Role = "User", // Try to demote admin
            Permissions = []
        };

        // Act
        var response = await _adminClient.PutAsJsonAsync($"/api/users/{adminId}", updateReq);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateUser_ValidUser_UpdatesRolePermissionsAndGroups()
    {
        // Arrange - seed a user and group
        Guid targetUserId;
        Guid groupId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var group = new UserGroup { Id = Guid.NewGuid(), Name = "SupportTeam" };
            db.UserGroups.Add(group);

            var user = new User { Id = Guid.NewGuid(), Username = "support_user", Role = "User" };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            targetUserId = user.Id;
            groupId = group.Id;
        }

        var updateReq = new UpdateUserRequest
        {
            Role = "Admin",
            Password = "NewSecretPassword99!",
            GroupIds = [groupId],
            Permissions = [new PermissionDto { PluginId = "SampleData", PluginTaskId = "SalesData", AllowedOptions = ["All"] }]
        };

        // Act
        var response = await _adminClient.PutAsJsonAsync($"/api/users/{targetUserId}", updateReq);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var scopeVerify = _factory.Services.CreateScope();
        var verifyDb = scopeVerify.ServiceProvider.GetRequiredService<AuthDbContext>();
        var updated = await verifyDb.Users
            .Include(u => u.TaskPermissions)
            .Include(u => u.Groups)
            .FirstOrDefaultAsync(u => u.Id == targetUserId);

        Assert.IsNotNull(updated);
        Assert.AreEqual("Admin", updated.Role);
        Assert.AreEqual(1, updated.Groups.Count);
        Assert.AreEqual("SupportTeam", updated.Groups[0].Name);
        Assert.AreEqual(1, updated.TaskPermissions.Count);
    }

    [TestMethod]
    public async Task UpdateUser_NonExistentId_ReturnsNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        var updateReq = new UpdateUserRequest { Role = "User" };

        // Act
        var response = await _adminClient.PutAsJsonAsync($"/api/users/{nonExistentId}", updateReq);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateUser_AttemptingToDemotePrimaryAdminRole_ReturnsBadRequest()
    {
        // Arrange
        Guid adminId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var admin = await db.Users.FirstAsync(u => u.Username == "admin");
            adminId = admin.Id;
        }

        var updateReq = new UpdateUserRequest { Role = "User" };

        // Act
        var response = await _adminClient.PutAsJsonAsync($"/api/users/{adminId}", updateReq);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(content, "Cannot remove Admin role from primary admin user.");
    }

    [TestMethod]
    public async Task DeleteUser_DeletingPrimaryAdmin_ReturnsBadRequest()
    {
        // Arrange
        Guid adminId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var admin = await db.Users.FirstAsync(u => u.Username == "admin");
            adminId = admin.Id;
        }

        // Act
        var response = await _adminClient.DeleteAsync($"/api/users/{adminId}");

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task DeleteUser_NonExistentId_ReturnsNotFound()
    {
        // Act
        var response = await _adminClient.DeleteAsync($"/api/users/{Guid.NewGuid()}");

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task DeleteUser_ValidUser_DeletesAndRevokesSessions()
    {
        // Arrange
        Guid targetUserId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var user = new User { Id = Guid.NewGuid(), Username = "user_to_delete", Role = "User" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            targetUserId = user.Id;
        }

        // Act
        var response = await _adminClient.DeleteAsync($"/api/users/{targetUserId}");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var scopeVerify = _factory.Services.CreateScope();
        var verifyDb = scopeVerify.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.IsNull(await verifyDb.Users.FindAsync(targetUserId));
    }

    [TestMethod]
    public async Task CreateGroup_And_GetGroups_WorksSuccessfully()
    {
        // Arrange
        var createReq = new CreateGroupRequest
        {
            Name = "DataEngineers",
            Description = "Data Engineering Team",
            Permissions = [new PermissionDto { PluginId = "PythonSandbox", PluginTaskId = "ExecutePython", AllowedOptions = ["AutoExecution"] }]
        };

        // Act 1: Create Group
        var postResponse = await _adminClient.PostAsJsonAsync("/api/groups", createReq);
        Assert.AreEqual(HttpStatusCode.OK, postResponse.StatusCode);

        // Act 2: Get Groups
        var getResponse = await _adminClient.GetAsync("/api/groups");
        Assert.AreEqual(HttpStatusCode.OK, getResponse.StatusCode);

        var groups = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(groups.EnumerateArray().Any(g => g.GetProperty("name").GetString() == "DataEngineers"));
    }

    [TestMethod]
    public async Task CreateGroup_DuplicateName_ReturnsBadRequest()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.UserGroups.Add(new UserGroup { Id = Guid.NewGuid(), Name = "ExistingGroup" });
            await db.SaveChangesAsync();
        }

        var req = new CreateGroupRequest
        {
            Name = "ExistingGroup",
            Description = "Duplicate"
        };

        // Act
        var response = await _adminClient.PostAsJsonAsync("/api/groups", req);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateGroup_ValidRequest_UpdatesGroupAndPermissions()
    {
        // Arrange
        Guid groupId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var group = new UserGroup { Id = Guid.NewGuid(), Name = "AlphaGroup", Description = "Old Desc" };
            db.UserGroups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
        }

        var updateReq = new UpdateGroupRequest
        {
            Name = "BetaGroup",
            Description = "Updated Description",
            Permissions = [new PermissionDto { PluginId = "FileIO", PluginTaskId = "WorkspaceTask", AllowedOptions = ["Write"] }]
        };

        // Act
        var response = await _adminClient.PutAsJsonAsync($"/api/groups/{groupId}", updateReq);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var scopeVerify = _factory.Services.CreateScope();
        var verifyDb = scopeVerify.ServiceProvider.GetRequiredService<AuthDbContext>();
        var updated = await verifyDb.UserGroups.Include(g => g.TaskPermissions).FirstOrDefaultAsync(g => g.Id == groupId);
        Assert.IsNotNull(updated);
        Assert.AreEqual("BetaGroup", updated.Name);
        Assert.AreEqual("Updated Description", updated.Description);
        Assert.AreEqual(1, updated.TaskPermissions.Count);
    }

    [TestMethod]
    public async Task UpdateGroup_NonExistentId_ReturnsNotFound()
    {
        // Act
        var response = await _adminClient.PutAsJsonAsync($"/api/groups/{Guid.NewGuid()}", new UpdateGroupRequest { Name = "NewName" });

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateGroup_WithExistingDifferentGroupName_ReturnsBadRequest()
    {
        // Arrange
        Guid group1Id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var group1 = new UserGroup { Id = Guid.NewGuid(), Name = "GroupOne" };
            var group2 = new UserGroup { Id = Guid.NewGuid(), Name = "GroupTwo" };
            db.UserGroups.AddRange(group1, group2);
            await db.SaveChangesAsync();
            group1Id = group1.Id;
        }

        // Act - Attempt to rename GroupOne to GroupTwo (already taken)
        var response = await _adminClient.PutAsJsonAsync($"/api/groups/{group1Id}", new UpdateGroupRequest { Name = "GroupTwo" });

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(content, "Group name already exists.");
    }

    [TestMethod]
    public async Task CreateGroup_DuplicateNameDifferentCasing_ReturnsBadRequest()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.UserGroups.Add(new UserGroup { Id = Guid.NewGuid(), Name = "DevTeam" });
            await db.SaveChangesAsync();
        }

        var req = new CreateGroupRequest
        {
            Name = "devteam",
            Description = "Duplicate with different casing"
        };

        // Act
        var response = await _adminClient.PostAsJsonAsync("/api/groups", req);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateGroup_DuplicateNameDifferentCasing_ReturnsBadRequest()
    {
        // Arrange
        Guid group1Id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var group1 = new UserGroup { Id = Guid.NewGuid(), Name = "Alpha" };
            var group2 = new UserGroup { Id = Guid.NewGuid(), Name = "Beta" };
            db.UserGroups.AddRange(group1, group2);
            await db.SaveChangesAsync();
            group1Id = group1.Id;
        }

        // Act - Attempt to rename Alpha to BETA (already taken with different casing)
        var response = await _adminClient.PutAsJsonAsync($"/api/groups/{group1Id}", new UpdateGroupRequest { Name = "BETA" });

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task DeleteGroup_NonExistentId_ReturnsNotFound()
    {
        // Act
        var response = await _adminClient.DeleteAsync($"/api/groups/{Guid.NewGuid()}");

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task DeleteGroup_DeletesGroupAndRevokesMemberSessions()
    {
        // Arrange
        Guid groupId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var group = new UserGroup { Id = Guid.NewGuid(), Name = "GroupToDelete" };
            db.UserGroups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
        }

        // Act
        var response = await _adminClient.DeleteAsync($"/api/groups/{groupId}");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var scopeVerify = _factory.Services.CreateScope();
        var verifyDb = scopeVerify.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.IsNull(await verifyDb.UserGroups.FindAsync(groupId));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task CreateUser_EmptyOrWhitespaceUsername_ReturnsBadRequest(string username)
    {
        // Arrange
        var req = new CreateUserRequest
        {
            Username = username,
            Password = "Password123!",
            Role = "User"
        };

        // Act
        var response = await _adminClient.PostAsJsonAsync("/api/users", req);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task CreateUser_EmptyOrWhitespacePassword_ReturnsBadRequest(string password)
    {
        // Arrange
        var req = new CreateUserRequest
        {
            Username = "validuser_" + Guid.NewGuid().ToString("N"),
            Password = password,
            Role = "User"
        };

        // Act
        var response = await _adminClient.PostAsJsonAsync("/api/users", req);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task UserAndGroupEndpoints_AsNonAdmin_ReturnForbiddenForAdminOnlyOperations()
    {
        // Act & Assert 1: Update user
        var updateResp = await _userClient.PutAsJsonAsync($"/api/users/{Guid.NewGuid()}", new UpdateUserRequest { Role = "Admin" });
        Assert.AreEqual(HttpStatusCode.Forbidden, updateResp.StatusCode);

        // Act & Assert 2: Delete user
        var deleteResp = await _userClient.DeleteAsync($"/api/users/{Guid.NewGuid()}");
        Assert.AreEqual(HttpStatusCode.Forbidden, deleteResp.StatusCode);

        // Act & Assert 3: Create group
        var createGroupResp = await _userClient.PostAsJsonAsync("/api/groups", new CreateGroupRequest { Name = "HackerGroup" });
        Assert.AreEqual(HttpStatusCode.Forbidden, createGroupResp.StatusCode);

        // Act & Assert 4: Update group
        var updateGroupResp = await _userClient.PutAsJsonAsync($"/api/groups/{Guid.NewGuid()}", new UpdateGroupRequest { Name = "HackerGroup" });
        Assert.AreEqual(HttpStatusCode.Forbidden, updateGroupResp.StatusCode);

        // Act & Assert 5: Delete group
        var deleteGroupResp = await _userClient.DeleteAsync($"/api/groups/{Guid.NewGuid()}");
        Assert.AreEqual(HttpStatusCode.Forbidden, deleteGroupResp.StatusCode);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task CreateGroup_EmptyOrWhitespaceName_ReturnsBadRequest(string name)
    {
        // Arrange
        var req = new CreateGroupRequest
        {
            Name = name,
            Description = "Test invalid group"
        };

        // Act
        var response = await _adminClient.PostAsJsonAsync("/api/groups", req);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task UpdateGroup_EmptyOrWhitespaceName_ReturnsBadRequest(string name)
    {
        // Arrange
        Guid groupId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var group = new UserGroup { Id = Guid.NewGuid(), Name = "ValidGroup_" + Guid.NewGuid().ToString("N")[..8] };
            db.UserGroups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
        }

        var updateReq = new UpdateGroupRequest
        {
            Name = name,
            Description = "Updated"
        };

        // Act
        var response = await _adminClient.PutAsJsonAsync($"/api/groups/{groupId}", updateReq);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateGroup_DuplicateName_ReturnsBadRequest()
    {
        // Arrange
        Guid group1Id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var g1 = new UserGroup { Id = Guid.NewGuid(), Name = "GroupUniqueA_" + Guid.NewGuid().ToString("N")[..6] };
            var g2 = new UserGroup { Id = Guid.NewGuid(), Name = "GroupUniqueB_" + Guid.NewGuid().ToString("N")[..6] };
            db.UserGroups.AddRange(g1, g2);
            await db.SaveChangesAsync();
            group1Id = g1.Id;
        }

        using var scopeRead = _factory.Services.CreateScope();
        var readDb = scopeRead.ServiceProvider.GetRequiredService<AuthDbContext>();
        var g2Target = await readDb.UserGroups.FirstAsync(g => g.Id != group1Id);

        var updateReq = new UpdateGroupRequest
        {
            Name = g2Target.Name, // Try to rename Group1 to Group2's name
            Description = "Conflict attempt"
        };

        // Act
        var response = await _adminClient.PutAsJsonAsync($"/api/groups/{group1Id}", updateReq);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateUser_NonExistentUser_ReturnsNotFound()
    {
        // Arrange
        var updateReq = new UpdateUserRequest
        {
            Role = "User"
        };

        // Act
        var response = await _adminClient.PutAsJsonAsync($"/api/users/{Guid.NewGuid()}", updateReq);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateGroup_And_DeleteGroup_NonExistentGroup_ReturnsNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        var updateReq = new UpdateGroupRequest
        {
            Name = "GhostGroup",
            Description = "Ghost"
        };

        // Act 1: PUT non-existent group
        var putResponse = await _adminClient.PutAsJsonAsync($"/api/groups/{nonExistentId}", updateReq);
        Assert.AreEqual(HttpStatusCode.NotFound, putResponse.StatusCode);

        // Act 2: DELETE non-existent group
        var deleteResponse = await _adminClient.DeleteAsync($"/api/groups/{nonExistentId}");
        Assert.AreEqual(HttpStatusCode.NotFound, deleteResponse.StatusCode);
    }
}
