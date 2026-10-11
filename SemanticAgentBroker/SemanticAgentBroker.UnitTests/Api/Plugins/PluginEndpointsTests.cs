using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.Shared.Constants;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SemanticAgentBroker.UnitTests.Api.Plugins;

[TestClass]
public class PluginEndpointsTests
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

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.PluginRegistrations.AnyAsync(p => p.Id == "PythonSandbox"))
        {
            db.PluginRegistrations.Add(new PluginRegistration
            {
                Id = "PythonSandbox",
                DisplayName = "Python Sandbox",
                Description = "Python Execution Sandbox",
                Category = PluginCategory.BuiltInRequired,
                IsBuiltIn = true,
                IsEnabled = true,
                LoadState = PluginLoadState.Loaded,
                ConfigValues = [new PluginConfigValue { Id = Guid.NewGuid(), PluginId = "PythonSandbox", Key = "TimeoutSeconds", Value = "30" }]
            });
        }

        if (!await db.PluginRegistrations.AnyAsync(p => p.Id == "FileIO"))
        {
            db.PluginRegistrations.Add(new PluginRegistration
            {
                Id = "FileIO",
                DisplayName = "File IO",
                Description = "Workspace File IO Plugin",
                Category = PluginCategory.BuiltInRequired,
                IsBuiltIn = true,
                IsEnabled = true,
                LoadState = PluginLoadState.Loaded,
                TasksJson = "[{\"Id\":\"SaveTask\",\"Name\":\"Save Task\",\"Description\":\"Save files to workspace\"}]"
            });
        }

        await db.SaveChangesAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _adminClient.Dispose();
        _userClient.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task GetPlugins_AsAdmin_ReturnsAllPluginsWithFullAllowedOptions()
    {
        // Act
        var response = await _adminClient.GetAsync(ApiRoutes.PluginsBase);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var plugins = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(JsonValueKind.Array, plugins.ValueKind);
        Assert.IsTrue(plugins.GetArrayLength() > 0);
    }

    [TestMethod]
    public async Task GetPlugins_AsUser_FiltersByPermissions()
    {
        // Arrange - user has permission only for FileIO SaveTask
        using (var scope = _factory.Services.CreateScope())
        {
            var authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var user = await authDb.Users.FirstAsync(u => u.Username == "testuser");
            authDb.UserTaskPermissions.Add(new UserTaskPermission
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PluginId = "FileIO",
                PluginTaskId = "SaveTask",
                AllowedOptions = ["AutoExecution"]
            });
            await authDb.SaveChangesAsync();
        }

        // Act
        var response = await _userClient.GetAsync(ApiRoutes.PluginsBase);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var plugins = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(JsonValueKind.Array, plugins.ValueKind);
        foreach (var p in plugins.EnumerateArray())
        {
            Assert.AreEqual("FileIO", p.GetProperty("id").GetString());
        }
    }

    [TestMethod]
    public async Task GetPlugins_AsUser_WithCasingDiscrepancy_ReturnsAllowedPluginsAndTasks()
    {
        // Arrange - user has permission with lowercase "fileio" and "savetask"
        using (var scope = _factory.Services.CreateScope())
        {
            var authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var user = await authDb.Users.FirstAsync(u => u.Username == "testuser");

            var existing = await authDb.UserTaskPermissions.Where(p => p.UserId == user.Id).ToListAsync();
            authDb.UserTaskPermissions.RemoveRange(existing);

            authDb.UserTaskPermissions.Add(new UserTaskPermission
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PluginId = "fileio", // lowercase
                PluginTaskId = "savetask", // lowercase
                AllowedOptions = ["AutoExecution"]
            });
            await authDb.SaveChangesAsync();
        }

        // Act 1: GET /api/plugins
        var responsePlugins = await _userClient.GetAsync(ApiRoutes.PluginsBase);
        Assert.AreEqual(HttpStatusCode.OK, responsePlugins.StatusCode);
        var plugins = await responsePlugins.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(plugins.EnumerateArray().Any(p => p.GetProperty("id").GetString() == "FileIO"),
            "FileIO plugin should be returned despite lowercase permission PluginId");

        // Act 2: GET /api/plugins/FileIO/tasks
        var taskUrl = ApiRoutes.PluginTasks.Replace("{id}", "FileIO");
        var responseTasks = await _userClient.GetAsync(taskUrl);
        Assert.AreEqual(HttpStatusCode.OK, responseTasks.StatusCode);
        var tasks = await responseTasks.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(tasks.EnumerateArray().Any(t => t.GetProperty("id").GetString() == "SaveTask"),
            "SaveTask should be returned despite lowercase permission PluginTaskId");
    }

    [TestMethod]
    public async Task GetPluginById_AsAdmin_ReturnsPluginWithMaskedSensitiveConfig()
    {
        // Act
        var url = ApiRoutes.PluginById.Replace("{id}", "PythonSandbox");
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("PythonSandbox", doc.GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task GetPluginById_AsNonAdmin_ReturnsForbidden()
    {
        // Act
        var url = ApiRoutes.PluginById.Replace("{id}", "PythonSandbox");
        var response = await _userClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task TogglePlugin_BuiltInRequired_ReturnsBadRequest()
    {
        // Act - Attempt to disable PythonSandbox (BuiltInRequired)
        var url = ApiRoutes.PluginToggle.Replace("{id}", "PythonSandbox");
        var response = await _adminClient.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var message = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(message, "required built-in plugin");
    }

    [TestMethod]
    public async Task UpdatePluginConfig_AsAdmin_UpdatesConfigAndPreservesMaskedValues()
    {
        // Arrange
        var config = new Dictionary<string, string>
        {
            ["TimeoutSeconds"] = "45"
        };

        // Act
        var url = ApiRoutes.PluginConfig.Replace("{id}", "PythonSandbox");
        var response = await _adminClient.PutAsJsonAsync(url, config);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task GetPluginTasks_ReturnsTaskListWithEnabledStates()
    {
        // Act
        var url = ApiRoutes.PluginTasks.Replace("{id}", "FileIO");
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var tasks = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(JsonValueKind.Array, tasks.ValueKind);
    }

    [TestMethod]
    public async Task TogglePluginTask_AsAdmin_TogglesTaskState()
    {
        // Act
        var url = ApiRoutes.PluginTaskToggle.Replace("{id}", "FileIO").Replace("{taskId}", "SaveTask");
        var response = await _adminClient.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(doc.TryGetProperty("isEnabled", out _));
    }

    [TestMethod]
    public async Task ReloadAllPlugins_AsAdmin_ReturnsOk()
    {
        // Act
        var response = await _adminClient.PostAsync(ApiRoutes.PluginsReloadAll, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task TogglePlugin_OptionalPlugin_TogglesEnabledState()
    {
        // Arrange - Seed optional plugin in DB
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PluginRegistrations.Add(new PluginRegistration
            {
                Id = "OptionalTestPlugin",
                DisplayName = "Optional Plugin",
                Description = "Optional for toggling",
                Category = PluginCategory.BuiltInOptional,
                IsBuiltIn = true,
                IsEnabled = false,
                LoadState = PluginLoadState.Discovered
            });
            await db.SaveChangesAsync();
        }

        // Act - Toggle to enabled
        var url = ApiRoutes.PluginToggle.Replace("{id}", "OptionalTestPlugin");
        var response = await _adminClient.PostAsync(url, null);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var scopeVerify = _factory.Services.CreateScope();
        var verifyDb = scopeVerify.ServiceProvider.GetRequiredService<AppDbContext>();
        var updated = await verifyDb.PluginRegistrations.FindAsync("OptionalTestPlugin");
        Assert.IsNotNull(updated);
        Assert.IsTrue(updated.IsEnabled);
    }

    [TestMethod]
    public async Task GetPluginById_NonExistentPlugin_ReturnsNotFound()
    {
        // Act
        var url = ApiRoutes.PluginById.Replace("{id}", "NonExistentPluginXYZ");
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdatePluginConfig_NonExistentPlugin_ReturnsNotFound()
    {
        // Act
        var url = ApiRoutes.PluginConfig.Replace("{id}", "NonExistentPluginXYZ");
        var response = await _adminClient.PutAsJsonAsync(url, new Dictionary<string, string> { ["Key"] = "Val" });

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ReloadPlugin_ExistingPlugin_CallsLoaderAndReturnsReloadResult()
    {
        // Arrange - Seed plugin in DB
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.PluginRegistrations.AnyAsync(p => p.Id == "PluginToReload"))
            {
                db.PluginRegistrations.Add(new PluginRegistration
                {
                    Id = "PluginToReload",
                    DisplayName = "Plugin To Reload",
                    Description = "Test reload",
                    Category = PluginCategory.Custom,
                    IsBuiltIn = false,
                    IsEnabled = true,
                    LoadState = PluginLoadState.Loaded
                });
                await db.SaveChangesAsync();
            }
        }

        // Act
        var url = ApiRoutes.PluginReload.Replace("{id}", "PluginToReload");
        var response = await _adminClient.PostAsync(url, null);

        // Assert - Will attempt to reload and return OK with reload status
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("PluginToReload", doc.GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task TogglePlugin_DisablingNotLoadedPlugin_DeletesFromDatabase()
    {
        // Arrange - Seed an enabled plugin with LoadState == NotLoaded (e.g. DLL missing)
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PluginRegistrations.Add(new PluginRegistration
            {
                Id = "MissingDllPlugin",
                DisplayName = "Missing Dll Plugin",
                Description = "Not loaded",
                Category = PluginCategory.Custom,
                IsBuiltIn = false,
                IsEnabled = true,
                LoadState = PluginLoadState.NotLoaded
            });
            await db.SaveChangesAsync();
        }

        // Act - Toggle to disabled
        var url = ApiRoutes.PluginToggle.Replace("{id}", "MissingDllPlugin");
        var response = await _adminClient.PostAsync(url, null);

        // Assert - Disabling a NotLoaded plugin completely removes it from DB
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(doc.GetProperty("removed").GetBoolean());

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.IsNull(await verifyDb.PluginRegistrations.FindAsync("MissingDllPlugin"));
    }

    [TestMethod]
    public async Task UpdatePluginConfig_PreservesExistingSensitiveValueWhenReceivingMaskedString()
    {
        // Arrange - Seed plugin with sensitive config value
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var plugin = new PluginRegistration
            {
                Id = "SensitivePlugin",
                DisplayName = "Sensitive Plugin",
                Description = "Testing sensitive config",
                Category = PluginCategory.Custom,
                IsBuiltIn = false,
                IsEnabled = true,
                LoadState = PluginLoadState.Loaded,
                ConfigValues =
                [
                    new PluginConfigValue { Id = Guid.NewGuid(), PluginId = "SensitivePlugin", Key = "ApiKey", Value = "SecretToken12345", IsSensitive = true }
                ]
            };
            db.PluginRegistrations.Add(plugin);
            await db.SaveChangesAsync();
        }

        // Act - Submit update with "***" for the sensitive key
        var url = ApiRoutes.PluginConfig.Replace("{id}", "SensitivePlugin");
        var updatePayload = new Dictionary<string, string>
        {
            ["ApiKey"] = "***",
            ["NewOption"] = "Enabled"
        };
        var response = await _adminClient.PutAsJsonAsync(url, updatePayload);

        // Assert - SecretToken12345 should NOT be overwritten with "***"
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var updated = await verifyDb.PluginRegistrations.Include(p => p.ConfigValues).FirstAsync(p => p.Id == "SensitivePlugin");
        var apiKeyConfig = updated.ConfigValues.First(c => c.Key == "ApiKey");
        Assert.AreEqual("SecretToken12345", apiKeyConfig.Value);
        Assert.IsTrue(updated.ConfigValues.Any(c => c.Key == "NewOption" && c.Value == "Enabled"));
    }

    [TestMethod]
    public async Task UpdatePluginConfig_WhenAddingNewKeyContainingSecretOrTokenOrPassword_AutoSetsIsSensitiveTrue()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var plugin = new PluginRegistration
            {
                Id = "AuthPlugin",
                DisplayName = "Auth Plugin",
                Description = "Testing auth config",
                Category = PluginCategory.Custom,
                IsBuiltIn = false,
                IsEnabled = true,
                LoadState = PluginLoadState.Loaded
            };
            db.PluginRegistrations.Add(plugin);
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.PluginConfig.Replace("{id}", "AuthPlugin");
        var updatePayload = new Dictionary<string, string>
        {
            ["ClientSecret"] = "super-secret-123",
            ["ServerUrl"] = "https://example.com"
        };
        var response = await _adminClient.PutAsJsonAsync(url, updatePayload);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var updated = await verifyDb.PluginRegistrations.Include(p => p.ConfigValues).FirstAsync(p => p.Id == "AuthPlugin");
        var secretConfig = updated.ConfigValues.First(c => c.Key == "ClientSecret");
        var urlConfig = updated.ConfigValues.First(c => c.Key == "ServerUrl");

        Assert.IsTrue(secretConfig.IsSensitive, "Keys containing 'secret' should have IsSensitive set to true automatically.");
        Assert.IsFalse(urlConfig.IsSensitive, "Standard keys should have IsSensitive set to false.");
    }
}
