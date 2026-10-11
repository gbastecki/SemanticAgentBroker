using System.Text;
using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.Plugins.BuiltIn.Required;

/// <summary>
/// Built-in plugin providing file I/O operations within the agent's isolated workspace.
/// Enables agents to save/read data files without sending large payloads through the LLM context.
/// </summary>
public class FileIOAgentPlugin : PluginBase
{
    public override string Id => "FileIO";
    public override string DisplayName => "File I/O";
    public override string Description => "Read, write, and list files in the agent's isolated workspace. Files are accessible from the Python sandbox at /app/workdir/.";
    public override string Version => "1.0.0";
    public override string Author => "SemanticAgentBroker";
    public override string Icon => "folder_open";
    public override IReadOnlyList<string> Tags => ["file", "io", "workspace"];
    public override PluginCategory Category => PluginCategory.BuiltInRequired;

    public override IReadOnlyList<IPluginMethod> GetMethods() =>
    [
        new SaveToWorkspaceMethod(),
        new AppendToWorkspaceMethod(),
        new ReadFromWorkspaceMethod(),
        new ListWorkspaceFilesMethod(),
        new ViewWorkspaceFileMethod(),
        new GrepWorkspaceFileMethod(),
        new ReplaceInWorkspaceFileMethod()
    ];

    public override IReadOnlyList<PluginConfigEntry> GetConfigurationSchema() =>
    [
        new() { Key = "MaxReadChars", DisplayName = "Max Read Characters", Description = "Maximum characters returned by ReadFromWorkspace before truncation.", DefaultValue = "8000", IsRequired = false }
    ];
}

