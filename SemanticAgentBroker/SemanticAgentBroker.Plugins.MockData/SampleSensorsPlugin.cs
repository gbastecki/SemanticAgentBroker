using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.Plugins.MockData;

/// <summary>
/// Mock data plugin providing building sensor data (temperature &amp; humidity) for testing.
/// Demonstrates multi-step agent workflows: fuzzy search → date resolution → data retrieval → Python analysis.
/// </summary>
public class SampleSensorsPlugin : PluginBase
{
    public override string Id => "SampleSensors";
    public override string DisplayName => "Sample Sensors";
    public override string Description => "Provides mock building sensor data (temperature & humidity) for testing. Query sensors by room, retrieve readings, and analyze with Python.";
    public override string Version => "1.0.0";
    public override string Author => "SemanticAgentBroker";
    public override string Icon => "thermostat";
    public override IReadOnlyList<string> Tags => ["sensors", "temperature", "humidity", "mock", "demo"];

    public override IReadOnlyList<PluginTaskDef> GetTasks() =>
    [
        new PluginTaskDef
        {
            Id = "SensorAnalysis",
            Name = "Sensor Data Analysis",
            Description = "Analyze building sensor data. Users can ask about temperature or humidity readings " +
                          "for specific rooms. The agent should search for the correct sensor (handling typos " +
                          "and alternate names), retrieve readings for the requested time period, and use Python " +
                          "to calculate statistics and generate a report.",
            AllowedMethods = ["GetSensors", "SearchSensors", "GetReadings"],
            SystemInstruction = """
                You are a Building Sensor Analysis Agent.
                You analyze temperature and humidity sensor data from building sensors.
                
                Workflow:
                1. First, use SystemInfo-GetCurrentDateTime to get the current date/time for reference
                2. Use SampleSensors-SearchSensors to find the correct sensor based on the user's description (they may use room numbers, partial names, or make typos)
                3. Use SampleSensors-GetReadings with appropriate date range to retrieve the data. If you have many sensors, save the list of IDs to a workspace file and pass the absolute file path to GetReadings to fetch them all at once.
                4. Save the data to workspace using FileIO-SaveToWorkspace
                5. Use PythonSandbox-ExecutePythonScript to analyze the data (first inspect columns/head if needed, calculate averages, find min/max, detect anomalies, generate report). Use tabulate or df.to_markdown() for tables.
                
                Always handle ambiguity gracefully - if multiple sensors match, list them and pick the most likely one.
                IMPORTANT: Always check the available fetched sensor types. If the user asks for "dampness" but the sensors are labeled "Humidity" (or vice-versa), treat them as synonymous and analyze the available data.
                Present results clearly with units (°C for temperature, % for humidity).
                """
        }
    ];

    public override IReadOnlyList<IPluginMethod> GetMethods() =>
    [
        new GetSensorsMethod(),
        new SearchSensorsMethod(),
        new GetReadingsMethod()
    ];
}

// ===========================================================================
// ── Sensor Data Model ──────────────────────────────────────────────────────
// ===========================================================================

internal static class SensorDatabase
{
    private static readonly string[] RoomTypes =
    [
        "Office", "Conference Room", "Lab", "Kitchen", "Server Room",
        "Storage", "Lobby", "Break Room", "Meeting Room", "Open Office"
    ];

    internal static readonly List<SensorInfo> Sensors = GenerateSensorCatalog();

