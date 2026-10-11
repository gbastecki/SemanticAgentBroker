using System.Text;
using System.Text.Json;
using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.Plugins.MockData;

/// <summary>
/// Built-in example plugin providing sample datasets that agents can query and analyze.
/// Demonstrates the plugin SDK by generating realistic mock data for testing.
/// </summary>
public class SampleDataAgentPlugin : PluginBase
{
    public override string Id => "SampleData";
    public override string DisplayName => "Sample Data";
    public override string Description => "Provides sample/mock datasets (sales, server logs, users) for testing and demonstration. Use these to verify agent behavior with realistic data.";
    public override string Version => "1.0.0";
    public override string Author => "SemanticAgentBroker";
    public override string Icon => "database";
    public override IReadOnlyList<string> Tags => ["data", "mock", "analysis", "demo"];

    public override IReadOnlyList<PluginTaskDef> GetTasks() =>
    [
        new PluginTaskDef
        {
            Id = "SalesAnalysis",
            Name = "Sales Data Analysis",
            Description = "Fetch the mock sales dataset and perform comprehensive analysis including revenue calculation, region performance, and KPIs. Present the results clearly.",
            AllowedMethods = ["GetSalesData"]
        },
        new PluginTaskDef
        {
            Id = "ServerLogDiag",
            Name = "Server Log Diagnostic",
            Description = "Retrieve the latest server logs, identify critical errors or warnings, and summarize the health status of the application.",
            AllowedMethods = ["GetServerLogs"]
        }
    ];

    public override IReadOnlyList<IPluginMethod> GetMethods() =>
    [
        new GetSalesDataMethod(),
        new GetServerLogsMethod(),
        new GetUserListMethod(),
        new GetProductCatalogMethod()
    ];
}

// ===========================================================================
// ── Sales Data ─────────────────────────────────────────────────────────────
// ===========================================================================

/// <summary>
/// Returns a CSV dataset of 200 sales records with dates, products, quantities, and revenue.
/// </summary>
public class GetSalesDataMethod : PluginMethodBase
{
    public override string Id => "GetSalesData";
    public override string DisplayName => "Get Sales Data";
    public override string Description => "Returns a CSV dataset of 200 sales records (~15KB) with Date, Product, Category, Quantity, UnitPrice, Revenue, and Region columns. WARNING: Large output - it will be auto-saved to a workspace file. Use Python to read the file and perform analysis.";
    public override string ReturnDescription => "CSV-formatted sales data with 200 rows. Auto-saved to workspace file when called.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "format", Description = "Output format: 'csv' (default) or 'json'.", ParameterType = typeof(string), IsRequired = false, DefaultValue = "csv" }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var format = GetOptionalString(arguments, "format", "csv").ToLowerInvariant();
        var rng = new Random(42); // Deterministic for reproducibility

        string[] products = ["Widget A", "Widget B", "Gadget X", "Gadget Y", "Component Z", "Module M", "Sensor S", "Adapter P"];
        string[] categories = ["Electronics", "Electronics", "Hardware", "Hardware", "Components", "Components", "Sensors", "Accessories"];
        string[] regions = ["North", "South", "East", "West", "Central"];

        var records = new List<Dictionary<string, object>>();
        var startDate = new DateTime(2025, 1, 1);

        for (int i = 0; i < 200; i++)
        {
            var productIdx = rng.Next(products.Length);
            var qty = rng.Next(1, 50);
            var unitPrice = Math.Round(10 + rng.NextDouble() * 190, 2);
            var revenue = Math.Round(qty * unitPrice, 2);

            records.Add(new Dictionary<string, object>
            {
                ["Date"] = startDate.AddDays(rng.Next(0, 365)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["Product"] = products[productIdx],
                ["Category"] = categories[productIdx],
                ["Quantity"] = qty,
                ["UnitPrice"] = unitPrice,
                ["Revenue"] = revenue,
                ["Region"] = regions[rng.Next(regions.Length)]
            });
        }

        if (format == "json")
        {
            var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
            return await SaveLargeResultAsync(json, "GetSalesData", context, 2000, "json");
        }

        var sb = new StringBuilder();
        sb.AppendLine("Date,Product,Category,Quantity,UnitPrice,Revenue,Region");
        foreach (var r in records)
        {
            var unitPriceStr = Convert.ToDouble(r["UnitPrice"]).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var revenueStr = Convert.ToDouble(r["Revenue"]).ToString(System.Globalization.CultureInfo.InvariantCulture);
            sb.AppendLine($"{r["Date"]},{r["Product"]},{r["Category"]},{r["Quantity"]},{unitPriceStr},{revenueStr},{r["Region"]}");
        }

        return await SaveLargeResultAsync(sb.ToString(), "GetSalesData", context, 2000, "csv");
    }
}