public class SaveToWorkspaceMethod : PluginMethodBase
{
    public override string Id => "SaveToWorkspace";
    public override string DisplayName => "Save to Workspace";
    public override string Description => "Saves text content to a file in the agent's workspace. Returns the absolute file path inside the Python sandbox (/app/workdir/). Use this to store data for later processing with Python or for transforming data between formats.";
    public override string ReturnDescription => "Confirmation message with the file path in the Python sandbox.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "filename", Description = "The filename to save (e.g. 'input.json', 'output.xml', 'data.csv').", ParameterType = typeof(string), IsRequired = true },
        new() { Name = "content", Description = "The text content to write to the file.", ParameterType = typeof(string), IsRequired = true }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        try
        {
            var filename = GetRequiredString(arguments, "filename");
            var content = GetRequiredString(arguments, "content");

            var safeName = SanitizeFilename(filename);
            using var fileLock = await AcquireLockAsync(context, safeName, ct);

            var filePath = ResolveAndValidatePath(filename, context.WorkspacePath, true);
            var fileDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDir))
            {
                Directory.CreateDirectory(fileDir);
            }
            await File.WriteAllTextAsync(filePath, content, new UTF8Encoding(false), ct);
            return $"File saved successfully. Path in Python sandbox: /app/workdir/{safeName}";
        }
        catch (Exception ex)
        {
            return $"Error saving file: {ex.Message}";
        }
    }

    internal static async Task<IDisposable?> AcquireLockAsync(IPluginContext context, string fileName, CancellationToken ct)
    {
        var lockManager = context.GetService<IWorkspaceLockManager>();
        if (lockManager != null && context.SessionId != Guid.Empty)
        {
            return await lockManager.AcquireLockAsync(context.SessionId, fileName, ct);
        }
        return null;
    }

    internal static string ResolveAndValidatePath(string filename, string workspacePath, bool forWriting = false)
    {
        var safeName = SanitizeFilename(filename);
        
        if (forWriting && (safeName.StartsWith('_') || Path.GetFileName(safeName).StartsWith('_')))
        {
            throw new InvalidOperationException($"Permission denied: Cannot modify system files starting with '_' (e.g. {safeName}).");
        }
        
        var fullPath = Path.GetFullPath(Path.Combine(workspacePath, safeName));

        var normalizedWorkspace = Path.GetFullPath(workspacePath);
        if (!normalizedWorkspace.EndsWith(Path.DirectorySeparatorChar))
        {
            normalizedWorkspace += Path.DirectorySeparatorChar;
        }

        if (!fullPath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path traversal attempt detected. Resolved path '{fullPath}' is outside workspace '{normalizedWorkspace}'.");
        }

        // Validate symbolic link / junction target if file exists on disk
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.LinkTarget != null)
            {
                var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    var targetFullPath = Path.GetFullPath(target.FullName);
                    if (!targetFullPath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Path traversal attempt via symbolic link detected. Target '{targetFullPath}' is outside workspace '{normalizedWorkspace}'.");
                    }
                }
            }
        }

        // Validate all ancestor directories to prevent symlink traversal through existing parent/junction directories
        var parentDir = new DirectoryInfo(Path.GetDirectoryName(fullPath) ?? workspacePath);
        while (parentDir != null && !string.Equals(parentDir.FullName.TrimEnd(Path.DirectorySeparatorChar), normalizedWorkspace.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            if (parentDir.Exists && (parentDir.LinkTarget != null || (parentDir.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                var dirTarget = parentDir.ResolveLinkTarget(returnFinalTarget: true);
                if (dirTarget != null)
                {
                    var dirTargetFullPath = Path.GetFullPath(dirTarget.FullName);
                    if (!dirTargetFullPath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Path traversal attempt via symbolic link directory detected. Target '{dirTargetFullPath}' is outside workspace '{normalizedWorkspace}'.");
                    }
                }
            }
            parentDir = parentDir.Parent;
        }

        return fullPath;
    }

    internal static string SanitizeFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return "output.txt";

        // LLMs often pass absolute paths like "/app/workdir/file.txt", Windows-style "workdir\file.txt", or "C:\...".
        var normalized = filename.Replace('\\', '/').Trim();

        // Strip Windows drive letters if present (e.g. "C:/app/workdir/file.txt" or "D:file.txt")
        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        {
            normalized = normalized[2..].TrimStart('/');
        }

        // Strip sandbox root prefixes
        if (normalized.StartsWith("/app/workdir/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["/app/workdir/".Length..];
        }
        else if (normalized.StartsWith("app/workdir/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["app/workdir/".Length..];
        }

        normalized = normalized.Trim('/');

        if (string.IsNullOrWhiteSpace(normalized))
            return "output.txt";

        var invalidChars = Path.GetInvalidFileNameChars();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cleanSegments = new List<string>();

        foreach (var segment in segments)
        {
            var cleanSegment = new string(segment.Where(c => !invalidChars.Contains(c)).ToArray()).Trim();
            cleanSegment = cleanSegment.Replace("..", "");

            if (!string.IsNullOrWhiteSpace(cleanSegment) && cleanSegment != ".")
            {
                cleanSegments.Add(cleanSegment);
            }
        }

        if (cleanSegments.Count == 0)
            return "output.txt";

        return string.Join('/', cleanSegments);
    }
}

public class AppendToWorkspaceMethod : PluginMethodBase
{
    public override string Id => "AppendToWorkspace";
    public override string DisplayName => "Append to Workspace File";
    public override string Description => "Appends text content to an existing file in the workspace. Creates the file if it doesn't exist. Use for incremental data collection instead of creating multiple files.";
    public override string ReturnDescription => "Confirmation message.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "filename", Description = "The filename to append to.", ParameterType = typeof(string), IsRequired = true },
        new() { Name = "content", Description = "The text content to append.", ParameterType = typeof(string), IsRequired = true }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        try
        {
            var filename = GetRequiredString(arguments, "filename");
            var content = GetRequiredString(arguments, "content");

            var safeName = SaveToWorkspaceMethod.SanitizeFilename(filename);
            using var fileLock = await SaveToWorkspaceMethod.AcquireLockAsync(context, safeName, ct);

            var filePath = SaveToWorkspaceMethod.ResolveAndValidatePath(filename, context.WorkspacePath, true);
            var fileDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDir))
            {
                Directory.CreateDirectory(fileDir);
            }

            bool existed = File.Exists(filePath);
            await File.AppendAllTextAsync(filePath, (existed ? "\n" : "") + content, new UTF8Encoding(false), ct);
            var totalSize = new FileInfo(filePath).Length;
            return $"Content appended to /app/workdir/{safeName} (now {totalSize:N0} bytes).";
        }
        catch (Exception ex)
        {
            return $"Error appending to file: {ex.Message}";
        }
    }
}

