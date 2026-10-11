using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.Agents.Models;
using SemanticAgentBroker.Core.Features.Agents;
using SemanticAgentBroker.Shared.Constants;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SemanticAgentBroker.UnitTests.Api.Agents;

[TestClass]
public class AgentEndpointsTests
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
        _adminClient.Dispose();
        _userClient.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task GetAgents_ReturnsAllRegisteredAgents()
    {
        // Act
        var response = await _adminClient.GetAsync(ApiRoutes.AgentsBase);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var agents = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(JsonValueKind.Array, agents.ValueKind);
    }

    [TestMethod]
    public async Task GetAgentByName_ExistingAgent_ReturnsAgentWithBindings()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var agent = new AgentDefinition
            {
                Name = "SpecialistAgent",
                Description = "A specialist agent",
                Icon = "psychology",
                SystemInstruction = "Perform specialized tasks",
                IsEnabled = true,
                MethodBindings = [new AgentMethodBinding { PluginId = "FileIO", MethodId = "SaveToWorkspace" }]
            };
            db.AgentDefinitions.Add(agent);
            await db.SaveChangesAsync();

            var registry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
            registry.Register(agent);
        }

        // Act
        var url = ApiRoutes.AgentByName.Replace("{name}", "SpecialistAgent");
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("SpecialistAgent", doc.GetProperty("name").GetString());
        Assert.AreEqual("A specialist agent", doc.GetProperty("description").GetString());
    }

    [TestMethod]
    public async Task GetAgentByName_NonExistentAgent_ReturnsNotFound()
    {
        // Act
        var url = ApiRoutes.AgentByName.Replace("{name}", "NonExistentAgent123");
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateAgent_AsAdmin_CreatesAgentAndRegisters()
    {
        // Arrange
        var request = new CreateAgentRequest
        {
            Name = "NewCreatedAgent",
            Description = "Newly created test agent",
            Icon = "smart_toy",
            AccentClass = "blue",
            SystemInstruction = "You are a test agent.",
            Methods = [new MethodBindingDto { PluginId = "FileIO", MethodId = "ReadFromWorkspace" }]
        };

        // Act
        var response = await _adminClient.PostAsJsonAsync(ApiRoutes.AgentsBase, request);

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var agent = await db.AgentDefinitions.FirstOrDefaultAsync(a => a.Name == "NewCreatedAgent");
        Assert.IsNotNull(agent);
        Assert.AreEqual("Newly created test agent", agent.Description);

        var registry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
        Assert.IsTrue(registry.Contains("NewCreatedAgent"));
    }

    [TestMethod]
    public async Task CreateAgent_AsNonAdmin_ReturnsForbidden()
    {
        // Arrange
        var request = new CreateAgentRequest
        {
            Name = "ForbiddenAgent",
            Description = "Should not be created"
        };

        // Act
        var response = await _userClient.PostAsJsonAsync(ApiRoutes.AgentsBase, request);

        // Assert
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateAgent_WithDuplicateName_ReturnsBadRequest()
    {
        // Arrange - create first agent
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AgentDefinitions.Add(new AgentDefinition { Name = "ExistingAgent", Description = "Existing" });
            await db.SaveChangesAsync();
        }

        var request = new CreateAgentRequest
        {
            Name = "ExistingAgent",
            Description = "Duplicate"
        };

        // Act
        var response = await _adminClient.PostAsJsonAsync(ApiRoutes.AgentsBase, request);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateAgent_WithDuplicateNameDifferentCasing_ReturnsBadRequest()
    {
        // Arrange - create first agent with "ExistingAgentName"
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AgentDefinitions.Add(new AgentDefinition { Name = "ExistingAgentName", Description = "Existing" });
            await db.SaveChangesAsync();
        }

        var request = new CreateAgentRequest
        {
            Name = "EXISTINGAGENTNAME",
            Description = "Duplicate with different casing"
        };

        // Act
        var response = await _adminClient.PostAsJsonAsync(ApiRoutes.AgentsBase, request);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateAgent_WithEmptyName_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateAgentRequest
        {
            Name = "   ",
            Description = "No name"
        };

        // Act
        var response = await _adminClient.PostAsJsonAsync(ApiRoutes.AgentsBase, request);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateAgent_AsAdmin_UpdatesPropertiesAndReplacesBindings()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var agent = new AgentDefinition
            {
                Name = "AgentToUpdate",
                Description = "Initial description",
                Icon = "initial_icon",
                SystemInstruction = "Initial prompt",
                MethodBindings = [new AgentMethodBinding { PluginId = "FileIO", MethodId = "ReadFromWorkspace" }]
            };
            db.AgentDefinitions.Add(agent);
            await db.SaveChangesAsync();

            var registry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
            registry.Register(agent);
        }

        var updateReq = new UpdateAgentRequest
        {
            Description = "Updated description",
            Icon = "updated_icon",
            SystemInstruction = "Updated prompt",
            IsEnabled = false,
            Methods = [new MethodBindingDto { PluginId = "PythonSandbox", MethodId = "ExecutePythonScript" }]
        };

        // Act
        var url = ApiRoutes.AgentByName.Replace("{name}", "AgentToUpdate");
        var response = await _adminClient.PutAsJsonAsync(url, updateReq);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var scopeVerify = _factory.Services.CreateScope();
        var verifyDb = scopeVerify.ServiceProvider.GetRequiredService<AppDbContext>();
        var updated = await verifyDb.AgentDefinitions.Include(a => a.MethodBindings).FirstOrDefaultAsync(a => a.Name == "AgentToUpdate");
        Assert.IsNotNull(updated);
        Assert.AreEqual("Updated description", updated.Description);
        Assert.AreEqual("updated_icon", updated.Icon);
        Assert.AreEqual("Updated prompt", updated.SystemInstruction);
        Assert.IsFalse(updated.IsEnabled);
        Assert.AreEqual(1, updated.MethodBindings.Count);
        Assert.AreEqual("PythonSandbox", updated.MethodBindings[0].PluginId);
    }

    [TestMethod]
    public async Task UpdateAgent_WithoutMethods_PreservesExistingMethodBindingsInRegistry()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var agent = new AgentDefinition
            {
                Name = "AgentPreserveBindings",
                Description = "Initial description",
                Icon = "initial_icon",
                SystemInstruction = "Initial prompt",
                MethodBindings = [
                    new AgentMethodBinding { PluginId = "FileIO", MethodId = "ReadFromWorkspace" },
                    new AgentMethodBinding { PluginId = "PythonSandbox", MethodId = "RunCode" }
                ]
            };
            db.AgentDefinitions.Add(agent);
            await db.SaveChangesAsync();

            var registry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
            registry.Register(agent);
        }

        var updateReq = new UpdateAgentRequest
        {
            Description = "Only description changed",
            Methods = null // explicitly null: methods should NOT be cleared or stripped from registry!
        };

        // Act
        var url = ApiRoutes.AgentByName.Replace("{name}", "AgentPreserveBindings");
        var response = await _adminClient.PutAsJsonAsync(url, updateReq);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var scopeVerify = _factory.Services.CreateScope();
        var registryVerify = scopeVerify.ServiceProvider.GetRequiredService<IAgentRegistry>();
        var registeredAgent = registryVerify.Resolve("AgentPreserveBindings");
        Assert.IsNotNull(registeredAgent);
        Assert.AreEqual(2, registeredAgent.MethodBindings.Count, "Registered agent in IAgentRegistry must preserve existing method bindings when req.Methods is null");
    }

    [TestMethod]
    public async Task DeleteAgent_AsAdmin_RemovesFromDatabaseAndRegistry()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var agent = new AgentDefinition
            {
                Name = "AgentToDelete",
                Description = "Will be deleted",
                MethodBindings = [new AgentMethodBinding { PluginId = "FileIO", MethodId = "SaveToWorkspace" }]
            };
            db.AgentDefinitions.Add(agent);
            await db.SaveChangesAsync();

            var registry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
            registry.Register(agent);
        }

        // Act
        var url = ApiRoutes.AgentByName.Replace("{name}", "AgentToDelete");
        var response = await _adminClient.DeleteAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var scopeVerify = _factory.Services.CreateScope();
        var verifyDb = scopeVerify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.IsNull(await verifyDb.AgentDefinitions.FirstOrDefaultAsync(a => a.Name == "AgentToDelete"));

        var registryVerify = scopeVerify.ServiceProvider.GetRequiredService<IAgentRegistry>();
        Assert.IsFalse(registryVerify.Contains("AgentToDelete"));
    }

    [TestMethod]
    public async Task DuplicateAgent_CreatesCopyWithUniqueSuffix()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var agent = new AgentDefinition
            {
                Name = "SourceAgent",
                Description = "Original source agent",
                SystemInstruction = "Do source tasks",
                MethodBindings = [new AgentMethodBinding { PluginId = "FileIO", MethodId = "SaveToWorkspace" }]
            };
            db.AgentDefinitions.Add(agent);
            await db.SaveChangesAsync();

            var registry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
            registry.Register(agent);
        }

        // Act 1: Duplicate once -> SourceAgent_copy
        var url = ApiRoutes.AgentDuplicate.Replace("{name}", "SourceAgent");
        var response1 = await _adminClient.PostAsync(url, null);

        Assert.AreEqual(HttpStatusCode.Created, response1.StatusCode);
        var doc1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("SourceAgent_copy", doc1.GetProperty("name").GetString());

        // Act 2: Duplicate second time -> SourceAgent_copy_1
        var response2 = await _adminClient.PostAsync(url, null);

        Assert.AreEqual(HttpStatusCode.Created, response2.StatusCode);
        var doc2 = await response2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("SourceAgent_copy_1", doc2.GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task PreviewPrompt_GeneratesSystemInstructionWithAvailableTools()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var agent = new AgentDefinition
            {
                Name = "PreviewAgent",
                SystemInstruction = "Preview system instruction",
                MethodBindings = [new AgentMethodBinding { PluginId = "FileIO", MethodId = "SaveToWorkspace" }]
            };
            var registry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
            registry.Register(agent);
        }

        // Act
        var url = ApiRoutes.AgentPreviewPrompt.Replace("{name}", "PreviewAgent");
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("Preview system instruction", doc.GetProperty("systemInstruction").GetString());
        Assert.IsTrue(doc.TryGetProperty("availableTools", out _));
    }

    [TestMethod]
    public async Task UpdateAgent_NonExistentName_ReturnsNotFound()
    {
        // Arrange
        var updateReq = new UpdateAgentRequest { Description = "Non-existent update" };

        // Act
        var url = ApiRoutes.AgentByName.Replace("{name}", "NonExistentAgent_XYZ");
        var response = await _adminClient.PutAsJsonAsync(url, updateReq);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task DeleteAgent_NonExistentName_ReturnsNotFound()
    {
        // Act
        var url = ApiRoutes.AgentByName.Replace("{name}", "NonExistentAgent_XYZ");
        var response = await _adminClient.DeleteAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task DuplicateAgent_NonExistentName_ReturnsNotFound()
    {
        // Act
        var url = ApiRoutes.AgentDuplicate.Replace("{name}", "NonExistentAgent_XYZ");
        var response = await _adminClient.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task PreviewPrompt_NonExistentName_ReturnsNotFound()
    {
        // Act
        var url = ApiRoutes.AgentPreviewPrompt.Replace("{name}", "NonExistentAgent_XYZ");
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task AgentEndpoints_AsNonAdmin_ReturnForbiddenForAdminOnlyOperations()
    {
        // Act & Assert 1: Update agent as non-admin
        var updateUrl = ApiRoutes.AgentByName.Replace("{name}", "SomeAgent");
        var updateResp = await _userClient.PutAsJsonAsync(updateUrl, new UpdateAgentRequest { Description = "Hacked" });
        Assert.AreEqual(HttpStatusCode.Forbidden, updateResp.StatusCode);

        // Act & Assert 2: Delete agent as non-admin
        var deleteUrl = ApiRoutes.AgentByName.Replace("{name}", "SomeAgent");
        var deleteResp = await _userClient.DeleteAsync(deleteUrl);
        Assert.AreEqual(HttpStatusCode.Forbidden, deleteResp.StatusCode);

        // Act & Assert 3: Duplicate agent as non-admin
        var dupUrl = ApiRoutes.AgentDuplicate.Replace("{name}", "SomeAgent");
        var dupResp = await _userClient.PostAsync(dupUrl, null);
        Assert.AreEqual(HttpStatusCode.Forbidden, dupResp.StatusCode);
    }

    [TestMethod]
    public async Task PreviewPrompt_AsNonAdminUser_IsAllowedAndReturnsToolsAndInstruction()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var agent = new AgentDefinition
            {
                Name = "PublicPreviewAgent",
                SystemInstruction = "Non-admin accessible prompt",
                MethodBindings = [new AgentMethodBinding { PluginId = "FileIO", MethodId = "ReadFromWorkspace" }]
            };
            var registry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
            registry.Register(agent);
        }

        // Act - Call as non-admin user
        var url = ApiRoutes.AgentPreviewPrompt.Replace("{name}", "PublicPreviewAgent");
        var response = await _userClient.GetAsync(url);

        // Assert - Should be 200 OK (public preview endpoint)
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("Non-admin accessible prompt", doc.GetProperty("systemInstruction").GetString());
        Assert.IsTrue(doc.GetProperty("methodCount").GetInt32() >= 0);
    }

    [TestMethod]
    public async Task GetAgents_ReturnsDistinctPluginIdsAndMethodCount()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var agent = new AgentDefinition
            {
                Name = "MultiPluginAgent",
                Description = "Agent with multiple methods from same plugin",
                MethodBindings =
                [
                    new AgentMethodBinding { PluginId = "FileIO", MethodId = "ReadFromWorkspace" },
                    new AgentMethodBinding { PluginId = "FileIO", MethodId = "SaveToWorkspace" }
                ]
            };
            var registry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
            registry.Register(agent);
        }

        // Act
        var response = await _userClient.GetAsync(ApiRoutes.AgentsBase);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var agents = await response.Content.ReadFromJsonAsync<JsonElement>();
        var targetAgent = agents.EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == "MultiPluginAgent");
        Assert.AreEqual(JsonValueKind.Object, targetAgent.ValueKind);
        Assert.AreEqual(2, targetAgent.GetProperty("methodCount").GetInt32());
        var plugins = targetAgent.GetProperty("plugins").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.AreEqual(1, plugins.Count);
        Assert.AreEqual("FileIO", plugins[0]);
    }
}