    private static List<SensorInfo> GenerateSensorCatalog()
    {
        var sensors = new List<SensorInfo>();
        // 5 buildings × 10 floors × 10 rooms = 500 rooms
        // Each room gets 1 Temperature + 1 Humidity sensor = 1000 sensors
        for (int bld = 1; bld <= 5; bld++)
        {
            var building = $"Building {(char)('A' + bld - 1)}";
            for (int floor = 1; floor <= 10; floor++)
            {
                for (int room = 1; room <= 10; room++)
                {
                    int roomNum = floor * 100 + room;
                    var roomName = RoomTypes[(bld + floor + room) % RoomTypes.Length] + $" {roomNum}";
                    var roomNumStr = roomNum.ToString();

                    sensors.Add(new($"TEMP-{bld}{roomNum}", $"Temp Sensor {roomName}",
                        roomNumStr, roomName, floor, building, "Temperature"));
                    sensors.Add(new($"HUM-{bld}{roomNum}", $"Humidity Sensor {roomName}",
                        roomNumStr, roomName, floor, building, "Humidity"));
                }
            }
        }
        return sensors;
    }    private static int GetDeterministicHashCode(string str)
    {
        unchecked
        {
            int hash1 = (5381 << 16) + 5381;
            int hash2 = hash1;

            for (int i = 0; i < str.Length; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ str[i];
                if (i == str.Length - 1)
                    break;
                hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
            }

            return hash1 + (hash2 * 1566083941);
        }
    }

