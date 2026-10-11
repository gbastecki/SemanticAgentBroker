namespace SemanticAgentBroker.Shared.Configuration;

/// <summary>
/// Utility helper to resolve portable and dynamic path tokens (such as %CURRENT_USER%,
/// %USERPROFILE%, %MYDOCUMENTS%, and ~) into fully-qualified platform paths, ensuring
/// target directories exist before file/database operations occur.
/// </summary>
public static class PathResolutionHelper
{
    /// <summary>
    /// Expands dynamic environment and user profile tokens in a file or directory path.
    /// </summary>
    public static string ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var resolved = path
            .Replace("%CURRENT_USER%", userProfile, StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%", userProfile, StringComparison.OrdinalIgnoreCase)
            .Replace("%MYDOCUMENTS%", myDocuments, StringComparison.OrdinalIgnoreCase);

        if (resolved == "~")
        {
            resolved = userProfile;
        }
        else if (resolved.StartsWith("~/") || resolved.StartsWith("~\\"))
        {
            resolved = Path.Combine(userProfile, resolved[2..]);
        }

        resolved = Environment.ExpandEnvironmentVariables(resolved);

        return resolved;
    }

    /// <summary>
    /// Resolves an SQLite connection string (e.g. "Data Source=%CURRENT_USER%\SemanticAgentBrokerData\broker.db"),
    /// expands path tokens, and ensures the target database directory exists on disk.
    /// </summary>
    public static string ResolveConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return string.Empty;

        const string dataSourceKey = "Data Source=";
        int dsIndex = connectionString.IndexOf(dataSourceKey, StringComparison.OrdinalIgnoreCase);
        if (dsIndex < 0)
        {
            return connectionString;
        }

        int pathStart = dsIndex + dataSourceKey.Length;

        // Skip leading whitespace after "Data Source="
        int current = pathStart;
        while (current < connectionString.Length && char.IsWhiteSpace(connectionString[current]))
        {
            current++;
        }

        if (current >= connectionString.Length)
        {
            return connectionString;
        }

        char firstChar = connectionString[current];
        int pathEnd;
        int semicolonIndex;
        bool isQuoted = firstChar == '"' || firstChar == '\'';

        if (isQuoted)
        {
            int closingQuote = connectionString.IndexOf(firstChar, current + 1);
            if (closingQuote >= 0)
            {
                pathEnd = closingQuote + 1;
                semicolonIndex = connectionString.IndexOf(';', pathEnd);
            }
            else
            {
                pathEnd = connectionString.Length;
                semicolonIndex = -1;
            }
        }
        else
        {
            semicolonIndex = connectionString.IndexOf(';', current);
            pathEnd = semicolonIndex >= 0 ? semicolonIndex : connectionString.Length;
        }

        string rawPath = connectionString[pathStart..pathEnd].Trim();
        string unquotedPath = isQuoted && rawPath.Length >= 2 && rawPath.StartsWith(firstChar) && rawPath.EndsWith(firstChar)
            ? rawPath[1..^1].Trim()
            : rawPath;

        // In-memory SQLite targets (e.g. ":memory:", "file::memory:?cache=shared", "file:memdb?mode=memory", or empty)
        // are SQLite internal designations and must not be treated as filesystem paths.
        if (string.IsNullOrEmpty(unquotedPath)
            || string.Equals(unquotedPath, ":memory:", StringComparison.OrdinalIgnoreCase)
            || unquotedPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        string resolvedPath = ResolvePath(unquotedPath);
        if (!Path.IsPathRooted(resolvedPath))
        {
            resolvedPath = Path.Combine(AppContext.BaseDirectory, resolvedPath);
        }

        EnsureFileDirectoryExists(resolvedPath);

        string finalPath = isQuoted ? $"{firstChar}{resolvedPath}{firstChar}" : resolvedPath;
        string remainder = semicolonIndex >= 0 ? connectionString[semicolonIndex..] : string.Empty;
        return $"{connectionString[..pathStart]}{finalPath}{remainder}";
    }

    /// <summary>
    /// Ensures that the directory containing the specified file exists.
    /// </summary>
    public static void EnsureFileDirectoryExists(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch
        {
            // Ignore directory creation errors during initialization probes
        }
    }

    /// <summary>
    /// Ensures that the specified directory exists on disk.
    /// </summary>
    public static void EnsureDirectoryExists(string dirPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(dirPath) && !Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
            }
        }
        catch
        {
            // Ignore directory creation errors during initialization probes
        }
    }
}
