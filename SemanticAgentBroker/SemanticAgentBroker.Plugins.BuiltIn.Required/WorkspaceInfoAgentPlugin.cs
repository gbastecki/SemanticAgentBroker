using System.Text;
using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.Plugins.BuiltIn.Required;

/// <summary>
/// Built-in core plugin providing workspace spatial awareness.
/// Gives the agent the ability to inspect the directory tree, search for files,
/// and check disk usage, reducing hallucinated file paths.
/// </summary>
public class WorkspaceInfoAgentPlugin : PluginBase
{
    public override string Id => "WorkspaceInfo";
    public override string DisplayName => "Workspace Info";
    public override string Description => "Get information about files and directories in the agent's workspace. Essential for spatial awareness.";
    public override string Version => "1.0.0";
    public override string Author => "SemanticAgentBroker";
    public override string Icon => "account_tree";
    public override IReadOnlyList<string> Tags => ["file", "workspace", "info", "search"];
    public override PluginCategory Category => PluginCategory.BuiltInRequired;

    public override IReadOnlyList<IPluginMethod> GetMethods() =>
    [
        new GetWorkspaceTreeMethod(),
        new SearchFilesMethod(),
        new GetDiskUsageMethod()
    ];

    public override IReadOnlyList<PluginConfigEntry> GetConfigurationSchema() =>
    [
        new() { Key = "MaxDepth", DisplayName = "Max Directory Depth", Description = "Maximum depth to recursively traverse the workspace directory tree. Prevents stack overflow.", DefaultValue = "100" },
        new() { Key = "MaxFiles", DisplayName = "Max Files Scanned", Description = "Maximum number of files to scan when summarizing disk usage to prevent performance degradation.", DefaultValue = "50000" }
    ];
}

public class GetWorkspaceTreeMethod : PluginMethodBase
{
    public override string Id => "GetWorkspaceTree";


    public override string DisplayName => "Get Workspace Tree";
    public override string Description => "Returns a formatted directory tree of the workspace, including file sizes. Useful to understand what files have been created or are available.";
    public override string ReturnDescription => "A string representation of the workspace directory tree.";

    public override IReadOnlyList<PluginParameter> Parameters => []; // No parameters

    public override Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        if (!Directory.Exists(context.WorkspacePath))
            return Task.FromResult("Workspace directory is empty or does not exist.");

        var sb = new StringBuilder();
        sb.AppendLine("workspace/");
        
        long totalSize = 0;
        int fileCount = 0;
        
        int maxDepth = int.TryParse(context.Configuration.GetValueOrDefault("MaxDepth", "100"), out var md) ? Math.Max(0, md) : 100;

        BuildTree(new DirectoryInfo(context.WorkspacePath), sb, "", ref totalSize, ref fileCount, maxDepth, 0);

        if (fileCount == 0)
            return Task.FromResult("Workspace directory is empty.");

        sb.AppendLine($"\nTotal: {fileCount} files, {totalSize:N0} bytes");
        return Task.FromResult(sb.ToString());
    }

    private void BuildTree(DirectoryInfo dir, StringBuilder sb, string indent, ref long totalSize, ref int fileCount, int maxDepth, int currentDepth)
    {
        if (currentDepth >= maxDepth)
        {
            sb.AppendLine($"{indent}└── ... (max depth {maxDepth} reached)");
            return;
        }

        DirectoryInfo[] dirs;
        FileInfo[] files;
        try
        {
            dirs = dir.GetDirectories();
            files = dir.GetFiles();
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        int totalItems = dirs.Length + files.Length;
        int currentItem = 0;

        foreach (var d in dirs)
        {
            currentItem++;
            bool isLast = currentItem == totalItems;
            sb.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}{d.Name}/");
            BuildTree(d, sb, indent + (isLast ? "    " : "│   "), ref totalSize, ref fileCount, maxDepth, currentDepth + 1);
        }

        foreach (var f in files)
        {
            currentItem++;
            bool isLast = currentItem == totalItems;
            long size = 0;
            try
            {
                if (f.Exists) size = f.Length;
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
            {
                // Ignore transient file lock, deletion, or permission error
            }
            sb.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}{f.Name} ({size:N0} bytes)");
            totalSize += size;
            fileCount++;
        }
    }
}

