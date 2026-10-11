using Samples.Common.Client;
using SemanticAgentBroker.Shared.Domain;

namespace Samples.RestApiIntegrationExample;

/// <summary>
/// Sample demonstrating how external client applications submit tasks to Semantic Agent Broker,
/// poll and await until execution produces a final result, inspect event logs, download workspace
/// artifacts, and send follow-up continuation prompts.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        PrintHeader("Semantic Agent Broker - REST API Integration Sample");

        var baseUrl = args.Length > 0 ? args[0] : "https://localhost:5100";
        var username = args.Length > 1 ? args[1] : "admin";
        var password = args.Length > 2 ? args[2] : "admin";

        Console.WriteLine($"Broker Endpoint: {baseUrl}");
        Console.WriteLine($"Connecting as:   {username}\n");

        using var client = new SemanticAgentBrokerClient(
            baseUrl: baseUrl,
            username: username,
            password: password,
            browserId: "rest-api-sample-client"
        );

        try
        {
            // ── Step 1: Submit an Autonomous Agent Task ────────────────────────
            PrintSection("1. Submitting Autonomous Agent Task");

            var taskRequest = new AgentTaskRequest
            {
                AgentName = "GeneralAssistant",
                Prompt = "Compare the time complexity of quicksort, mergesort, and bubblesort. Create a markdown report named 'algorithm_comparison.md' summarizing the comparison with best, average, and worst case numbers.",
                Mode = ExecutionMode.Auto,
                DebugMode = true,
                TraceMode = false,
                MaxSteps = 6,
                IsPrivate = false
            };

            Console.WriteLine($"Submitting task with prompt: \"{taskRequest.Prompt}\"");
            var createdTask = await client.CreateTaskAsync(taskRequest);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Task enqueued successfully! Task ID: {createdTask.TaskId}\n");
            Console.ResetColor();

            // ── Step 2: Await Task Completion with Status Polling ─────────────
            PrintSection("2. Polling & Awaiting Final Result");

            var lastState = TaskState.Draft;
            var completedTask = await client.AwaitTaskCompletionAsync(
                createdTask.TaskId,
                timeout: TimeSpan.FromMinutes(4),
                pollInterval: TimeSpan.FromSeconds(2),
                onProgress: session =>
                {
                    if (session.State != lastState)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Status changed -> {session.State}");
                        lastState = session.State;
                    }
                    else
                    {
                        Console.Write(".");
                    }
                }
            );

            Console.WriteLine();
            if (completedTask.State == TaskState.Completed)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n✓ Task Completed successfully! (Duration: {(completedTask.CompletedAt - completedTask.StartedAt)?.TotalSeconds:F1}s)");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n⚠ Task ended in terminal state: {completedTask.State}");
                Console.ResetColor();
            }

            // Print Final Result
            Console.WriteLine("\n--- [FINAL RESULT] ---");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(string.IsNullOrWhiteSpace(completedTask.FinalResult) ? "(No final result text returned)" : completedTask.FinalResult);
            Console.ResetColor();
            Console.WriteLine("----------------------\n");

            // ── Step 3: Fetch Task Execution Audit & Events ────────────────────
            PrintSection("3. Execution Audit & Event Timeline");

            var events = await client.GetTaskEventsAsync(createdTask.TaskId);
            Console.WriteLine($"Retrieved {events.Count} execution events:");

            var toolCalls = events.Where(e => e.EventType == "ToolExecuted" || e.Level == "Tool").ToList();
            Console.WriteLine($"• Total Events Logged: {events.Count}");
            Console.WriteLine($"• Tool Invocations:     {toolCalls.Count}");

            foreach (var evt in events.Take(10))
            {
                Console.WriteLine($"  [{evt.Timestamp:HH:mm:ss}] [{evt.Level,-7}] {evt.EventType}: {Truncate(evt.Payload, 70)}");
            }
            if (events.Count > 10)
            {
                Console.WriteLine($"  ... and {events.Count - 10} more events.");
            }

            // ── Step 4: Workspace Files Inspection & Download ──────────────────
            PrintSection("4. Workspace Artifacts");

            var files = await client.ListWorkspaceFilesAsync(createdTask.TaskId);
            Console.WriteLine($"Workspace contains {files.Count} file(s):");
            foreach (var file in files)
            {
                Console.WriteLine($"  📄 {file.Name} ({file.SizeBytes} bytes, modified {file.LastModified:HH:mm:ss})");
            }

            var reportFile = files.FirstOrDefault(f => f.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !f.Name.StartsWith("_"));
            if (reportFile != null)
            {
                Console.WriteLine($"\nReading generated report: {reportFile.Name}...");
                var reportContent = await client.GetWorkspaceTextFileAsync(createdTask.TaskId, reportFile.Name);
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine(Truncate(reportContent, 400));
                Console.ResetColor();
            }

            // ── Step 5: Task Continuation (Proceed with Follow-up) ─────────────
            PrintSection("5. Session Continuation (Proceed)");

            var followUpPrompt = "Add radix sort and timsort to the comparison summary.";
            Console.WriteLine($"Sending follow-up prompt: \"{followUpPrompt}\"");

            var continuedTask = await client.ProceedTaskAsync(createdTask.TaskId, followUpPrompt);
            if (continuedTask != null)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Task continuation enqueued successfully!");
                Console.ResetColor();

                Console.WriteLine("Awaiting continuation completion...");
                lastState = TaskState.Draft;
                var finalSession = await client.AwaitTaskCompletionAsync(
                    createdTask.TaskId,
                    timeout: TimeSpan.FromMinutes(3),
                    pollInterval: TimeSpan.FromSeconds(2),
                    onProgress: session =>
                    {
                        if (session.State != lastState)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Status changed -> {session.State}");
                            lastState = session.State;
                        }
                    }
                );

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n✓ Continuation finished with state: {finalSession.State}");
                Console.ResetColor();
                Console.WriteLine("\n--- [UPDATED FINAL RESULT] ---");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(Truncate(finalSession.FinalResult ?? "", 500));
                Console.ResetColor();
                Console.WriteLine("------------------------------\n");
            }

            PrintHeader("Sample Completed Successfully");
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

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength] + "...";
    }
}