    /// <summary>
    /// Generates deterministic sensor readings for a given sensor and time window.
    /// Uses a seeded random generator based on sensorId hash so the same query always returns the same data.
    /// </summary>
    internal static List<SensorReading> GenerateReadings(string sensorId, DateTimeOffset from, DateTimeOffset to)
    {
        var sensor = Sensors.FirstOrDefault(s => s.Id.Equals(sensorId, StringComparison.OrdinalIgnoreCase));
        if (sensor == null) return [];

        var readings = new List<SensorReading>();
        var seed = GetDeterministicHashCode(sensorId);
        var rng = new Random(seed);

        // Determine base value and range based on sensor type and room
        double baseValue, amplitude;
        if (sensor.SensorType == "Temperature")
        {
            // Server Room runs hotter, Parking Garage cooler
            baseValue = sensor.RoomName switch
            {
                var s when s.StartsWith("Server Room", StringComparison.OrdinalIgnoreCase) => 24.0,
                var s when s.StartsWith("Parking Garage", StringComparison.OrdinalIgnoreCase) => 15.0,
                _ => 21.5
            };
            amplitude = sensor.RoomName switch
            {
                var s when s.StartsWith("Server Room", StringComparison.OrdinalIgnoreCase) => 3.0,  // 21-27°C
                var s when s.StartsWith("Parking Garage", StringComparison.OrdinalIgnoreCase) => 5.0,  // 10-20°C
                _ => 3.5  // 18-25°C
            };
        }
        else // Humidity
        {
            baseValue = sensor.RoomName switch
            {
                var s when s.StartsWith("Server Room", StringComparison.OrdinalIgnoreCase) => 35.0,  // Low humidity, controlled
                var s when s.StartsWith("Basement Storage", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Storage", StringComparison.OrdinalIgnoreCase) => 65.0,  // High humidity
                _ => 48.0
            };
            amplitude = sensor.RoomName switch
            {
                var s when s.StartsWith("Server Room", StringComparison.OrdinalIgnoreCase) => 5.0,  // 30-40%
                var s when s.StartsWith("Basement Storage", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Storage", StringComparison.OrdinalIgnoreCase) => 10.0,  // 55-75%
                _ => 12.0  // 36-60%
            };
        }

        // Generate one reading per 15 minutes
        var interval = TimeSpan.FromMinutes(15);
        var current = from;

        while (current <= to)
        {
            // Deterministic seed per timestamp
            var timeSeed = seed ^ current.UtcTicks.GetHashCode();
            var timeRng = new Random(timeSeed);

            // Add daily cycle variation (warmer during day for temp, drier during day for humidity)
            var hourOfDay = current.Hour;
            var dailyCycle = Math.Sin((hourOfDay - 6.0) / 24.0 * 2.0 * Math.PI) * (amplitude * 0.3);

            var noise = (timeRng.NextDouble() - 0.5) * amplitude;
            var value = baseValue + dailyCycle + noise;

            // Clamp to reasonable bounds
            if (sensor.SensorType == "Temperature")
                value = Math.Clamp(value, 5.0, 40.0);
            else
                value = Math.Clamp(value, 15.0, 95.0);

            readings.Add(new SensorReading(current, Math.Round(value, 2)));
            current = current.Add(interval);
        }

        return readings;
    }
}

internal record SensorInfo(
    string Id,
    string Name,
    string RoomNumber,
    string RoomName,
    int Floor,
    string Building,
    string SensorType);

internal record SensorReading(DateTimeOffset Timestamp, double Value);

// ===========================================================================
// ── Methods ────────────────────────────────────────────────────────────────
// ===========================================================================

public class GetSensorsMethod : PluginMethodBase
{
    public override string Id => "GetSensors";
    public override string DisplayName => "Get Sensors";
    public override string Description => "Returns a JSON list of all available building sensors. Optionally filter by sensor type.";
    public override string ReturnDescription => "JSON array of sensor objects with Id, Name, RoomNumber, RoomName, Floor, Building, SensorType.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new()
        {
            Name = "sensorType",
            Description = "Optional filter: 'Temperature' or 'Humidity'. Leave empty for all sensors.",
            ParameterType = typeof(string),
            IsRequired = false
        }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var sensorType = GetOptionalString(arguments, "sensorType");

        var sensors = SensorDatabase.Sensors.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(sensorType))
        {
            sensors = sensors.Where(s => s.SensorType.Equals(sensorType.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var result = sensors.Select(s => new
        {
            s.Id,
            s.Name,
            s.RoomNumber,
            s.RoomName,
            s.Floor,
            s.Building,
            s.SensorType
        });

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        return await SaveLargeResultAsync(json, "GetSensors", context, 2000, "json");
    }
}

public class SearchSensorsMethod : PluginMethodBase
{
    public override string Id => "SearchSensors";
    public override string DisplayName => "Search Sensors";
    public override string Description => "Searches for sensors by name, room number, or room name using case-insensitive substring matching. Handles partial names and common misspellings.";
    public override string ReturnDescription => "JSON array of matching sensor objects.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new()
        {
            Name = "query",
            Description = "Search term - can be a room name (e.g. 'conference'), room number (e.g. '232'), sensor name, or partial match.",
            ParameterType = typeof(string),
            IsRequired = true
        }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var query = GetRequiredString(arguments, "query").Trim();

        var matches = SensorDatabase.Sensors
            .Where(s =>
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.RoomNumber.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.RoomName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Building.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.RoomNumber,
                s.RoomName,
                s.Floor,
                s.Building,
                s.SensorType
            })
            .ToList();

        if (matches.Count == 0)
            return $"No sensors found matching '{query}'. Try using a partial room name, room number, or sensor type.";

        var json = JsonSerializer.Serialize(matches, new JsonSerializerOptions { WriteIndented = true });
        return await SaveLargeResultAsync(json, "SearchSensors", context, 2000, "json");
    }
}

public class GetReadingsMethod : PluginMethodBase
{
    public override string Id => "GetReadings";
    public override string DisplayName => "Get Readings";
    public override string Description => "Returns sensor readings for a specific sensor within a date/time range. Data is returned as CSV (Timestamp,Value) for easy processing with Python.";
    public override string ReturnDescription => "CSV data with headers: Timestamp,Value. Timestamps are in ISO 8601 UTC format. Values are in °C for temperature, % for humidity.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new()
        {
            Name = "sensorIdsOrFilePath",
            Description = "The sensor ID(s) to retrieve readings for. You can pass a comma-separated list of IDs. If you have too many IDs (saved in a workspace file), you MUST pass the absolute file path to that file (e.g. '/app/workdir/tool_output_...txt') instead of trying to read and print them all. DO NOT fetch sensors one-by-one.",
            ParameterType = typeof(string),
            IsRequired = true
        },
        new()
        {
            Name = "fromUtc",
            Description = "Start of the time range in ISO 8601 format (e.g. '2026-04-24T00:00:00Z').",
            ParameterType = typeof(string),
            IsRequired = true
        },
        new()
        {
            Name = "toUtc",
            Description = "End of the time range in ISO 8601 format (e.g. '2026-04-25T00:00:00Z').",
            ParameterType = typeof(string),
            IsRequired = true
        }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var sensorIdsOrFilePath = GetOptionalString(arguments, "sensorIdsOrFilePath");
        var fromStr = GetOptionalString(arguments, "fromUtc");
        var toStr = GetOptionalString(arguments, "toUtc");