public class SearchFilesMethod : PluginMethodBase
{
    public override string Id => "SearchFiles";
    public override string DisplayName => "Search Files";
    public override string Description => "Searches for files in the workspace matching a glob pattern (e.g., '*.csv', 'report*'). Returns a list of matching file paths and their sizes.";
    public override string ReturnDescription => "List of matching files and their properties.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "pattern", Description = "Glob pattern to search for (e.g., '*.json').", ParameterType = typeof(string), IsRequired = true }
    ];

    public override Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var pattern = GetRequiredString(arguments, "pattern");

        if (pattern.Contains("..") || Path.IsPathRooted(pattern))
            return Task.FromResult("Error searching files: Search pattern must not contain directory traversal sequences ('..') or rooted paths.");

        if (!Directory.Exists(context.WorkspacePath))
            return Task.FromResult("Workspace directory is empty or does not exist.");

        var normalizedWorkspace = Path.GetFullPath(context.WorkspacePath);
        if (!normalizedWorkspace.EndsWith(Path.DirectorySeparatorChar))
        {
            normalizedWorkspace += Path.DirectorySeparatorChar;
        }

        try
        {
            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.PlatformDefault
            };
            var files = Directory.GetFiles(context.WorkspacePath, pattern, enumOptions);
            if (files.Length == 0)
                return Task.FromResult($"No files found matching pattern '{pattern}'.");

            var sb = new StringBuilder();
            sb.AppendLine($"Files matching '{pattern}':");
            int matchCount = 0;
            
            foreach (var file in files)
            {
                var fullFilePath = Path.GetFullPath(file);
                if (!fullFilePath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                    continue;

                matchCount++;
                var relPath = Path.GetRelativePath(context.WorkspacePath, file);
                long size = 0;
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Exists) size = fi.Length;
                }
                catch
                {
                    // Ignore transient file lock or deletion
                }
                sb.AppendLine($"- {relPath} ({size:N0} bytes)");
            }

            if (matchCount == 0)
                return Task.FromResult($"No files found matching pattern '{pattern}'.");
            
            return Task.FromResult(sb.ToString());
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error searching files: {ex.Message}");
        }
    }
}

public class GetDiskUsageMethod : PluginMethodBase
{
    public override string Id => "GetDiskUsage";
    public override string DisplayName => "Get Disk Usage";
    public override string Description => "Summarizes the total disk space utilized by the workspace and identifies the largest file.";
    public override string ReturnDescription => "A summary of workspace disk usage.";

    public override IReadOnlyList<PluginParameter> Parameters => [];

    public override Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        if (!Directory.Exists(context.WorkspacePath))
            return Task.FromResult("Workspace directory is empty or does not exist.");

        int maxDepth = int.TryParse(context.Configuration.GetValueOrDefault("MaxDepth", "100"), out var md) ? Math.Max(0, md) : 100;
        int maxFiles = int.TryParse(context.Configuration.GetValueOrDefault("MaxFiles", "50000"), out var mf) ? Math.Max(1, mf) : 50000;

        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = maxDepth > 0,
            MaxRecursionDepth = maxDepth,
            IgnoreInaccessible = true
        };

        var dirInfo = new DirectoryInfo(context.WorkspacePath);
        long totalSize = 0;
        int fileCount = 0;
        FileInfo? largestFile = null;
        long largestFileLength = 0;
        bool capped = false;

        try
        {
            foreach (var file in dirInfo.EnumerateFiles("*", enumOptions))
            {
                ct.ThrowIfCancellationRequested();
                long size = 0;
                try
                {
                    if (file.Exists) size = file.Length;
                }
                catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
                {
                    continue; // Skip transient or inaccessible files
                }

                fileCount++;
                totalSize += size;
                if (largestFile == null || size > largestFileLength)
                {
                    largestFile = file;
                    largestFileLength = size;
                }

                if (fileCount >= maxFiles)
                {
                    capped = true;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Directory disappeared or inaccessible during enumeration
        }

        if (fileCount == 0)
            return Task.FromResult("Workspace is empty (0 bytes).");

        var relPath = largestFile != null ? Path.GetRelativePath(context.WorkspacePath, largestFile.FullName) : string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Workspace Disk Usage:");
        sb.AppendLine($"- Total Files: {fileCount:N0}" + (capped ? $" (capped at {maxFiles:N0})" : ""));
        sb.AppendLine($"- Total Size: {totalSize:N0} bytes ({totalSize / 1024.0 / 1024.0:F2} MB)" + (capped ? " (partial)" : ""));
        sb.AppendLine($"- Largest File: {relPath} ({largestFileLength:N0} bytes)");

        return Task.FromResult(sb.ToString());
    }
}
