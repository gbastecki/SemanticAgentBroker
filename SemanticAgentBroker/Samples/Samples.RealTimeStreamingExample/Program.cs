using Microsoft.AspNetCore.SignalR.Client;
using Samples.Common.Client;
using SemanticAgentBroker.Shared.Domain;

namespace Samples.RealTimeStreamingExample;

/// <summary>
/// Sample demonstrating real-time SignalR WebSocket streaming with Semantic Agent Broker:
/// live agent thought streaming, tool execution telemetry, workspace file notifications,
/// plan review approval (Human-in-the-Loop), and mid-flight user interruptions.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        PrintHeader("Semantic Agent Broker - Real-Time SignalR Streaming Sample");

        var baseUrl = args.Length > 0 ? args[0] : "https://localhost:5100";
        var username = args.Length > 1 ? args[1] : "admin";
        var password = args.Length > 2 ? args[2] : "admin";

        Console.WriteLine($"Broker Endpoint: {baseUrl}");
        Console.WriteLine($"Connecting as:   {username}\n");

        using var client = new SemanticAgentBrokerClient(
            baseUrl: baseUrl,
            username: username,
            password: password,
            browserId: "signalr-streaming-sample-client"
        );

        try
        {
            // ── Step 1: Establish SignalR Hub Connection ───────────────────────
            PrintSection("1. Connecting to SignalR /loghub");

            var hubConnection = client.CreateSignalRConnection();

            var taskCompletedTcs = new TaskCompletionSource<TaskState>();
            Guid currentTaskId = Guid.Empty;
            bool interruptionSent = false;

            // Register Event Handlers
            hubConnection.On<TaskStateChangedEvent>(HubMethodNames.TaskStateChanged, evt =>
            {
                if (evt.TaskId != currentTaskId) return;

                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"\n[STATE CHANGED] -> {evt.NewState} {(string.IsNullOrEmpty(evt.Detail) ? "" : $"({evt.Detail})")}");
                Console.ResetColor();

                if (evt.NewState is TaskState.Completed or TaskState.Failed or TaskState.Cancelled)
                {
                    taskCompletedTcs.TrySetResult(evt.NewState);
                }
            });

            hubConnection.On<AgentThoughtEvent>(HubMethodNames.AgentThought, evt =>
            {
                if (evt.TaskId != currentTaskId) return;

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n[THOUGHT] ({evt.AgentName}):\n{evt.Thought.Trim()}");
                Console.ResetColor();

                // Demonstrate Mid-Flight Interruption after observing the first thought
                if (!interruptionSent)
                {
                    interruptionSent = true;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("\n[USER DIRECTIVE] Sending mid-flight interruption: 'Please ensure numbers are formatted with comma separators.'");
                        Console.ResetColor();

                        await hubConnection.InvokeAsync(HubMethodNames.SendInterruption, evt.TaskId, "Please ensure numbers are formatted with comma separators.");
                    });
                }
            });

            hubConnection.On<ToolExecutionEvent>(HubMethodNames.ToolExecution, evt =>
            {
                if (evt.TaskId != currentTaskId) return;

                if (evt.IsStarting)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"\n[TOOL CALLING] {evt.ToolName} (Agent: {evt.AgentName})");
                    Console.WriteLine($"  Args: {Truncate(evt.Args, 120)}");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[TOOL RESULT]  {evt.ToolName}");
                    Console.WriteLine($"  Output: {Truncate(evt.Result ?? "", 140)}");
                    Console.ResetColor();
                }
            });

            hubConnection.On<WorkspaceFileChangedEvent>(HubMethodNames.WorkspaceFileChanged, evt =>
            {
                if (evt.TaskId != currentTaskId) return;

                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"\n[FILE EVENT] Workspace file '{evt.FileName}' was {evt.ChangeType}.");
                Console.ResetColor();
            });

            hubConnection.On<PlanReviewRequestedEvent>(HubMethodNames.PlanReviewRequested, async evt =>
            {
                if (evt.TaskId != currentTaskId) return;

                Console.ForegroundColor = ConsoleColor.DarkMagenta;
                Console.WriteLine($"\n[PLAN REVIEW REQUESTED] Version {evt.Version}:");
                Console.WriteLine(new string('-', 60));
                Console.WriteLine(evt.PlanMarkdown.Trim());
                Console.WriteLine(new string('-', 60));
                Console.ResetColor();

                Console.WriteLine("Simulating Human-in-the-Loop review: Auto-approving implementation plan in 2 seconds...");
                await Task.Delay(2000);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Approving plan via hub method...");
                Console.ResetColor();
                await hubConnection.InvokeAsync(HubMethodNames.ApprovePlan, evt.TaskId);
            });

            Console.WriteLine("Starting WebSocket connection...");
            await hubConnection.StartAsync();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Connected to {baseUrl}/loghub (Connection ID: {hubConnection.ConnectionId})\n");
            Console.ResetColor();

            // ── Step 2: Create a Task in Planned Mode ──────────────────────────
            PrintSection("2. Creating Task in Planned Execution Mode");

            var taskRequest = new AgentTaskRequest
            {
                AgentName = "GeneralAssistant",
                Prompt = "Calculate the total sales per product category using Python, and generate a markdown table in 'summary.md'.",
                Mode = ExecutionMode.Planned, // Generates plan and awaits user review
                MaxSteps = 5,
                DebugMode = true
            };

            var createdTask = await client.CreateTaskAsync(taskRequest);
            currentTaskId = createdTask.TaskId;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Task enqueued! Task ID: {currentTaskId}");
            Console.ResetColor();

            // ── Step 3: Subscribe to Task Stream ───────────────────────────────
            PrintSection("3. Subscribing to Live Task Telemetry");

            Console.WriteLine($"Invoking SubscribeToTask({currentTaskId})...");
            await hubConnection.InvokeAsync(HubMethodNames.SubscribeToTask, currentTaskId);
            Console.WriteLine("Subscribed! Now streaming live thoughts, tools, and reviews...\n");

            // Wait for completion via the SignalR stream
            var finalState = await taskCompletedTcs.Task.WaitAsync(TimeSpan.FromMinutes(4));
            Console.WriteLine($"\nTask finished with terminal state: {finalState}");

            // Fetch and print final result from REST API
            var details = await client.GetTaskAsync(currentTaskId);
            Console.WriteLine("\n--- [FINAL RESULT] ---");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(details?.FinalResult ?? "(No final result)");
            Console.ResetColor();
            Console.WriteLine("----------------------\n");

            await hubConnection.InvokeAsync(HubMethodNames.UnsubscribeFromTask, currentTaskId);
            await hubConnection.StopAsync();

            PrintHeader("SignalR Streaming Sample Completed Successfully");
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
