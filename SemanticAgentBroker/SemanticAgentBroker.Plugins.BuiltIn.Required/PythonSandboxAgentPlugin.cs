using System.Text;
using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.Plugins.BuiltIn.Required;

/// <summary>
/// Built-in plugin providing Python script execution in an isolated Docker sandbox.
/// Pre-installed libraries: pandas, numpy, scipy, matplotlib, seaborn and more...
/// </summary>
public class PythonSandboxAgentPlugin : PluginBase
{
    public override string Id => "PythonSandbox";
    public override string DisplayName => "Python Sandbox";
    public override string Description => "Executes Python 3.12 scripts in an isolated, network-disabled Docker sandbox with pandas, numpy, scipy, matplotlib, seaborn, tabulate, openpyxl, scikit-learn, pypdf, python-docx, odfpy, python-pptx, xlrd, bs4, lxml, PyYAML, Pillow, py7zr, and markdown.";
    public override string Version => "1.0.0";
    public override string Author => "SemanticAgentBroker";
    public override string Icon => "code";
    public override IReadOnlyList<string> Tags => ["python", "sandbox", "docker", "data-analysis"];
    public override PluginCategory Category => PluginCategory.BuiltInRequired;

    public override IReadOnlyList<IPluginMethod> GetMethods() =>
    [
        new ExecutePythonScriptMethod()
    ];

    public override IReadOnlyList<PluginConfigEntry> GetConfigurationSchema() =>
    [
        new() { Key = "ImageName", DisplayName = "Docker Image", Description = "Docker image for the Python sandbox.", DefaultValue = "ai-sandbox-python:latest" },
        new() { Key = "TimeoutSeconds", DisplayName = "Timeout (s)", Description = "Max execution time in seconds.", DefaultValue = "60" },
        new() { Key = "MemoryLimitMb", DisplayName = "Memory Limit (MB)", Description = "Container memory limit.", DefaultValue = "512" },
        new() { Key = "MaxOutputMb", DisplayName = "Max Output (MB)", Description = "Maximum output size in megabytes before truncation.", DefaultValue = "50" },
        new() { Key = "DockerUri", DisplayName = "Docker URI", Description = "Docker daemon URI. Leave empty for auto-detect.", DefaultValue = "" },
        new() { Key = "ContainerDataPath", DisplayName = "Container Data Path", Description = "Mount path inside the container.", DefaultValue = "/app/workdir" },
        new() { Key = "CpuQuota", DisplayName = "CPU Quota", Description = "CPU quota in microseconds. Empty = no limit.", DefaultValue = "" },
        new() { Key = "CpuPeriod", DisplayName = "CPU Period", Description = "CPU CFS period in microseconds.", DefaultValue = "100000" },
    ];
    public override async Task InitializeAsync(IPluginContext context)
    {
        await base.InitializeAsync(context);
        try
        {
            var dockerUri = context.Configuration.GetValueOrDefault("DockerUri", "");
            if (string.IsNullOrEmpty(dockerUri))
            {
                var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
                dockerUri = !string.IsNullOrEmpty(dockerHost)
                    ? dockerHost
                    : Environment.OSVersion.Platform == PlatformID.Win32NT
                        ? "npipe://./pipe/docker_engine"
                        : "unix:///var/run/docker.sock";
            }
            using var client = new Docker.DotNet.DockerClientConfiguration(new Uri(dockerUri)).CreateClient();
            await ExecutePythonScriptMethod.CleanupOrphanedContainersAsync(client);
        }
        catch { /* Ignore daemon connection issues during init */ }
    }
}

public class ExecutePythonScriptMethod : PluginMethodBase
{
    public override string Id => "ExecutePythonScript";
    public override string DisplayName => "Execute Python Script";
    public override string Description => "Executes a Python 3.12 script in an isolated, network-disabled sandbox. Pre-installed libraries: pandas, numpy, scipy, matplotlib, seaborn, tabulate, openpyxl, scikit-learn, pypdf, python-docx, odfpy, python-pptx, xlrd, bs4, lxml, PyYAML, Pillow, py7zr, markdown. Use print() to output results.";
    public override string ReturnDescription => "Standard output from the script execution.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "script", Description = "The exact Python 3.12 code to execute. Must use print() to produce output.", ParameterType = typeof(string), IsRequired = true }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var script = GetRequiredString(arguments, "script");

