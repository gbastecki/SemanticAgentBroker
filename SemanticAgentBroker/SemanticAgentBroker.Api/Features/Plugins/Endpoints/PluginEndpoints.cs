using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Core.Features.PluginHost.Implementations;
using SemanticAgentBroker.Shared.Constants;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.Api.Features.Auth;

namespace SemanticAgentBroker.Api.Features.Plugins.Endpoints;

public static class PluginEndpoints
{
    public static void MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        // List all plugins with their state
        app.MapGet(ApiRoutes.PluginsBase, async (HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var session = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
            var isAdmin = session?.Role == "Admin";

            var plugins = await db.PluginRegistrations
                .AsNoTracking()
                .OrderBy(p => p.DisplayName)
                .ToListAsync();


            if (!isAdmin && session != null)
            {
                var allowedPerms = await authDb.GetEffectivePermissionsAsync(session.UserId);

                var allowedPluginIds = allowedPerms.Select(p => p.PluginId).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var filteredPlugins = plugins.Where(p => allowedPluginIds.Contains(p.Id)).ToList();
                foreach (var plugin in filteredPlugins)
                {
                    if (!string.IsNullOrEmpty(plugin.TasksJson))
                    {
                        var tasks = System.Text.Json.JsonSerializer.Deserialize<List<SemanticAgentBroker.Sdk.PluginTaskDef>>(plugin.TasksJson);
                        if (tasks != null)
                        {
                            var hasWildcard = allowedPerms.Any(p => string.Equals(p.PluginId, plugin.Id, StringComparison.OrdinalIgnoreCase) && p.PluginTaskId == "*");
                            var allowedTaskIds = allowedPerms.Where(p => string.Equals(p.PluginId, plugin.Id, StringComparison.OrdinalIgnoreCase))
                                .Select(p => p.PluginTaskId)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            var filteredTasks = tasks.Where(t => hasWildcard || allowedTaskIds.Contains(t.Id)).ToList();
                            var customTasks = filteredTasks.Select(t => new { 
                                t.Id, t.Name, t.Description,
                                AllowedOptions = allowedPerms.GetAllowedOptions(plugin.Id, t.Id)
                            }).ToList();
                            plugin.TasksJson = System.Text.Json.JsonSerializer.Serialize(customTasks);
                        }
                    }
                }
                plugins = filteredPlugins;
            }

            else
            {
                // Admin gets everything, but full AllowedOptions must be injected into tasks so UI works
                foreach (var plugin in plugins)
                {
                    if (!string.IsNullOrEmpty(plugin.TasksJson))
                    {
                        var tasks = System.Text.Json.JsonSerializer.Deserialize<List<SemanticAgentBroker.Sdk.PluginTaskDef>>(plugin.TasksJson);
                        if (tasks != null)
                        {
                            var customTasks = tasks.Select(t => new { 
                                t.Id, t.Name, t.Description,
                                AllowedOptions = new List<string> { "AutoExecution", "UploadFiles", "EditFiles", "ConsoleView", "DebugMode", "TraceMode", "Delete" }
                            }).ToList();
                            plugin.TasksJson = System.Text.Json.JsonSerializer.Serialize(customTasks);
                        }
                    }
                }
            }

            var list = plugins.Select(p => new
            {
                p.Id,
                p.DisplayName,
                p.Description,
                p.Version,
                p.Author,
                p.Icon,
                p.Tags,
                p.IsEnabled,
                p.IsBuiltIn,
                Category = p.Category.ToString(),
                LoadState = p.LoadState.ToString(),
                p.LoadedAt,
                p.LastError,
                p.DllFileName,
                p.RequiredPlugins,
                p.TasksJson,
                p.MethodManifestJson
            });
            return Results.Ok(list);
        });

        var adminGroup = app.MapGroup("");
        adminGroup.AddEndpointFilter(async (context, next) =>
        {
            var session = context.HttpContext.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
            if (session == null || session.Role != "Admin") return Results.Forbid();
            return await next(context);
        });

        // Get plugin details (admin only - exposes config values)
        adminGroup.MapGet(ApiRoutes.PluginById, async (string id, AppDbContext db) =>
        {
            var plugin = await db.PluginRegistrations
                .Include(p => p.ConfigValues)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (plugin == null) return Results.NotFound();

            return Results.Ok(new
            {
                plugin.Id,
                plugin.DisplayName,
                plugin.Description,
                plugin.Version,
                plugin.Author,
                plugin.Icon,
                plugin.Tags,
                plugin.IsEnabled,
                plugin.IsBuiltIn,
                LoadState = plugin.LoadState.ToString(),
                plugin.DllFileName,
                plugin.LoadedAt,
                plugin.LastError,
                plugin.MethodManifestJson,
                Config = plugin.ConfigValues.Select(c => new
                {
                    c.Key,
                    Value = c.IsSensitive ? "***" : c.Value,
                    c.IsSensitive
                })
            });
        });