public class ReadFromWorkspaceMethod : PluginMethodBase
{
    public override string Id => "ReadFromWorkspace";
    public override string DisplayName => "Read from Workspace";
    public override string Description => "Reads the content of a file from the agent's workspace. Returns the file content as a string. Use sparingly for small files only - for large files, use Python instead.";
    public override string ReturnDescription => "File content as a string, possibly truncated for large files.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "filename", Description = "The filename to read (e.g. 'output.xml').", ParameterType = typeof(string), IsRequired = true }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        try
        {
            var filename = GetRequiredString(arguments, "filename");
            var safeName = SaveToWorkspaceMethod.SanitizeFilename(filename);
            using var fileLock = await SaveToWorkspaceMethod.AcquireLockAsync(context, safeName, ct);

            var filePath = SaveToWorkspaceMethod.ResolveAndValidatePath(filename, context.WorkspacePath);

            if (!File.Exists(filePath))
            {
                var available = Directory.Exists(context.WorkspacePath)
                    ? string.Join(", ", Directory.GetFiles(context.WorkspacePath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Select(f => Path.GetRelativePath(context.WorkspacePath, f).Replace('\\', '/')))
                    : "(workspace directory does not exist)";
                return $"Error: File '{safeName}' not found in workspace. Available files: {available}";
            }

            var maxChars = int.TryParse(context.Configuration.GetValueOrDefault("MaxReadChars", "8000"), out var mc) ? mc : 8000;
            var fileInfo = new FileInfo(filePath);

            // Prepend metadata so the LLM can decide whether to use Python
            var header = $"[File: {safeName} | Size: {fileInfo.Length:N0} bytes | Est. Lines: ~{Math.Max(1, fileInfo.Length / 40)}]\n\n";

            // Stream only the required bytes - never load the entire file
            using var reader = new StreamReader(filePath);
            var buffer = new char[maxChars];
            int charsRead = await reader.ReadBlockAsync(buffer, 0, maxChars);
            var content = new string(buffer, 0, charsRead);

            if (charsRead >= maxChars)
            {
                // Find a clean break point (last newline) to avoid cutting mid-line
                int lastNewline = content.LastIndexOf('\n');
                if (lastNewline > maxChars / 2)
                    content = content[..lastNewline];

                return header + content +
                    $"\n\n... [TRUNCATED - file is {fileInfo.Length:N0} bytes. " +
                    $"Use FileIO-ViewWorkspaceFile for line ranges, or Python for full processing at /app/workdir/{safeName}]";
            }

            return header + content;
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }
}

public class ListWorkspaceFilesMethod : PluginMethodBase
{
    public override string Id => "ListWorkspaceFiles";
    public override string DisplayName => "List Workspace Files";
    public override string Description => "Lists all files currently in the agent's workspace directory. Returns filenames and sizes.";
    public override string ReturnDescription => "List of filenames with their sizes.";

    public override Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        if (!Directory.Exists(context.WorkspacePath))
            return Task.FromResult("Workspace directory is empty.");

        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };
        var files = Directory.GetFiles(context.WorkspacePath, "*", enumOptions);
        if (files.Length == 0)
            return Task.FromResult("Workspace directory is empty.");

        var sb = new StringBuilder();
        sb.AppendLine("Files in workspace:");
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            var relativePath = Path.GetRelativePath(context.WorkspacePath, file).Replace('\\', '/');
            sb.AppendLine($"  - {relativePath} ({info.Length:N0} bytes)");
        }
        return Task.FromResult(sb.ToString());
    }
}