// ===========================================================================
// ── Server Logs ────────────────────────────────────────────────────────────
// ===========================================================================

/// <summary>
/// Returns simulated server log entries with errors, warnings, and info messages.
/// </summary>
public class GetServerLogsMethod : PluginMethodBase
{
    public override string Id => "GetServerLogs";
    public override string DisplayName => "Get Server Logs";
    public override string Description => "Returns 100 simulated server log entries (~8KB) in standard log format. Contains a mix of ERROR, WARN, and INFO messages from various services. Large output - auto-saved to workspace file. Use Python to parse and analyze.";
    public override string ReturnDescription => "Multi-line server log output. Auto-saved to workspace file when called.";

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var rng = new Random(123);
        var sb = new StringBuilder();
        var baseTime = new DateTime(2025, 6, 15, 8, 0, 0);

        string[] services = ["api-gateway", "auth-service", "db-connector", "cache-layer", "worker-queue", "web-server"];

        var templates = new (string Level, string[] Messages)[]
        {
            ("ERROR", [
                "Connection refused to database at 10.0.1.15:5432 - timeout after 30s",
                "OutOfMemoryException in request handler - heap size exceeded 2GB limit",
                "SSL certificate validation failed for upstream service auth.internal:443",
                "Deadlock detected in transaction pool - rolled back 3 pending transactions",
                "Disk I/O error: write failed on /data/logs/access.log - device full",
                "Unhandled NullReferenceException at UserService.GetProfile(id=null)",
                "Rate limit exceeded for client 192.168.1.50 - 429 Too Many Requests"
            ]),
            ("WARN", [
                "Response time > 5000ms for GET /api/users - possible performance degradation",
                "Connection pool utilization at 85% - consider scaling db-connector replicas",
                "Retry attempt 3/5 for message publish to queue 'order-events'",
                "Cache miss rate increased to 40% - cache warming recommended",
                "Deprecated API endpoint /v1/legacy/users called by client app-v2.1"
            ]),
            ("INFO", [
                "Health check passed - all 6 services responding",
                "Processed 1,247 requests in the last minute - avg latency 45ms",
                "Auto-scaled worker-queue from 2 to 4 replicas",
                "Daily backup completed - 2.3GB compressed to /backups/2025-06-15.tar.gz",
                "New deployment v3.4.1 rolled out to production cluster"
            ])
        };

        for (int i = 0; i < 100; i++)
        {
            var time = baseTime.AddSeconds(rng.Next(0, 28800)); // 8-hour window
            var service = services[rng.Next(services.Length)];

            // Weight: 30% ERROR, 25% WARN, 45% INFO
            var roll = rng.Next(100);
            var (level, messages) = roll < 30 ? templates[0] : roll < 55 ? templates[1] : templates[2];
            var message = messages[rng.Next(messages.Length)];

            var timeStr = time.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            sb.AppendLine($"[{timeStr}] [{level,-5}] [{service}] {message}");
        }

        return await SaveLargeResultAsync(sb.ToString(), "GetServerLogs", context, 2000, "log");
    }
}

// ===========================================================================
// ── User List ──────────────────────────────────────────────────────────────
// ===========================================================================

/// <summary>
/// Returns a JSON array of mock user profiles.
/// </summary>
public class GetUserListMethod : PluginMethodBase
{
    public override string Id => "GetUserList";
    public override string DisplayName => "Get User List";
    public override string Description => "Returns a JSON array of 25 mock user profiles with Name, Email, Department, Role, JoinDate, and IsActive fields. Use for data transformation and formatting tasks.";
    public override string ReturnDescription => "JSON array of user objects.";

    public override Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        string[] firstNames = ["Alice", "Bob", "Charlie", "Diana", "Eve", "Frank", "Grace", "Hank", "Ivy", "Jack", "Kate", "Leo", "Mia", "Noah", "Olivia", "Pete", "Quinn", "Rosa", "Sam", "Tina", "Uma", "Vince", "Wendy", "Xander", "Yara"];
        string[] lastNames = ["Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez"];
        string[] departments = ["Engineering", "Marketing", "Sales", "HR", "Finance", "Operations", "Support", "Design"];
        string[] roles = ["Junior", "Mid", "Senior", "Lead", "Manager", "Director"];