        var imageName = context.Configuration.GetValueOrDefault("ImageName", "ai-sandbox-python:latest");
        var timeoutSeconds = int.TryParse(context.Configuration.GetValueOrDefault("TimeoutSeconds", "60"), out var ts) ? ts : 60;
        var memoryLimitMb = int.TryParse(context.Configuration.GetValueOrDefault("MemoryLimitMb", "512"), out var ml) ? ml : 512;
        var maxOutputMb = int.TryParse(context.Configuration.GetValueOrDefault("MaxOutputMb", "50"), out var mo) ? mo : 50;
        var containerDataPath = context.Configuration.GetValueOrDefault("ContainerDataPath", "/app/workdir");
        var dockerUri = context.Configuration.GetValueOrDefault("DockerUri", "");

        var cpuQuotaStr = context.Configuration.GetValueOrDefault("CpuQuota", "");
        var cpuPeriodStr = context.Configuration.GetValueOrDefault("CpuPeriod", "100000");
        long? cpuQuota = long.TryParse(cpuQuotaStr, out var cq) ? cq : null;
        long cpuPeriod = long.TryParse(cpuPeriodStr, out var cp) ? cp : 100_000;

        // Resolve Docker URI
        if (string.IsNullOrEmpty(dockerUri))
        {
            var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
            dockerUri = !string.IsNullOrEmpty(dockerHost)
                ? dockerHost
                : Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? "npipe://./pipe/docker_engine"
                    : "unix:///var/run/docker.sock";
        }

        using var client = new Docker.DotNet.DockerClientConfiguration(new Uri(dockerUri)).CreateClient();

        var lockManager = context.GetService<IWorkspaceLockManager>();
        using var workspaceLock = lockManager != null && context.SessionId != Guid.Empty
            ? await lockManager.AcquireLockAsync(context.SessionId, null, ct)
            : null;

