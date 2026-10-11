using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Api.Features.Tasks.Services;

/// <summary>
/// Background service that runs periodically (hourly) to delete completed/failed tasks (and their workspaces)
/// older than TasksCleanupDays.
/// </summary>
public class TasksCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AgentSystemOptions _systemOptions;
    private readonly ILogger<TasksCleanupService> _logger;

    public TasksCleanupService(
        IServiceProvider serviceProvider,
        AgentSystemOptions systemOptions,
        ILogger<TasksCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _systemOptions = systemOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TasksCleanupService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Prune expired token sessions from tokens.db on every maintenance cycle
                using (var scope = _serviceProvider.CreateScope())
                {
                    var authService = scope.ServiceProvider.GetRequiredService<SemanticAgentBroker.Api.Features.Auth.AuthService>();
                    await authService.PruneExpiredTokensAsync(stoppingToken);
                }

                // Clean up expired tasks if enabled
                if (_systemOptions.IsTasksCleanupServiceEnabled && _systemOptions.TasksCleanupDays > 0)
                {
                    await CleanupOldTasksAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing periodic maintenance/cleanup.");
            }

            // Run hourly
            var jitter = Random.Shared.NextDouble() * 10.0; // up to 10 minutes
            await Task.Delay(TimeSpan.FromMinutes(60 + jitter), stoppingToken);
        }
    }

    public async Task CleanupOldTasksAsync(CancellationToken stoppingToken)
    {
        List<TaskSession> expiredTasks;
        using (var initialScope = _serviceProvider.CreateScope())
        {
            var taskStore = initialScope.ServiceProvider.GetRequiredService<AgentTaskStore>();
            var cutoffDate = DateTime.UtcNow.AddDays(-_systemOptions.TasksCleanupDays);
            expiredTasks = await taskStore.GetExpiredTasksAsync(cutoffDate, stoppingToken);
        }

        if (expiredTasks.Count == 0)
            return;

        int deletedCount = 0;
        foreach (var task in expiredTasks)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                using var taskScope = _serviceProvider.CreateScope();
                var db = taskScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var taskStore = taskScope.ServiceProvider.GetRequiredService<AgentTaskStore>();
                var taskOps = taskScope.ServiceProvider.GetRequiredService<TaskOperationsService>();
                var stateManager = taskScope.ServiceProvider.GetService<SemanticAgentBroker.Core.Features.TaskState.ITaskStateManager>();
                var lockManager = taskScope.ServiceProvider.GetService<SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager>();

                // Verify the task is not currently active in memory
                if (taskOps.IsTaskActive(task.Id))
                {
                    continue;
                }

                // Re-verify current database state atomically before deleting
                var currentSession = await db.TaskSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == task.Id, stoppingToken);
                if (currentSession == null || (currentSession.State != TaskState.Completed && currentSession.State != TaskState.Failed && currentSession.State != TaskState.Cancelled))
                {
                    continue; // Task state changed (e.g. restarted or resumed)
                }

                taskOps.CancelTask(task.Id);

                await taskStore.DeleteTaskWithCleanupAsync(task.Id, _systemOptions.CleanupWorkspacesOnTaskDelete, stoppingToken);
                stateManager?.CleanupSession(task.Id);
                lockManager?.CleanupSession(task.Id);
                deletedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clean up expired task {TaskId}.", task.Id);
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation("Cleanup complete. Deleted {Count} completed/failed old tasks.", deletedCount);
        }
    }
}
