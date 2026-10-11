using System.Text;
using Samples.Common.Client;
using SemanticAgentBroker.Shared.Domain;

namespace Samples.FileIngestionExample;

/// <summary>
/// Sample demonstrating file processing pipelines with Semantic Agent Broker:
/// atomic initial file staging via multipart/form-data on task creation, mid-flight workspace uploads,
/// execution against staged datasets, and downloading resulting reports/artifacts.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        PrintHeader("Semantic Agent Broker - File Ingestion & Workspace Sample");

        var baseUrl = args.Length > 0 ? args[0] : "https://localhost:5100";
        var username = args.Length > 1 ? args[1] : "admin";
        var password = args.Length > 2 ? args[2] : "admin";

        Console.WriteLine($"Broker Endpoint: {baseUrl}");
        Console.WriteLine($"Connecting as:   {username}\n");

        using var client = new SemanticAgentBrokerClient(
            baseUrl: baseUrl,
            username: username,
            password: password,
            browserId: "file-ingestion-sample-client"
        );

        try
        {
            // ── Step 1: Prepare Sample Datasets ────────────────────────────────
            PrintSection("1. Preparing Input Datasets");

            var salesCsv = 
                "Region,Product,UnitsSold,RevenueUSD\n" +
                "North,Widget A,120,2400.00\n" +
                "North,Widget B,80,3200.00\n" +
                "South,Widget A,210,4200.00\n" +
                "South,Widget B,45,1800.00\n" +
                "East,Widget A,300,6000.00\n" +
                "East,Widget B,150,6000.00\n" +
                "West,Widget A,90,1800.00\n" +
                "West,Widget B,110,4400.00\n";

            Console.WriteLine("Prepared 'regional_sales.csv' (8 data rows).");

            // ── Step 2: Atomic Task Creation with Attached File ────────────────
            PrintSection("2. Creating Task with Multipart File Staging");

            var taskRequest = new AgentTaskRequest
            {
                AgentName = "GeneralAssistant",
                Prompt = "Read 'regional_sales.csv' from your workspace. Use Python to calculate total revenue per region, identify the top performing region, and write a summary markdown report to 'regional_analysis.md'.",
                Mode = ExecutionMode.Auto,
                MaxSteps = 6,
                DebugMode = true
            };

            using var csvStream = new MemoryStream(Encoding.UTF8.GetBytes(salesCsv));
            var initialFiles = new (string FileName, Stream Content)[]
            {
                ("regional_sales.csv", csvStream)
            };

            Console.WriteLine("Submitting task with 'regional_sales.csv' staged atomically...");
            var createdTask = await client.CreateTaskAsync(taskRequest, initialFiles);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Task created and file staged into workspace! Task ID: {createdTask.TaskId}");
            Console.ResetColor();

            // ── Step 3: Mid-Flight Workspace Upload ─────────────────────────────
            PrintSection("3. Uploading Supplementary File to Workspace");

            var quarterlyTargets = 
                "Region,TargetUSD\n" +
                "North,5000.00\n" +
                "South,5500.00\n" +
                "East,10000.00\n" +
                "West,6000.00\n";

            using var targetsStream = new MemoryStream(Encoding.UTF8.GetBytes(quarterlyTargets));
            Console.WriteLine("Uploading 'quarterly_targets.csv' to task workspace via POST /api/tasks/{id}/workspace/upload...");

            var uploaded = await client.UploadWorkspaceFileAsync(createdTask.TaskId, "quarterly_targets.csv", targetsStream);
            if (uploaded)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓ Supplementary file uploaded successfully to workspace.");
                Console.ResetColor();
            }

            // ── Step 4: Verify Staged Workspace Files ──────────────────────────
            PrintSection("4. Inspecting Staged Workspace Files");

            var files = await client.ListWorkspaceFilesAsync(createdTask.TaskId);
            Console.WriteLine($"Current workspace contents ({files.Count} files):");
            foreach (var file in files)
            {
                Console.WriteLine($"  📄 {file.Name} ({file.SizeBytes} bytes)");
            }

            // ── Step 5: Await Task Execution ───────────────────────────────────
            PrintSection("5. Awaiting Agent Execution & Analysis");

            var lastState = TaskState.Draft;
            var completedTask = await client.AwaitTaskCompletionAsync(
                createdTask.TaskId,
                timeout: TimeSpan.FromMinutes(3),
                pollInterval: TimeSpan.FromSeconds(2),
                onProgress: s =>
                {
                    if (s.State != lastState)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Status: {s.State}");
                        lastState = s.State;
                    }
                    else
                    {
                        Console.Write(".");
                    }
                }
            );

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Task finished with state: {completedTask.State}");
            Console.ResetColor();

            // ── Step 6: Download & Display Generated Analysis Report ───────────
            PrintSection("6. Downloading Generated Artifacts");

            var finalFiles = await client.ListWorkspaceFilesAsync(createdTask.TaskId);
            Console.WriteLine($"Workspace files after execution ({finalFiles.Count} files):");
            foreach (var file in finalFiles)
            {
                Console.WriteLine($"  📄 {file.Name} ({file.SizeBytes} bytes, modified: {file.LastModified:HH:mm:ss})");
            }

            var reportFile = finalFiles.FirstOrDefault(f => f.Name.Equals("regional_analysis.md", StringComparison.OrdinalIgnoreCase))
                ?? finalFiles.FirstOrDefault(f => f.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !f.Name.StartsWith("_"));

            if (reportFile != null)
            {
                Console.WriteLine($"\nDownloading report: {reportFile.Name}...");
                var reportContent = await client.GetWorkspaceTextFileAsync(createdTask.TaskId, reportFile.Name);

                Console.WriteLine("\n--- [GENERATED REPORT] ---");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(reportContent.Trim());
                Console.ResetColor();
                Console.WriteLine("--------------------------\n");
            }
            else
            {
                Console.WriteLine("No separate markdown report file was found in the workspace.");
                Console.WriteLine("Final Result: " + completedTask.FinalResult);
            }

            PrintHeader("File Ingestion Sample Completed Successfully");
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