        try
        {
            // Opportunistically prune any stale sandbox containers
            await CleanupOrphanedContainersAsync(client, ct);

            var memoryLimitBytes = (long)memoryLimitMb * 1024 * 1024;
            var safeWorkspacePath = Path.GetFullPath(context.WorkspacePath);
            Directory.CreateDirectory(safeWorkspacePath);

            var hostConfig = new Docker.DotNet.Models.HostConfig
            {
                AutoRemove = false,
                Memory = memoryLimitBytes,
                NetworkMode = "none",
                Binds = new List<string>
                {
                    $"{safeWorkspacePath}:{containerDataPath}:rw"
                }
            };

            if (cpuQuota.HasValue)
            {
                hostConfig.CPUQuota = cpuQuota.Value;
                hostConfig.CPUPeriod = cpuPeriod;
            }

            var sanitizedScript = PythonScriptSanitizer.Sanitize(script);

            var createParams = new Docker.DotNet.Models.CreateContainerParameters
            {
                Image = imageName,
                Cmd = new List<string> { sanitizedScript },
                NetworkDisabled = true,
                Labels = new Dictionary<string, string>
                {
                    ["semantic-broker-sandbox"] = "true",
                    ["created-at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
                },
                HostConfig = hostConfig
            };

            string? containerId = null;
            try
            {
                var createResponse = await client.Containers.CreateContainerAsync(createParams, ct);
                containerId = createResponse.ID;
                await client.Containers.StartContainerAsync(containerId, new Docker.DotNet.Models.ContainerStartParameters(), ct);

                // Wait with timeout
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                Docker.DotNet.Models.ContainerWaitResponse waitResponse;
                try
                {
                    waitResponse = await client.Containers.WaitContainerAsync(containerId, waitCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        await client.Containers.KillContainerAsync(containerId, new Docker.DotNet.Models.ContainerKillParameters(), killCts.Token);
                    }
                    catch { /* Best effort kill - container removal in finally will also attempt force cleanup */ }
                    return $"Error: Script execution timed out after {timeoutSeconds} seconds.";
                }

                using var logs = await client.Containers.GetContainerLogsAsync(containerId, false,
                    new Docker.DotNet.Models.ContainerLogsParameters
                    {
                        ShowStdout = true,
                        ShowStderr = true
                    }, ct);

                var maxBytes = CalculateMaxOutputBytes(maxOutputMb);
                var (stdout, stderr) = await ReadBoundedOutputAsync(logs, maxBytes, ct);

                // Check container exit code
                if (waitResponse.StatusCode != 0)
                {
                    var errorSb = new StringBuilder();
                    errorSb.AppendLine($"Error: Python script execution failed (exit code {waitResponse.StatusCode}):");
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        errorSb.AppendLine(stderr);
                    }
                    else if (!string.IsNullOrWhiteSpace(stdout))
                    {
                        errorSb.AppendLine(stdout);
                    }

                    var hint = GenerateDiagnosticHint(stderr);
                    if (!string.IsNullOrEmpty(hint))
                    {
                        errorSb.AppendLine();
                        errorSb.AppendLine($"[Diagnostic Hint]: {hint}");
                    }

                    if (!string.IsNullOrWhiteSpace(stdout) && !string.IsNullOrWhiteSpace(stderr))
                    {
                        errorSb.AppendLine();
                        errorSb.AppendLine("Standard output:");
                        errorSb.Append(stdout);
                    }

                    return errorSb.ToString().TrimEnd();
                }

                // Check for caught tracebacks or exceptions when exit code is 0
                bool hasCaughtTraceback = (!string.IsNullOrEmpty(stderr) && (
                    stderr.Contains("Traceback (most recent call last):", StringComparison.OrdinalIgnoreCase) ||
                    stderr.Contains("SyntaxError:", StringComparison.OrdinalIgnoreCase) ||
                    stderr.Contains("IndentationError:", StringComparison.OrdinalIgnoreCase))) ||
                    (!string.IsNullOrEmpty(stdout) && (
                    stdout.Contains("Traceback (most recent call last):", StringComparison.OrdinalIgnoreCase) ||
                    stdout.Contains("Error during processing:", StringComparison.OrdinalIgnoreCase)));

                if (hasCaughtTraceback)
                {
                    var errorSb = new StringBuilder();
                    errorSb.AppendLine("Error: Python script execution encountered an unhandled or caught exception:");
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        errorSb.AppendLine(stderr);
                    }

                    var errorSource = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                    var hint = GenerateDiagnosticHint(errorSource);
                    if (!string.IsNullOrEmpty(hint))
                    {
                        errorSb.AppendLine();
                        errorSb.AppendLine($"[Diagnostic Hint]: {hint}");
                    }

                    if (!string.IsNullOrWhiteSpace(stdout))
                    {
                        errorSb.AppendLine();
                        errorSb.AppendLine("Standard output:");
                        errorSb.Append(stdout);
                    }

                    return errorSb.ToString().TrimEnd();
                }

                if (!string.IsNullOrEmpty(stderr))
                {
                    return $"Warnings:\n{stderr}\n\nStandard output:\n{stdout}";
                }

                return string.IsNullOrWhiteSpace(stdout)
                    ? "(No output produced. Make sure your script uses print() to output results.)"
                    : stdout;
            }