        // Enable/disable a plugin (with lifecycle transitions)
        adminGroup.MapPost(ApiRoutes.PluginToggle, async (string id, AppDbContext db, IPluginLoader loader, IPluginRegistry registry, IPluginContextFactory contextFactory) =>
        {
            var plugin = await db.PluginRegistrations.FindAsync(id);
            if (plugin == null) return Results.NotFound();

            // Reject disable for required built-in plugins
            if (plugin.Category == SemanticAgentBroker.Sdk.PluginCategory.BuiltInRequired)
                return Results.BadRequest($"Cannot toggle required built-in plugin '{plugin.DisplayName}'. It must always be enabled.");

            if (plugin.IsEnabled)
            {
                // Disabling
                plugin.IsEnabled = false;
                registry.Unregister(id);
                await loader.UnloadPluginAsync(id);

                if (plugin.LoadState == PluginLoadState.NotLoaded)
                {
                    // Not loaded + disabling = safe to remove from DB
                    db.PluginRegistrations.Remove(plugin);
                    await db.SaveChangesAsync();
                    return Results.Ok(new { id, IsEnabled = false, Removed = true });
                }

                plugin.LoadState = PluginLoadState.Discovered;
                await db.SaveChangesAsync();
                return Results.Ok(new { id, plugin.IsEnabled, LoadState = plugin.LoadState.ToString() });
            }
            else
            {
                // Enabling - attempt to load the plugin
                plugin.IsEnabled = true;
                var initContext = contextFactory.CreateInitContext();
                var loaded = await loader.LoadPluginAsync(id, initContext);

                if (loaded != null)
                {
                    registry.Register(loaded);
                    plugin.LoadState = PluginLoadState.Loaded;
                    plugin.LastError = null;
                    plugin.LastErrorAt = null;
                    plugin.DllHash = loaded.DllHash;
                    plugin.LoadedAt = DateTime.UtcNow;
                    plugin.MethodManifestJson = PluginLoader.GenerateMethodManifest(loaded.Plugin);
                    plugin.RequiredPlugins = loaded.Plugin.RequiredPlugins.ToList();
                    plugin.TasksJson = System.Text.Json.JsonSerializer.Serialize(loaded.Plugin.GetTasks());
                }
                else
                {
                    plugin.LoadState = PluginLoadState.NotLoaded;
                    plugin.LastError = "Plugin DLL not found or failed to load";
                    plugin.LastErrorAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync();
                return Results.Ok(new { id, plugin.IsEnabled, LoadState = plugin.LoadState.ToString() });
            }
        });

        // Reload a single plugin
        adminGroup.MapPost(ApiRoutes.PluginReload, async (string id, AppDbContext db, IPluginLoader loader, IPluginRegistry registry, IPluginContextFactory contextFactory) =>
        {
            var plugin = await db.PluginRegistrations.FindAsync(id);
            if (plugin == null) return Results.NotFound();

            // Unload first
            registry.Unregister(id);
            await loader.UnloadPluginAsync(id);

            // Re-load
            var initContext = contextFactory.CreateInitContext();
            var loaded = await loader.LoadPluginAsync(id, initContext);

            if (loaded != null)
            {
                registry.Register(loaded);
                plugin.LoadState = PluginLoadState.Loaded;
                plugin.LastError = null;
                plugin.LastErrorAt = null;
                plugin.DllHash = loaded.DllHash;
                plugin.LoadedAt = DateTime.UtcNow;
                plugin.MethodManifestJson = PluginLoader.GenerateMethodManifest(loaded.Plugin);
                plugin.RequiredPlugins = loaded.Plugin.RequiredPlugins.ToList();
                plugin.TasksJson = System.Text.Json.JsonSerializer.Serialize(loaded.Plugin.GetTasks());
                await db.SaveChangesAsync();
                return Results.Ok(new { id, plugin.LoadState, Reloaded = true });
            }

            plugin.LoadState = PluginLoadState.NotLoaded;
            plugin.LastError = "Plugin DLL not found on disk during reload";
            plugin.LastErrorAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { id, plugin.LoadState, Reloaded = false });
        });

