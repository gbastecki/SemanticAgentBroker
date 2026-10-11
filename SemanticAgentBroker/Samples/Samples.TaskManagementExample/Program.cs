using Samples.Common.Client;
using SemanticAgentBroker.Shared.Domain;

namespace Samples.TaskManagementExample;

/// <summary>
/// Sample demonstrating administrative and lifecycle task management with Semantic Agent Broker:
/// querying system execution statistics, paginating tasks, pausing and resuming in-flight tasks,
/// restarting tasks, and single/bulk task deletion.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        PrintHeader("Semantic Agent Broker - Task Management Sample");

        var baseUrl = args.Length > 0 ? args[0] : "https://localhost:5100";
        var username = args.Length > 1 ? args[1] : "admin";
        var password = args.Length > 2 ? args[2] : "admin";

        Console.WriteLine($"Broker Endpoint: {baseUrl}");
        Console.WriteLine($"Connecting as:   {username}\n");

        using var client = new SemanticAgentBrokerClient(
            baseUrl: baseUrl,
            username: username,
            password: password,
            browserId: "task-mgmt-sample-client"
        );

        try
        {
            // ── Step 1: Query System Execution Statistics ───────────────────────
            PrintSection("1. Querying System Task Statistics");

            var stats = await client.GetTaskStatsAsync();
            if (stats != null)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  • Total Tasks:     {stats.TotalCount}");
                Console.WriteLine($"  • Active Tasks:    {stats.ActiveCount}");
                Console.WriteLine($"  • Completed Tasks: {stats.CompletedCount}");
                Console.WriteLine($"  • Failed Tasks:    {stats.FailedCount}");
                Console.ResetColor();
            }

            // ── Step 2: List Tasks with Pagination ──────────────────────────────
            PrintSection("2. Listing Tasks (Paginated Query)");

            var page = await client.ListTasksAsync(page: 1, pageSize: 5);
            Console.WriteLine($"Page {page.Page} of {page.TotalPages} (Total items: {page.TotalCount}):");
            Console.WriteLine(new string('-', 75));
            Console.WriteLine($"{"Task ID",-38} {"State",-14} {"Mode",-8} {"Created"}");
            Console.WriteLine(new string('-', 75));

            foreach (var item in page.Items)
            {
                Console.WriteLine($"{item.Id,-38} {item.State,-14} {item.Mode,-8} {item.CreatedAt:yyyy-MM-dd HH:mm}");
            }
            Console.WriteLine(new string('-', 75));

            // ── Step 3: Pause & Resume Lifecycle Demo ──────────────────────────
            PrintSection("3. Pause & Resume Task Lifecycle");

            Console.WriteLine("Creating a multi-step audit task...");
            var taskRequest = new AgentTaskRequest
            {
                AgentName = "GeneralAssistant",
                Prompt = "Calculate the first 25 Fibonacci numbers using Python, verify each number, and write the sequence to 'fibonacci.txt'.",
                Mode = ExecutionMode.Auto,
                MaxSteps = 6,
                DebugMode = true
            };

            var createdTask = await client.CreateTaskAsync(taskRequest);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Task enqueued! Task ID: {createdTask.TaskId}");
            Console.ResetColor();

            // Wait until the task is picked up (Queued or InProgress or Planning)
            Console.WriteLine("Waiting for task to begin execution...");
            await Task.Delay(1500);

            Console.WriteLine($"Attempting to pause task {createdTask.TaskId}...");
            var paused = await client.PauseTaskAsync(createdTask.TaskId);
            if (paused)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("✓ Pause request accepted by Broker.");
                Console.ResetColor();

                var current = await client.GetTaskAsync(createdTask.TaskId);
                Console.WriteLine($"Current task state: {current?.State}");

                Console.WriteLine("Holding task in paused state for 3 seconds...");
                await Task.Delay(3000);

                Console.WriteLine("Resuming task execution...");
                var resumed = await client.ResumeTaskAsync(createdTask.TaskId);
                if (resumed)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✓ Resume request accepted by Broker. Task resumed!");
                    Console.ResetColor();
                }
            }

            // Await completion of this task
            Console.WriteLine("Awaiting task completion...");
            var completedTask = await client.AwaitTaskCompletionAsync(
                createdTask.TaskId,
                timeout: TimeSpan.FromMinutes(3),
                pollInterval: TimeSpan.FromSeconds(2),
                onProgress: s => Console.Write(".")
            );
            Console.WriteLine($"\nTask finished with state: {completedTask.State}");

            // ── Step 4: Restart Task Workflow ──────────────────────────────────
            PrintSection("4. Restarting Finished Task from Scratch");

            Console.WriteLine($"Restarting task {createdTask.TaskId}...");
            var restarted = await client.RestartTaskAsync(createdTask.TaskId);
            if (restarted != null)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓ Task restarted successfully! Prior state and events cleared.");
                Console.ResetColor();

                var current = await client.GetTaskAsync(createdTask.TaskId);
                Console.WriteLine($"Post-restart state: {current?.State} (StartedAt: {current?.StartedAt?.ToString() ?? "null"})");
            }

            // ── Step 5: Task Deletion & Bulk Cleanup ────────────────────────────
            PrintSection("5. Single & Bulk Task Deletion");

            // Single deletion
            Console.WriteLine($"Deleting single task {createdTask.TaskId}...");
            var deleted = await client.DeleteTaskAsync(createdTask.TaskId);
            if (deleted)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Task {createdTask.TaskId} successfully cancelled and deleted.");
                Console.ResetColor();
            }

            // Bulk deletion demo
            Console.WriteLine("\nCreating two transient tasks to demonstrate bulk deletion...");
            var batchTask1 = await client.CreateTaskAsync(new AgentTaskRequest
            {
                AgentName = "GeneralAssistant",
                Prompt = "Transient test batch item 1",
                MaxSteps = 1
            });
            var batchTask2 = await client.CreateTaskAsync(new AgentTaskRequest
            {
                AgentName = "GeneralAssistant",
                Prompt = "Transient test batch item 2",
                MaxSteps = 1
            });

            Console.WriteLine($"Created task 1: {batchTask1.TaskId}");
            Console.WriteLine($"Created task 2: {batchTask2.TaskId}");

            var batchIds = new[] { batchTask1.TaskId, batchTask2.TaskId };
            Console.WriteLine($"Executing bulk delete for {batchIds.Length} tasks...");

            var deleteCount = await client.BulkDeleteTasksAsync(batchIds);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Bulk delete completed: {deleteCount} tasks purged atomically!");
            Console.ResetColor();

            PrintHeader("Task Management Sample Completed Successfully");
        }
        catch (HttpRequestException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] Network error communicating with Semantic Agent Broker: {ex.Message}");
            Console.WriteLine("Make sure SemanticAgentBroker.Api is running on " + baseUrl);
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] An unexpected error occurred: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
        }
    }

    private static void PrintHeader(string title)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(new string('=', 70));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', 70));
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintSection(string title)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n>>> {title}");
        Console.WriteLine(new string('-', 50));
        Console.ResetColor();
    }
}