public class ViewWorkspaceFileMethod : PluginMethodBase
{
    public override string Id => "ViewWorkspaceFile";
    public override string DisplayName => "View Workspace File";
    public override string Description => "Reads a specific range of lines from a file. Useful for peeking at the schema or contents of massive files without blowing up the context window.";
    public override string ReturnDescription => "The requested lines from the file.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "filename", Description = "The filename to read.", ParameterType = typeof(string), IsRequired = true },
        new() { Name = "startLine", Description = "The starting line number (1-indexed).", ParameterType = typeof(int), IsRequired = true },
        new() { Name = "endLine", Description = "The ending line number (inclusive).", ParameterType = typeof(int), IsRequired = true }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        try
        {
            var filename = GetRequiredString(arguments, "filename");
            var safeName = SaveToWorkspaceMethod.SanitizeFilename(filename);
            using var fileLock = await SaveToWorkspaceMethod.AcquireLockAsync(context, safeName, ct);

            var filePath = SaveToWorkspaceMethod.ResolveAndValidatePath(filename, context.WorkspacePath);
            
            if (!File.Exists(filePath))
            {
                var available = Directory.Exists(context.WorkspacePath)
                    ? string.Join(", ", Directory.GetFiles(context.WorkspacePath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Select(f => Path.GetRelativePath(context.WorkspacePath, f).Replace('\\', '/')))
                    : "(workspace directory does not exist)";
                return $"Error: File '{filename}' not found in workspace. Available files: {available}";
            }

            if (!arguments.TryGetValue("startLine", out var slObj) || !int.TryParse(slObj?.ToString(), out var startLine) || startLine < 1)
                startLine = 1;
            if (!arguments.TryGetValue("endLine", out var elObj) || !int.TryParse(elObj?.ToString(), out var endLine) || endLine < startLine)
                endLine = startLine + 100;

            // Limit range to prevent abuse
            if (endLine - startLine > 500)
                endLine = startLine + 500;

            var lines = new List<string>();
            int currentLine = 1;
            using var reader = new StreamReader(filePath);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (currentLine >= startLine && currentLine <= endLine)
                    lines.Add(line);
                if (currentLine > endLine)
                    break;
                currentLine++;
            }

            if (lines.Count == 0)
                return "No lines found in the specified range.";

            var sb = new StringBuilder();
            sb.AppendLine($"Showing lines {startLine} to {startLine + lines.Count - 1}:");
            for (int i = 0; i < lines.Count; i++)
            {
                sb.AppendLine($"{startLine + i}: {lines[i]}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error viewing file: {ex.Message}";
        }
    }
}

public class GrepWorkspaceFileMethod : PluginMethodBase
{
    public override string Id => "GrepWorkspaceFile";
    public override string DisplayName => "Grep Workspace File";
    public override string Description => "Searches for a specific string or regex pattern within a file. Returns matching lines and their line numbers.";
    public override string ReturnDescription => "Matching lines with line numbers.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "filename", Description = "The filename to search.", ParameterType = typeof(string), IsRequired = true },
        new() { Name = "pattern", Description = "The regex pattern or string to search for.", ParameterType = typeof(string), IsRequired = true },
        new() { Name = "contextLines", Description = "Number of surrounding lines to show (0-3). Like grep -C. Default: 0.", ParameterType = typeof(int), IsRequired = false },
        new() { Name = "maxResults", Description = "Maximum number of matches to return. Default: 50.", ParameterType = typeof(int), IsRequired = false }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        try
        {
            var filename = GetRequiredString(arguments, "filename");
            var pattern = GetRequiredString(arguments, "pattern");
            int contextLineCount = int.TryParse(GetOptionalString(arguments, "contextLines", "0"), out var cl) ? Math.Clamp(cl, 0, 3) : 0;
            int maxResults = int.TryParse(GetOptionalString(arguments, "maxResults", "50"), out var mr) ? Math.Clamp(mr, 1, 200) : 50;

            var safeName = SaveToWorkspaceMethod.SanitizeFilename(filename);
            using var fileLock = await SaveToWorkspaceMethod.AcquireLockAsync(context, safeName, ct);

            var filePath = SaveToWorkspaceMethod.ResolveAndValidatePath(filename, context.WorkspacePath);
            
            if (!File.Exists(filePath))
                return $"Error: File '{filename}' not found in workspace.";

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > 50_000_000) // 50MB guard
                return $"Error: File '{filename}' is too large ({fileInfo.Length:N0} bytes) for in-memory grep. Use Python to search it at /app/workdir/{safeName}.";