        if (string.IsNullOrWhiteSpace(sensorIdsOrFilePath))
            return "Error: Required parameter 'sensorIdsOrFilePath' is missing or empty.";
        if (string.IsNullOrWhiteSpace(fromStr))
            return "Error: Required parameter 'fromUtc' is missing or empty.";
        if (string.IsNullOrWhiteSpace(toStr))
            return "Error: Required parameter 'toUtc' is missing or empty.";

        if (!DateTimeOffset.TryParse(fromStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var from))
            return $"Error: Invalid fromUtc format '{fromStr}'. Use ISO 8601 format (e.g. '2026-04-24T00:00:00Z').";

        if (!DateTimeOffset.TryParse(toStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var to))
            return $"Error: Invalid toUtc format '{toStr}'. Use ISO 8601 format (e.g. '2026-04-25T00:00:00Z').";

        if (to <= from)
            return "Error: 'toUtc' must be after 'fromUtc'.";

        // Limit to 7 days max to prevent massive outputs
        if ((to - from).TotalDays > 7)
            return "Error: Maximum range is 7 days. Please narrow your time window.";

        List<string> sensorIds;
        string? candidateFilePath = null;
        if (!string.IsNullOrEmpty(context.WorkspacePath))
        {
            var wsFullPath = Path.GetFullPath(context.WorkspacePath);
            var trimmed = sensorIdsOrFilePath.Trim().Replace('\\', '/');

            var fullRelativePath = Path.GetFullPath(Path.Combine(wsFullPath, trimmed.TrimStart('/')));
            if (fullRelativePath.StartsWith(wsFullPath, StringComparison.OrdinalIgnoreCase) && File.Exists(fullRelativePath))
            {
                candidateFilePath = fullRelativePath;
            }
            else
            {
                var fileNameOnly = Path.GetFileName(trimmed);
                if (!string.IsNullOrEmpty(fileNameOnly))
                {
                    var rootCandidate = Path.GetFullPath(Path.Combine(wsFullPath, fileNameOnly));
                    if (rootCandidate.StartsWith(wsFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        candidateFilePath = rootCandidate;
                    }
                }
            }
        }

        if (sensorIdsOrFilePath.Contains('/') || 
            sensorIdsOrFilePath.Contains('\\') || 
            sensorIdsOrFilePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            sensorIdsOrFilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            sensorIdsOrFilePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
            (candidateFilePath != null && File.Exists(candidateFilePath)))
        {
            if (candidateFilePath != null && File.Exists(candidateFilePath))
            {
                var fileContent = await File.ReadAllTextAsync(candidateFilePath, ct);
                sensorIds = ExtractSensorIds(fileContent);
            }
            else
            {
                return $"Error: File '{sensorIdsOrFilePath}' not found in workspace.";
            }
        }
        else
        {
            sensorIds = ExtractSensorIds(sensorIdsOrFilePath);
        }

        if (sensorIds.Count == 0)
        {
            return "Error: No valid sensor IDs found. Make sure IDs are formatted as TEMP-1234 or HUM-1234.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("SensorId,Timestamp,Value");

        foreach (var id in sensorIds)
        {
            var sensor = SensorDatabase.Sensors.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (sensor == null)
            {
                sb.AppendLine($"# Error: Sensor '{id}' not found.");
                continue;
            }

            var readings = SensorDatabase.GenerateReadings(id, from, to);
            foreach (var r in readings)
            {
                sb.AppendLine(System.FormattableString.Invariant($"{id},{r.Timestamp:o},{r.Value}"));
            }
        }

        var csv = sb.ToString().TrimEnd();
        return await SaveLargeResultAsync(csv, "GetReadings", context, 2000, "csv");
    }

    private static List<string> ExtractSensorIds(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        var regex = new Regex(@"(TEMP|HUM|DAMP)-\d+", RegexOptions.IgnoreCase);
        return regex.Matches(input)
                    .Select(m => m.Value.ToUpperInvariant())
                    .Distinct()
                    .ToList();
    }
}