        var rng = new Random(77);
        var users = new List<object>();

        for (int i = 0; i < 25; i++)
        {
            users.Add(new
            {
                Id = i + 1,
                Name = $"{firstNames[i]} {lastNames[rng.Next(lastNames.Length)]}",
                Email = $"{firstNames[i].ToLowerInvariant()}.{lastNames[rng.Next(lastNames.Length)].ToLowerInvariant()}@example.com",
                Department = departments[rng.Next(departments.Length)],
                Role = roles[rng.Next(roles.Length)],
                JoinDate = new DateTime(2020 + rng.Next(0, 5), rng.Next(1, 13), rng.Next(1, 28)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                IsActive = rng.Next(100) > 15
            });
        }

        return Task.FromResult(JsonSerializer.Serialize(users, new JsonSerializerOptions { WriteIndented = true }));
    }
}

// ===========================================================================
// ── Product Catalog ────────────────────────────────────────────────────────
// ===========================================================================

/// <summary>
/// Returns a JSON product catalog with nested categories.
/// </summary>
public class GetProductCatalogMethod : PluginMethodBase
{
    public override string Id => "GetProductCatalog";
    public override string DisplayName => "Get Product Catalog";
    public override string Description => "Returns a JSON product catalog with 15 products across 4 categories, including Name, SKU, Price, Stock, and Rating fields. Useful for testing data aggregation and reporting.";
    public override string ReturnDescription => "JSON product catalog with nested category structure.";

    public override Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var catalog = new
        {
            GeneratedAt = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            Categories = new[]
            {
                new
                {
                    Name = "Electronics",
                    Products = new[]
                    {
                        new { Name = "Wireless Headphones Pro", SKU = "ELEC-001", Price = 149.99, Stock = 234, Rating = 4.5 },
                        new { Name = "USB-C Hub 7-in-1", SKU = "ELEC-002", Price = 59.99, Stock = 567, Rating = 4.2 },
                        new { Name = "Mechanical Keyboard", SKU = "ELEC-003", Price = 129.99, Stock = 189, Rating = 4.7 },
                        new { Name = "4K Webcam", SKU = "ELEC-004", Price = 89.99, Stock = 312, Rating = 4.0 }
                    }
                },
                new
                {
                    Name = "Components",
                    Products = new[]
                    {
                        new { Name = "Temperature Sensor Module", SKU = "COMP-001", Price = 12.50, Stock = 1500, Rating = 4.3 },
                        new { Name = "Servo Motor 180°", SKU = "COMP-002", Price = 8.99, Stock = 2300, Rating = 4.1 },
                        new { Name = "OLED Display 1.3\"", SKU = "COMP-003", Price = 15.99, Stock = 890, Rating = 4.6 },
                        new { Name = "ESP32 DevKit v4", SKU = "COMP-004", Price = 9.99, Stock = 3400, Rating = 4.8 }
                    }
                },
                new
                {
                    Name = "Accessories",
                    Products = new[]
                    {
                        new { Name = "Braided USB-C Cable 2m", SKU = "ACC-001", Price = 14.99, Stock = 4500, Rating = 4.4 },
                        new { Name = "Laptop Stand Aluminum", SKU = "ACC-002", Price = 45.99, Stock = 678, Rating = 4.6 },
                        new { Name = "Monitor Arm Dual", SKU = "ACC-003", Price = 79.99, Stock = 234, Rating = 4.3 },
                        new { Name = "Desk Mat XL", SKU = "ACC-004", Price = 29.99, Stock = 890, Rating = 4.5 }
                    }
                },
                new
                {
                    Name = "Software",
                    Products = new[]
                    {
                        new { Name = "IDE Pro License (Annual)", SKU = "SOFT-001", Price = 199.99, Stock = 999, Rating = 4.7 },
                        new { Name = "Cloud Storage 1TB", SKU = "SOFT-002", Price = 9.99, Stock = 999, Rating = 4.2 },
                        new { Name = "VPN Service (Monthly)", SKU = "SOFT-003", Price = 4.99, Stock = 999, Rating = 3.9 }
                    }
                }
            }
        };

        return Task.FromResult(JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));
    }
}
