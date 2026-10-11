using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Core.Features.Agents;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Core.Features.PluginHost.Implementations;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Api.Extensions;

public static class ApplicationInitialization
{
    public static async Task InitializeApplicationAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        }

        var authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await authDb.Database.EnsureCreatedAsync();
        if (authDb.Database.IsSqlite())
        {
            await authDb.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        }

        var tokenDb = scope.ServiceProvider.GetRequiredService<TokenDbContext>();
        await tokenDb.Database.EnsureCreatedAsync();
        if (tokenDb.Database.IsSqlite())
        {
            await tokenDb.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        }

        if (!await authDb.Users.AnyAsync())
        {
            var sysOpts = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SemanticAgentBroker.Shared.Configuration.AgentSystemOptions>>()?.Value 
                          ?? new SemanticAgentBroker.Shared.Configuration.AgentSystemOptions();
            
            var user = new User
            {
                Username = sysOpts.DefaultAdminUsername,
                Role = "Admin"
            };
            user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(user, sysOpts.DefaultAdminPassword);
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        // ── Load Plugins ───────────────────────────────────────────────────
        var pluginLoader = scope.ServiceProvider.GetRequiredService<IPluginLoader>();
        var pluginRegistry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IPluginContextFactory>();
        var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        var initContext = contextFactory.CreateInitContext();

        // Determine which plugins are enabled in the DB (if any exist)
        var existingPlugins = await db.PluginRegistrations.ToListAsync();
        var enabledPluginIds = existingPlugins.Count > 0
            ? existingPlugins.Where(p => p.IsEnabled).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null; // null = first run, load everything

        await pluginLoader.LoadAllAsync(initContext, enabledPluginIds);

        // ── Sync loaded plugins to DB and registry ────────────────────────
        foreach (var loaded in pluginLoader.GetLoadedPlugins())
        {
            pluginRegistry.Register(loaded);

            var existingPlugin = existingPlugins.FirstOrDefault(p => p.Id == loaded.Plugin.Id);
            if (existingPlugin == null)
            {
                db.PluginRegistrations.Add(new PluginRegistration
                {
                    Id = loaded.Plugin.Id,
                    DisplayName = loaded.Plugin.DisplayName,
                    Description = loaded.Plugin.Description,
                    Version = loaded.Plugin.Version,
                    Author = loaded.Plugin.Author,
                    Icon = loaded.Plugin.Icon,
                    Tags = loaded.Plugin.Tags.ToList(),
                    DllFileName = Path.GetFileName(loaded.DllPath),
                    DllHash = loaded.DllHash,
                    IsBuiltIn = loaded.Plugin.Category != PluginCategory.Custom,
                    Category = loaded.Plugin.Category,
                    IsEnabled = loaded.Plugin.Category == PluginCategory.BuiltInRequired,
                    LoadState = PluginLoadState.Loaded,
                    LoadedAt = DateTime.UtcNow,
                    MethodManifestJson = PluginLoader.GenerateMethodManifest(loaded.Plugin), RequiredPlugins = loaded.Plugin.RequiredPlugins.ToList(), TasksJson = System.Text.Json.JsonSerializer.Serialize(loaded.Plugin.GetTasks())
                });
            }
            else
            {
                existingPlugin.DisplayName = loaded.Plugin.DisplayName;
                existingPlugin.Description = loaded.Plugin.Description;
                existingPlugin.Version = loaded.Plugin.Version;
                existingPlugin.Author = loaded.Plugin.Author;
                existingPlugin.Icon = loaded.Plugin.Icon;
                existingPlugin.Tags = loaded.Plugin.Tags.ToList();
                existingPlugin.DllHash = loaded.DllHash;
                existingPlugin.LoadState = PluginLoadState.Loaded;
                existingPlugin.LoadedAt = DateTime.UtcNow;
                existingPlugin.LastError = null;
                existingPlugin.LastErrorAt = null;
                existingPlugin.Category = loaded.Plugin.Category;
                existingPlugin.IsBuiltIn = loaded.Plugin.Category != PluginCategory.Custom;
                existingPlugin.MethodManifestJson = PluginLoader.GenerateMethodManifest(loaded.Plugin);
                existingPlugin.RequiredPlugins = loaded.Plugin.RequiredPlugins.ToList();
                existingPlugin.TasksJson = System.Text.Json.JsonSerializer.Serialize(loaded.Plugin.GetTasks());
            }
        }

        // ── Sync discovered (not enabled) plugins to DB ───────────────────
        foreach (var discovered in pluginLoader.GetDiscoveredPlugins())
        {
            var existingPlugin = existingPlugins.FirstOrDefault(p => p.Id == discovered.PluginId);
            if (existingPlugin == null)
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
                    IsBuiltIn = discovered.Category != PluginCategory.Custom,
                    Category = discovered.Category,
                    IsEnabled = false,
                    LoadState = PluginLoadState.Discovered,
                    LoadedAt = DateTime.UtcNow,
                    MethodManifestJson = discovered.MethodManifestJson,
                    RequiredPlugins = discovered.RequiredPlugins.ToList(),
                    TasksJson = discovered.TasksJson
                });
            }
            else
            {
                // Enabled-but-not-loaded plugins should be marked NotLoaded, not Discovered.
                existingPlugin.LoadState = existingPlugin.IsEnabled ? PluginLoadState.NotLoaded : PluginLoadState.Discovered;
                existingPlugin.DllHash = discovered.DllHash;
                existingPlugin.Category = discovered.Category;
                existingPlugin.IsBuiltIn = discovered.Category != PluginCategory.Custom;
                existingPlugin.MethodManifestJson = discovered.MethodManifestJson;
                existingPlugin.RequiredPlugins = discovered.RequiredPlugins.ToList();
                existingPlugin.TasksJson = discovered.TasksJson;
            }
        }

        // ── Mark enabled-but-missing plugins as NotLoaded ─────────────────
        var pluginIdsOnDisk = pluginLoader.GetAllPluginIdsOnDisk();
        foreach (var dbPlugin in existingPlugins)
        {
            if (dbPlugin.IsEnabled && !pluginIdsOnDisk.Contains(dbPlugin.Id))
            {
                dbPlugin.LoadState = PluginLoadState.NotLoaded;
                dbPlugin.LastError = "Plugin DLL not found on disk";
                dbPlugin.LastErrorAt = DateTime.UtcNow;
                startupLogger.LogWarning("Plugin '{PluginId}' is enabled but DLL not found - marked as NotLoaded", dbPlugin.Id);
            }
        }

        await db.SaveChangesAsync();

        // Wire up hot-load events - queue discovered plugins for async DB persistence
        // instead of blocking the FileSystemWatcher thread with synchronous SaveChanges.
        pluginLoader.PluginDiscovered += discoveredPlugin =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var eventScope = app.Services.CreateScope();
                    var eventDb = eventScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var existingInDb = await eventDb.PluginRegistrations.FindAsync(discoveredPlugin.PluginId);
                    if (existingInDb == null)
                    {
                        eventDb.PluginRegistrations.Add(new PluginRegistration
                        {
                            Id = discoveredPlugin.PluginId,
                            DisplayName = discoveredPlugin.DisplayName,
                            Description = discoveredPlugin.Description,
                            Version = discoveredPlugin.Version,
                            Author = discoveredPlugin.Author,
                            Icon = discoveredPlugin.Icon,
                            Tags = discoveredPlugin.Tags.ToList(),
                            DllFileName = Path.GetFileName(discoveredPlugin.DllPath),
                            DllHash = discoveredPlugin.DllHash,
                            IsBuiltIn = false,
                            IsEnabled = false,
                            LoadState = PluginLoadState.Discovered,
                            LoadedAt = DateTime.UtcNow,
                            MethodManifestJson = discoveredPlugin.MethodManifestJson
                        });
                        await eventDb.SaveChangesAsync();
                        startupLogger.LogInformation("Hot-discovered new plugin '{PluginId}' - added to DB as Discovered", discoveredPlugin.PluginId);
                    }
                }
                catch (DbUpdateException)
                {
                    // Already inserted concurrently by another watcher event
                    startupLogger.LogDebug("Hot-discovered plugin '{PluginId}' was already added to DB.", discoveredPlugin.PluginId);
                }
                catch (Exception ex)
                {
                    startupLogger.LogError(ex, "Failed to persist hot-discovered plugin '{PluginId}' to DB", discoveredPlugin.PluginId);
                }
            });
        };
        pluginLoader.StartWatching(initContext);

        // ── Load Agent Definitions from DB ─────────────────────────────────
        var IAgentRegistry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
        var agents = await db.AgentDefinitions
            .Include(a => a.MethodBindings)
            .Where(a => a.IsEnabled)
            .ToListAsync();
        IAgentRegistry.LoadAll(agents);

        // ── Seed Default Agents (first-run only) ───────────────────────────
        if (!agents.Any())
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("No agents found. Seeding default agents...");

            var defaultAgents = CreateDefaultAgents(pluginRegistry);
            foreach (var agent in defaultAgents)
            {
                db.AgentDefinitions.Add(agent);
            }
            await db.SaveChangesAsync();
            IAgentRegistry.LoadAll(defaultAgents);
            logger.LogInformation("Seeded {Count} default agent(s)", defaultAgents.Count);
        }

        // ── Recover Orphaned Tasks ─────────────────────────────────────────
        var orphanedStates = new[] { TaskState.InProgress, TaskState.AwaitingApproval, TaskState.AwaitingInput, TaskState.Planning, TaskState.PlanReview, TaskState.Queued, TaskState.Paused };
        var orphanedTasks = await db.TaskSessions
            .Where(t => orphanedStates.Contains(t.State))
            .ToListAsync();

        if (orphanedTasks.Any())
        {
            var queue = scope.ServiceProvider.GetRequiredService<AgentTaskQueueService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            foreach (var session in orphanedTasks)
            {
                logger.LogInformation("Recovering orphaned task {TaskId} (was {State})", session.Id, session.State);
                session.State = TaskState.Draft;
            }
            // Batch all state changes into a single DB write instead of N writes
            await db.SaveChangesAsync();

            foreach (var session in orphanedTasks)
            {
                await queue.EnqueueTaskAsync(new AgentTaskRequest
                {
                    TaskId = session.Id,
                    AgentName = session.AgentName,
                    Prompt = session.UserPrompt,
                    Mode = session.Mode,
                    DebugMode = session.DebugMode,
                    TraceMode = session.TraceMode,
                    MaxSteps = session.MaxSteps,
                    IsContinuation = true,  // Resume from persisted ChatHistory
                    PluginTaskId = session.PluginTaskId,
                    SourcePluginId = session.SourcePluginId
                });
            }
            logger.LogInformation("Recovered {Count} orphaned task(s)", orphanedTasks.Count);
        }

        var aiOptions = scope.ServiceProvider.GetRequiredService<SemanticAgentBroker.Shared.Configuration.AiProviderOptions>();

        // ── Clean up orphaned Docker Sandbox Containers ────────────────────
        try
        {
            var dockerUri = aiOptions.PythonSandbox.DockerUri;
            if (string.IsNullOrEmpty(dockerUri))
            {
                var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
                dockerUri = !string.IsNullOrEmpty(dockerHost)
                    ? dockerHost
                    : Environment.OSVersion.Platform == PlatformID.Win32NT
                        ? "npipe://./pipe/docker_engine"
                        : "unix:///var/run/docker.sock";
            }
            using var dockerClient = new Docker.DotNet.DockerClientConfiguration(new Uri(dockerUri)).CreateClient();
            var containers = await dockerClient.Containers.ListContainersAsync(new Docker.DotNet.Models.ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { ["semantic-broker-sandbox=true"] = true }
                }
            });
            int cleanedCount = 0;
            foreach (var container in containers)
            {
                try
                {
                    await dockerClient.Containers.RemoveContainerAsync(container.ID, new Docker.DotNet.Models.ContainerRemoveParameters { Force = true });
                    cleanedCount++;
                }
                catch { /* best-effort */ }
            }
            if (cleanedCount > 0)
            {
                startupLogger.LogInformation("Cleaned up {Count} orphaned sandbox container(s) on startup.", cleanedCount);
            }
        }
        catch (Exception ex)
        {
            startupLogger.LogDebug(ex, "Docker daemon not reachable during startup orphaned container cleanup check.");
        }

        // ── Insecure TLS Validation Warning ───────────────────────────────
        var insecureConnections = aiOptions.Connections
            .Where(c => c.IsEnabled && c.TrustUntrustedCertificates)
            .ToList();

        if (insecureConnections.Count > 0)
        {
            var connNames = string.Join(", ", insecureConnections.Select(c => $"'{c.EndpointName}' ({c.Id})"));
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine("╔==========================================================================================╗");
            Console.WriteLine("║ [SECURITY WARNING] Insecure TLS Certificate Validation Enabled                           ║");
            Console.WriteLine("║ One or more AI provider connections have 'TrustUntrustedCertificates' enabled:           ║");
            Console.WriteLine($"║   Connections: {connNames,-74}║");
            Console.WriteLine("║ This setting bypasses SSL/TLS validation and is intended ONLY for local development or   ║");
            Console.WriteLine("║ testing purposes. Ensure 'TrustUntrustedCertificates' is set to FALSE in production!     ║");
            Console.WriteLine("╚==========================================================================================╝");
            Console.WriteLine();
            Console.ResetColor();

            startupLogger.LogWarning(
                "AI provider connections [{Connections}] have TrustUntrustedCertificates=true. This bypasses TLS validation and MUST NOT be used in production.",
                connNames);
        }
    }

    private static List<AgentDefinition> CreateDefaultAgents(IPluginRegistry pluginRegistry)
    {
        var agents = new List<AgentDefinition>();

        // Helper to create bindings for all methods of a plugin
        List<AgentMethodBinding> BindAllMethods(params string[] pluginIds)
        {
            var bindings = new List<AgentMethodBinding>();
            foreach (var pluginId in pluginIds)
            {
                var plugin = pluginRegistry.GetPlugin(pluginId);
                if (plugin == null) continue;
                foreach (var method in plugin.Plugin.GetMethods())
                {
                    bindings.Add(new AgentMethodBinding { PluginId = pluginId, MethodId = method.Id });
                }
            }
            return bindings;
        }

        // 1. Data Analyst - Python + Sample Data
        agents.Add(new AgentDefinition
        {
            Name = "DataAnalyst",
            Description = "Fetches sample datasets and performs statistical analysis using Python sandbox. Start by fetching data with GetSalesData, then analyze it with Python.",
            Icon = "trending_up",
            AccentClass = "cyan",
            IsSeeded = true,
            SystemInstruction = """
                You are a Data Analysis Agent.
                You have access to sample datasets via the SampleData plugin and a Python sandbox.
                
                Workflow:
                1. First, fetch the required data using SampleData methods (GetSalesData, GetUserList, etc.)
                2. Save the data to the workspace using FileIO-SaveToWorkspace
                3. Inspect data columns and sample rows first (e.g. print(df.columns); print(df.head(2)))
                4. Write and execute Python scripts to analyze the data using ExecutePythonScript
                
                Libraries available in Python: pandas, numpy, scipy, matplotlib, seaborn, tabulate.
                Always print results to stdout so they appear in the output. Use tabulate or df.to_markdown() for tables.
                When saving data for Python, use CSV format and save to the workspace.
                The Python sandbox can read files from /app/workdir/.
                """,
            MethodBindings = BindAllMethods("SampleData", "PythonSandbox", "FileIO")
        });

        // 2. Log Analyzer - Python + Sample Logs
        agents.Add(new AgentDefinition
        {
            Name = "LogAnalyzer",
            Description = "Analyzes server logs from sample data. Counts errors, groups by type, and generates reports with fix suggestions.",
            Icon = "search",
            AccentClass = "amber",
            IsSeeded = true,
            SystemInstruction = """
                You are a Server Log Analysis Agent.
                You analyze server logs to identify issues, count errors, group by type, and suggest fixes.
                
                Workflow:
                1. Fetch logs using SampleData-GetServerLogs
                2. Save the logs to the workspace
                3. Use Python to parse and analyze the logs
                4. Generate a structured Markdown report with:
                   - Total error/warning/info counts
                   - Errors grouped by service
                   - Top recurring error messages
                   - Suggested fixes for each error type
                
                Always present your findings in a clear, structured format.
                """,
            MethodBindings = BindAllMethods("SampleData", "PythonSandbox", "FileIO")
        });

        // 3. General-purpose Assistant
        agents.Add(new AgentDefinition
        {
            Name = "GeneralAssistant",
            Description = "A versatile assistant with access to Python execution, file I/O, HTTP requests, and sample data.",
            Icon = "smart_toy",
            AccentClass = "indigo",
            IsSeeded = true,
            SystemInstruction = """
                You are a General-Purpose Assistant.
                You have access to multiple tools including Python execution, file operations, HTTP requests, and sample data.
                Choose the appropriate tool for each task.
                For data processing, use Python. For file operations, use the File I/O tools.
                For fetching external data, use the HTTP Client.
                For testing with sample data, use the SampleData plugin.
                Always work methodically and explain your reasoning.
                """,
            MethodBindings = BindAllMethods("PythonSandbox", "FileIO", "HttpClient", "SampleData")
        });

        return agents;
    }
}
