namespace SemanticAgentBroker.Shared.Utilities;

/// <summary>
/// Utility methods for workspace file handling and text classification.
/// </summary>
public static class WorkspaceFileUtils
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".py", ".xml", ".html", ".htm",
        ".log", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".cs", ".js", ".ts", ".css", ".sql", ".sh", ".bat", ".ps1",
        ".env", ".gitignore", ".dockerfile", ".razor", ".csproj", ".sln",
        ".props", ".targets", ".editorconfig", ".gitattributes"
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Makefile", "LICENSE", "README", "Procfile", "Gemfile",
        "Containerfile", "Rakefile", ".gitignore", ".gitattributes", ".editorconfig", ".env"
    };

    /// <summary>
    /// Checks whether a given filename represents an editable text file based on its extension or well-known name.
    /// </summary>
    public static bool IsTextFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var nameOnly = Path.GetFileName(fileName.Replace('\\', '/'));
        if (TextFileNames.Contains(nameOnly))
            return true;

        if (nameOnly.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase) ||
            nameOnly.StartsWith("Containerfile.", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(ext) && TextExtensions.Contains(ext))
            return true;

        return false;
    }
}
