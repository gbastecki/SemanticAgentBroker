namespace SemanticAgentBroker.Sdk;

/// <summary>
/// Helper base class for defining plugin methods with minimal boilerplate.
/// Plugin authors can also implement <see cref="IPluginMethod"/> directly.
/// </summary>
public abstract class PluginMethodBase : IPluginMethod
{
    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public virtual IReadOnlyList<PluginParameter> Parameters => [];

    /// <inheritdoc />
    public virtual string ReturnDescription => "String result.";

    /// <inheritdoc />
    public virtual bool IsDangerous => false;

    /// <inheritdoc />
    public abstract Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct = default);

    /// <summary>
    /// Helper to get a required string argument, throwing a clear error if missing.
    /// </summary>
    protected static string GetRequiredString(IDictionary<string, object?> arguments, string key)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.TryGetValue(key, out var value) && value is string strVal && !string.IsNullOrEmpty(strVal))
            return strVal;

        // Also handle the case where the value is a non-null object (e.g., JsonElement)
        if (value != null)
        {
            var str = value.ToString();
            if (!string.IsNullOrEmpty(str))
                return str;
        }

        throw new ArgumentException($"Required parameter '{key}' is missing or empty.");
    }

    /// <summary>
    /// Helper to get an optional string argument with a default value.
    /// </summary>
    protected static string GetOptionalString(IDictionary<string, object?> arguments, string key, string defaultValue = "")
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.TryGetValue(key, out var value) && value != null)
        {
            var str = value.ToString();
            return string.IsNullOrEmpty(str) ? defaultValue : str;
        }
        return defaultValue;
    }

    /// <summary>
    /// Checks if a string result exceeds the max length. If so, saves it to the workspace and returns a pointer string.
    /// Uses deterministic filenames per action to prevent workspace file proliferation.
    /// Always overwrites - each call produces a complete, self-contained result.
    /// If incremental collection is needed, use FileIO-AppendToWorkspace explicitly.
    /// </summary>
    protected async Task<string> SaveLargeResultAsync(string result, string prefix, IPluginContext context, int maxLength = 2000, string extension = "txt")
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        if (result.Length <= maxLength)
            return result;

        var cleanPrefix = SanitizePrefix(prefix);
        var cleanExtension = SanitizeExtension(extension);

        // Deterministic filename - same action always writes to the same file
        var fileName = $"{cleanPrefix}_output.{cleanExtension}";
        var fullWorkspace = Path.GetFullPath(context.WorkspacePath);
        if (!fullWorkspace.EndsWith(Path.DirectorySeparatorChar))
        {
            fullWorkspace += Path.DirectorySeparatorChar;
        }
        var filePath = Path.GetFullPath(Path.Combine(fullWorkspace, fileName));

        if (!filePath.StartsWith(fullWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path traversal detected in SaveLargeResultAsync. Target '{filePath}' is outside workspace '{fullWorkspace}'.");
        }

        // Always overwrite - each call produces a complete result
        Directory.CreateDirectory(fullWorkspace);
        var lockManager = context.GetService<IWorkspaceLockManager>();
        if (lockManager != null && context.SessionId != Guid.Empty)
        {
            using (await lockManager.AcquireLockAsync(context.SessionId, fileName))
            {
                await File.WriteAllTextAsync(filePath, result);
            }
        }
        else
        {
            await File.WriteAllTextAsync(filePath, result);
        }
        return $"Data saved to /app/workdir/{fileName} ({result.Length:N0} chars). Use Python or FileIO to process.";
    }

    private static string SanitizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return "result";

        var normalized = prefix.Replace('\\', '/');
        var nameOnly = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(nameOnly))
            nameOnly = normalized;

        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(nameOnly.Where(c => !invalid.Contains(c) && c != '\\' && c != '/').ToArray());
        clean = clean.Replace("..", "").Trim();
        return string.IsNullOrWhiteSpace(clean) ? "result" : clean;
    }

    private static string SanitizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return "txt";

        var ext = Path.GetExtension(extension);
        if (string.IsNullOrEmpty(ext))
            ext = extension;

        ext = ext.TrimStart('.');
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(ext.Where(c => !invalid.Contains(c) && c != '\\' && c != '/').ToArray());
        clean = clean.Replace("..", "").Trim();
        return string.IsNullOrWhiteSpace(clean) ? "txt" : clean;
    }
}