            System.Text.RegularExpressions.Regex regex;
            try
            {
                regex = new System.Text.RegularExpressions.Regex(pattern, 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.NonBacktracking, 
                    TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException ex)
            {
                return $"Error: Invalid regular expression pattern '{pattern}': {ex.Message}";
            }

            var results = new List<string>();
            var preBuffer = new Queue<(int LineNumber, string Line)>();
            int lastEmittedLine = -1;
            int postContextRemaining = 0;
            int matchCount = 0;
            int currentLineNumber = 0;
            bool isTruncated = false;

            using (var reader = new StreamReader(filePath))
            {
                string? currentLine;
                while ((currentLine = await reader.ReadLineAsync(ct)) != null)
                {
                    currentLineNumber++;
                    if (regex.IsMatch(currentLine))
                    {
                        if (matchCount >= maxResults)
                        {
                            results.Add($"... [TRUNCATED at {maxResults} matches]");
                            isTruncated = true;
                            break;
                        }

                        matchCount++;

                        if (contextLineCount > 0 && results.Count > 0 && lastEmittedLine < currentLineNumber - 1)
                        {
                            results.Add("---");
                        }

                        // Emit pre-context lines
                        foreach (var (pNum, pLine) in preBuffer)
                        {
                            if (pNum > lastEmittedLine)
                            {
                                results.Add($"  {pNum}: {pLine}");
                                lastEmittedLine = pNum;
                            }
                        }

                        // Emit match line
                        results.Add($"▶ {currentLineNumber}: {currentLine}");
                        lastEmittedLine = currentLineNumber;
                        postContextRemaining = contextLineCount;

                        if (contextLineCount > 0)
                        {
                            if (preBuffer.Count >= contextLineCount) preBuffer.Dequeue();
                            preBuffer.Enqueue((currentLineNumber, currentLine));
                        }
                    }
                    else
                    {
                        if (postContextRemaining > 0)
                        {
                            results.Add($"  {currentLineNumber}: {currentLine}");
                            lastEmittedLine = currentLineNumber;
                            postContextRemaining--;
                        }

                        if (contextLineCount > 0)
                        {
                            if (preBuffer.Count >= contextLineCount) preBuffer.Dequeue();
                            preBuffer.Enqueue((currentLineNumber, currentLine));
                        }
                    }
                }
            }

            if (results.Count == 0)
                return "No matches found.";

            string countLabel = isTruncated ? $"{matchCount}+" : $"{matchCount}";
            return $"{countLabel} match(es) in {filename}:\n" + string.Join("\n", results);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return $"Error: Search timed out after 5 seconds due to pattern complexity.";
        }
        catch (Exception ex)
        {
            return $"Error searching file: {ex.Message}";
        }
    }
}

public class ReplaceInWorkspaceFileMethod : PluginMethodBase
{
    public override string Id => "ReplaceInWorkspaceFile";
    public override string DisplayName => "Replace in Workspace File";
    public override string Description => "Replaces all occurrences of a text pattern in a file with new content. Use for targeted updates without rewriting the entire file.";
    public override string ReturnDescription => "Number of replacements made.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "filename", Description = "The filename to modify.", ParameterType = typeof(string), IsRequired = true },
        new() { Name = "searchText", Description = "The exact text to find.", ParameterType = typeof(string), IsRequired = true },
        new() { Name = "replaceText", Description = "The replacement text.", ParameterType = typeof(string), IsRequired = true }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        try
        {
            var filename = GetRequiredString(arguments, "filename");
            var searchText = GetOptionalString(arguments, "searchText");
            if (string.IsNullOrEmpty(searchText))
                return "Error: 'searchText' cannot be empty.";

            var replaceText = GetOptionalString(arguments, "replaceText", "");

            var safeName = SaveToWorkspaceMethod.SanitizeFilename(filename);
            using var fileLock = await SaveToWorkspaceMethod.AcquireLockAsync(context, safeName, ct);

            var filePath = SaveToWorkspaceMethod.ResolveAndValidatePath(filename, context.WorkspacePath, true);

            if (!File.Exists(filePath))
                return $"Error: File '{filename}' not found in workspace.";

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > 50_000_000) // 50MB guard
                return $"Error: File '{filename}' is too large ({fileInfo.Length:N0} bytes) for in-memory replacement. Use Python to modify it at /app/workdir/{safeName}.";

            var content = await File.ReadAllTextAsync(filePath, ct);
            int count = 0;
            var idx = 0;
            while ((idx = content.IndexOf(searchText, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += searchText.Length;
            }

            if (count == 0)
                return $"No occurrences of the search text found in '{filename}'.";

            var updated = content.Replace(searchText, replaceText);
            await File.WriteAllTextAsync(filePath, updated, new System.Text.UTF8Encoding(false), ct);

            return $"Replaced {count} occurrence(s) in /app/workdir/{safeName}.";
        }
        catch (Exception ex)
        {
            return $"Error replacing text in file: {ex.Message}";
        }
    }
}