        // Update plugin configuration
        adminGroup.MapPut(ApiRoutes.PluginConfig, async (string id, [FromBody] Dictionary<string, string> config, AppDbContext db) =>
        {
            var plugin = await db.PluginRegistrations
                .Include(p => p.ConfigValues)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (plugin == null) return Results.NotFound();

            foreach (var kvp in config)
            {
                var existing = plugin.ConfigValues.FirstOrDefault(c => c.Key == kvp.Key);
                if (existing != null)
                {
                    if (existing.IsSensitive && (kvp.Value == "***" || string.IsNullOrEmpty(kvp.Value)))
                        continue;
                    existing.Value = kvp.Value;
                }
                else
                {
                    if (kvp.Value == "***")
                        continue;

                    bool isSensitive = kvp.Key.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                                       kvp.Key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                                       kvp.Key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                                       kvp.Key.Contains("password", StringComparison.OrdinalIgnoreCase);

                    db.PluginConfigValues.Add(new PluginConfigValue 
                    { 
                        PluginId = id, 
                        Key = kvp.Key, 
                        Value = kvp.Value,
                        IsSensitive = isSensitive
                    });
                }
            }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // List tasks for a plugin with their enabled state
        app.MapGet(ApiRoutes.PluginTasks, async (string id, HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var session = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
            var isAdmin = session?.Role == "Admin";

            var plugin = await db.PluginRegistrations.FindAsync(id);
            if (plugin == null || string.IsNullOrEmpty(plugin.TasksJson)) 
                return Results.NotFound();

            var tasks = System.Text.Json.JsonSerializer.Deserialize<List<SemanticAgentBroker.Sdk.PluginTaskDef>>(plugin.TasksJson);
            if (tasks == null || !tasks.Any())
                return Results.Ok(new List<object>());

            var states = await db.TaskEnabledStates
                .Where(t => t.PluginId == id)
                .ToListAsync();

            List<EvaluatedPermission>? allowedPerms = null;
            if (!isAdmin && session != null)
            {
                allowedPerms = await authDb.GetEffectivePermissionsAsync(session.UserId);
                allowedPerms = allowedPerms.Where(p => string.Equals(p.PluginId, id, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var result = tasks.Select(t => new
            {
                t.Id,
                t.Name,
                t.Description,
                IsEnabled = states.FirstOrDefault(s => s.TaskId == t.Id)?.IsEnabled ?? t.EnabledByDefault,
                AllowedOptions = isAdmin 
                    ? new List<string> { "AutoExecution", "UploadFiles", "EditFiles", "ConsoleView", "DebugMode", "TraceMode", "Delete" }
                    : (allowedPerms != null ? allowedPerms.GetAllowedOptions(id, t.Id) : new List<string>())
            });

            if (!isAdmin && session != null)
            {
                result = result.Where(t => allowedPerms!.Any(p => string.Equals(p.PluginTaskId, t.Id, StringComparison.OrdinalIgnoreCase) || p.PluginTaskId == "*"));
            }

            return Results.Ok(result);
        });

        // Toggle a task's enabled state
        adminGroup.MapPost(ApiRoutes.PluginTaskToggle, async (string id, string taskId, AppDbContext db) =>
        {
            var plugin = await db.PluginRegistrations.FindAsync(id);
            if (plugin == null) return Results.NotFound();

            var state = await db.TaskEnabledStates
                .FirstOrDefaultAsync(t => t.PluginId == id && t.TaskId == taskId);

            if (state == null)
            {
                state = new TaskEnabledState
                {
                    PluginId = id,
                    TaskId = taskId,
                    IsEnabled = false // Default is true, so toggle makes it false
                };
                db.TaskEnabledStates.Add(state);
            }
            else
            {
                state.IsEnabled = !state.IsEnabled;
            }

            await db.SaveChangesAsync();
            return Results.Ok(new { id, taskId, IsEnabled = state.IsEnabled });
        });

        // Force rescan all plugins
        adminGroup.MapPost(ApiRoutes.PluginsReloadAll, async (AppDbContext db, IPluginLoader loader, IPluginRegistry registry, IPluginContextFactory contextFactory) =>
        {
            var initContext = contextFactory.CreateInitContext();
            var enabledIds = await db.PluginRegistrations.Where(p => p.IsEnabled).Select(p => p.Id).ToListAsync();
            await loader.LoadAllAsync(initContext, enabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase));

            foreach (var loaded in loader.GetLoadedPlugins())
            {
                if (!registry.Contains(loaded.Plugin.Id))
                    registry.Register(loaded);
            }

            // Sync discovered to DB
            foreach (var discovered in loader.GetDiscoveredPlugins())
            {
                var existing = await db.PluginRegistrations.FindAsync(discovered.PluginId);
                if (existing == null)
                {
                    db.PluginRegistrations.Add(new PluginRegistration
                    {
                        Id = discovered.PluginId,
                        DisplayName = discovered.DisplayName,
                        Description = discovered.Description,
                        Version = discovered.Version,
                        Author = discovered.Author,
                        Icon = discovered.Icon,
                        Tags = discovered.Tags.ToList(),
                        DllFileName = Path.GetFileName(discovered.DllPath),
                        DllHash = discovered.DllHash,
                        IsBuiltIn = false,
                        IsEnabled = false,
                        LoadState = PluginLoadState.Discovered,
                        LoadedAt = DateTime.UtcNow,
                        MethodManifestJson = discovered.MethodManifestJson
                    });
                }
            }
            await db.SaveChangesAsync();

            return Results.Ok(new { loaded = registry.GetAllPlugins().Count, discovered = loader.GetDiscoveredPlugins().Count });
        });
    }
}