            finally
            {
                if (containerId != null)
                {
                    try
                    {
                        await client.Containers.RemoveContainerAsync(containerId, new Docker.DotNet.Models.ContainerRemoveParameters { Force = true });
                    }
                    catch { /* Ignore cleanup errors */ }
                }
            }
        }
        catch (Exception ex)
        {
            return $"Failed to execute docker integration: {ex.Message}";
        }
    }
    
    public static int CalculateMaxOutputBytes(int maxOutputMb)
    {
        long bytes = (long)maxOutputMb * 1024L * 1024L;
        return (int)Math.Clamp(bytes, 1024L * 1024L, (long)int.MaxValue);
    }

    public static async Task<(string stdout, string stderr)> ReadBoundedOutputAsync(Docker.DotNet.MultiplexedStream logs, int maxBytes, CancellationToken ct)
    {
        using var stdoutMs = new BoundedMemoryStream(maxBytes);
        using var stderrMs = new BoundedMemoryStream(maxBytes);

        try
        {
            using var waitTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitTimeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            await logs.CopyOutputToAsync(Stream.Null, stdoutMs, stderrMs, waitTimeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Reached timeout during copy
        }

        var stdoutBytes = stdoutMs.ToArray();
        var stderrBytes = stderrMs.ToArray();

        var stdoutStr = System.Text.Encoding.UTF8.GetString(stdoutBytes);
        if (stdoutMs.IsTruncated)
            stdoutStr += $"\n[OUTPUT TRUNCATED: Output exceeded {maxBytes / (1024 * 1024)} MB limit]";

        var stderrStr = System.Text.Encoding.UTF8.GetString(stderrBytes);
        if (stderrMs.IsTruncated)
            stderrStr += $"\n[ERROR LOG TRUNCATED: Output exceeded {maxBytes / (1024 * 1024)} MB limit]";

        return (stdoutStr, stderrStr);
    }

    public static async Task<(string stdout, string stderr)> ReadBoundedOutputAsync(Stream rawStream, int maxBytes, CancellationToken ct)
    {
        using var mux = new Docker.DotNet.MultiplexedStream(rawStream, false);
        return await ReadBoundedOutputAsync(mux, maxBytes, ct);
    }

    /// <summary>
    /// Analyzes standard error output from Python execution and produces targeted, actionable hints
    /// for common syntax, indentation, and quotation errors made by local LLMs in Python 3.12.
    /// </summary>
    public static string? GenerateDiagnosticHint(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return null;

        if (stderr.Contains("SyntaxError: f-string: expecting '}'", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("closing parenthesis '}' does not match opening parenthesis", StringComparison.OrdinalIgnoreCase))
        {
            return "In Python 3.12, single-quoted f-strings f'...' cannot contain unescaped single quotes inside {...}. Use double quotes for dictionary keys or inner strings (e.g. f'{row[\"col\"]}') or assign the expression to a variable before formatting.";
        }

        if (stderr.Contains("f-string expression part cannot include a backslash", StringComparison.OrdinalIgnoreCase))
        {
            return "In Python 3.12, backslashes are not allowed inside f-string {...} expressions. Assign the expression to a variable before formatting it into the f-string.";
        }

        if (stderr.Contains("SyntaxError: expected ':'", StringComparison.OrdinalIgnoreCase))
        {
            return "A compound statement (such as 'for', 'if', 'elif', 'else', 'while', 'try', 'except', 'def', or 'class') is missing a trailing colon ':'. Ensure the statement line ends with a colon.";
        }

        if (stderr.Contains("ValueError: invalid mode:", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid file mode in open(). Use 'w' for writing, 'r' for reading, or 'a' for appending.";
        }

        if (stderr.Contains("IndentationError: expected an indented block", StringComparison.OrdinalIgnoreCase))
        {
            return "Python blocks after 'else:', 'if:', 'try:', or 'except:' cannot consist solely of comments. Add 'pass' or indent your code statements properly.";
        }

        if (stderr.Contains("NameError:", StringComparison.OrdinalIgnoreCase) || stderr.Contains("KeyError:", StringComparison.OrdinalIgnoreCase))
        {
            return "Check variable and column names. Before processing, run a quick script to inspect columns (print(df.columns)) and verify DataFrame variable names.";
        }

        if (stderr.Contains("ZeroDivisionError:", StringComparison.OrdinalIgnoreCase))
        {
            return "Division by zero. Check if the denominator is zero or if a dataset or filtered group is empty before dividing.";
        }

        if (stderr.Contains("FileNotFoundError:", StringComparison.OrdinalIgnoreCase))
        {
            return "File not found. Verify the file path exists in /app/workdir/ (e.g. use os.path.exists() or check preceding step tool outputs).";
        }

        if (stderr.Contains("SyntaxError: invalid syntax", StringComparison.OrdinalIgnoreCase))
        {
            return "Python syntax error. Check for duplicate/stray tokens on the indicated line (e.g. 'for x in filtered ...'), missing commas in lists/dictionaries, or unclosed quotes/parentheses.";
        }

        return null;
    }

    private static readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private static long _lastCleanupTicks = 0;

    public static async Task CleanupOrphanedContainersAsync(Docker.DotNet.DockerClient client, CancellationToken ct = default)
    {
        var lastTicks = Interlocked.Read(ref _lastCleanupTicks);
        if (DateTime.UtcNow.Ticks - lastTicks < TimeSpan.FromSeconds(30).Ticks)
            return;

        // Try to acquire lock immediately; if another cleanup is already running, skip
        if (!await _cleanupLock.WaitAsync(0, ct))
            return;

        try
        {
            // Double-check after acquiring lock to prevent redundant cleanup sweeps
            lastTicks = Interlocked.Read(ref _lastCleanupTicks);
            if (DateTime.UtcNow.Ticks - lastTicks < TimeSpan.FromSeconds(30).Ticks)
                return;

            Interlocked.Exchange(ref _lastCleanupTicks, DateTime.UtcNow.Ticks);
            var containers = await client.Containers.ListContainersAsync(new Docker.DotNet.Models.ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { ["semantic-broker-sandbox=true"] = true }
                }
            }, ct);

            foreach (var container in containers)
            {
                try
                {
                    if (container.State == "exited" || container.State == "dead")
                    {
                        await client.Containers.RemoveContainerAsync(container.ID, new Docker.DotNet.Models.ContainerRemoveParameters { Force = true }, ct);
                    }
                    else if (container.Labels.TryGetValue("created-at", out var createdStr) &&
                             long.TryParse(createdStr, out var createdSec) &&
                             DateTimeOffset.UtcNow.ToUnixTimeSeconds() - createdSec > 600)
                    {
                        await client.Containers.RemoveContainerAsync(container.ID, new Docker.DotNet.Models.ContainerRemoveParameters { Force = true }, ct);
                    }
                }
                catch { /* Best effort per-container cleanup */ }
            }
        }
        catch { /* Best effort daemon cleanup */ }
        finally
        {
            _cleanupLock.Release();
        }
    }

    private sealed class BoundedMemoryStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly int _maxBytes;
        public bool IsTruncated { get; private set; }

        public BoundedMemoryStream(int maxBytes)
        {
            _maxBytes = maxBytes;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            var remaining = _maxBytes - (int)_inner.Length;
            if (remaining <= 0)
            {
                IsTruncated = true;
                return;
            }

            var toWrite = Math.Min(count, remaining);
            _inner.Write(buffer, offset, toWrite);
            if (toWrite < count)
            {
                IsTruncated = true;
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var remaining = _maxBytes - (int)_inner.Length;
            if (remaining <= 0)
            {
                IsTruncated = true;
                return;
            }

            var toWrite = Math.Min(count, remaining);
            await _inner.WriteAsync(buffer.AsMemory(offset, toWrite), cancellationToken);
            if (toWrite < count)
            {
                IsTruncated = true;
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = _maxBytes - (int)_inner.Length;
            if (remaining <= 0)
            {
                IsTruncated = true;
                return;
            }

            var toWrite = Math.Min(buffer.Length, remaining);
            await _inner.WriteAsync(buffer[..toWrite], cancellationToken);
            if (toWrite < buffer.Length)
            {
                IsTruncated = true;
            }
        }

        public byte[] ToArray() => _inner.ToArray();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

