using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Shared.Configuration;

[TestClass]
public class PathResolutionHelperTests
{
    [TestMethod]
    public void ResolvePath_WithNullOrEmpty_ReturnsEmptyString()
    {
        Assert.AreEqual(string.Empty, PathResolutionHelper.ResolvePath(null));
        Assert.AreEqual(string.Empty, PathResolutionHelper.ResolvePath(""));
        Assert.AreEqual(string.Empty, PathResolutionHelper.ResolvePath("   "));
    }

    [TestMethod]
    public void ResolvePath_ExpandsUserProfileAndCurrentUserTokens()
    {
        // Arrange
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string pathWithToken = @"%CURRENT_USER%\test\file.txt";

        // Act
        string resolved = PathResolutionHelper.ResolvePath(pathWithToken);

        // Assert
        Assert.IsFalse(resolved.Contains("%CURRENT_USER%"));
        Assert.IsTrue(resolved.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ResolvePath_ExpandsTildePrefix()
    {
        // Arrange
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string pathWithTilde = "~/subfolder/file.json";

        // Act
        string resolved = PathResolutionHelper.ResolvePath(pathWithTilde);

        // Assert
        Assert.IsFalse(resolved.StartsWith("~"));
        Assert.IsTrue(resolved.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ResolvePath_ExpandsMyDocumentsAndBackslashTildeTokens()
    {
        // Arrange
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string pathWithMyDocs = @"%MYDOCUMENTS%\reports\summary.txt";
        string pathWithBackslashTilde = @"~\projects\workspace";

        // Act
        string resolvedDocs = PathResolutionHelper.ResolvePath(pathWithMyDocs);
        string resolvedTilde = PathResolutionHelper.ResolvePath(pathWithBackslashTilde);

        // Assert
        Assert.IsFalse(resolvedDocs.Contains("%MYDOCUMENTS%"));
        Assert.IsTrue(resolvedDocs.StartsWith(myDocuments, StringComparison.OrdinalIgnoreCase));

        Assert.IsFalse(resolvedTilde.StartsWith("~"));
        Assert.IsTrue(resolvedTilde.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ResolveConnectionString_ExpandsDataSourceTokens()
    {
        // Arrange
        string connStr = @"Data Source=%CURRENT_USER%\SemanticAgentBrokerDataTests\broker.db;Cache=Shared";
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Act
        string resolved = PathResolutionHelper.ResolveConnectionString(connStr);

        // Assert
        Assert.IsFalse(resolved.Contains("%CURRENT_USER%"));
        StringAssert.StartsWith(resolved, "Data Source=");
        StringAssert.Contains(resolved, userProfile);
        StringAssert.EndsWith(resolved, ";Cache=Shared");
    }

    [TestMethod]
    public void EnsureDirectoryExists_CreatesFolderSafely()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "test_sab_" + Guid.NewGuid().ToString("N"));

        try
        {
            // Act
            PathResolutionHelper.EnsureDirectoryExists(tempDir);

            // Assert
            Assert.IsTrue(Directory.Exists(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir);
        }
    }

    [TestMethod]
    public void EnsureFileDirectoryExists_CreatesParentFolderTreeForFile()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "test_sab_filedir_" + Guid.NewGuid().ToString("N"), "nested", "sub");
        var filePath = Path.Combine(tempDir, "database.db");

        try
        {
            // Act
            PathResolutionHelper.EnsureFileDirectoryExists(filePath);

            // Assert
            Assert.IsTrue(Directory.Exists(tempDir));
        }
        finally
        {
            var topLevel = Path.Combine(Path.GetTempPath(), Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(tempDir))!));
            if (Directory.Exists(topLevel))
            {
                try { Directory.Delete(topLevel, true); } catch { }
            }
        }
    }

    [TestMethod]
    public void ResolveConnectionString_WithRelativePath_ResolvesAgainstBaseDirectory()
    {
        // Arrange
        string connStr = "Data Source=relative/path/to/test.db;Mode=ReadWriteCreate";

        // Act
        string resolved = PathResolutionHelper.ResolveConnectionString(connStr);

        // Assert
        StringAssert.StartsWith(resolved, "Data Source=");
        StringAssert.Contains(resolved, AppContext.BaseDirectory);
        StringAssert.EndsWith(resolved, ";Mode=ReadWriteCreate");
    }

    [TestMethod]
    public void ResolveConnectionString_WithNullOrNoDataSource_ReturnsOriginal()
    {
        Assert.AreEqual(string.Empty, PathResolutionHelper.ResolveConnectionString(null));
        Assert.AreEqual(string.Empty, PathResolutionHelper.ResolveConnectionString("   "));
        Assert.AreEqual("Server=myServer;Database=myDb;", PathResolutionHelper.ResolveConnectionString("Server=myServer;Database=myDb;"));
    }

    [TestMethod]
    public void ResolveConnectionString_WithQuotedPathContainingSpaces_ResolvesCorrectlyWithoutDuplicatingRoot()
    {
        // Arrange: Quoted absolute path
        string rootDir = Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\";
        string absolutePathWithSpaces = Path.Combine(rootDir, "Program Files", "App Data", "test.db");
        string connStr = $"Data Source=\"{absolutePathWithSpaces}\";Mode=ReadWrite";

        // Act
        string resolved = PathResolutionHelper.ResolveConnectionString(connStr);

        // Assert - AppContext.BaseDirectory should NOT be prepended to an absolute path
        Assert.IsFalse(resolved.Contains(AppContext.BaseDirectory), $"Expected resolved path not to contain BaseDirectory, but got: {resolved}");
        StringAssert.StartsWith(resolved, "Data Source=");
        StringAssert.Contains(resolved, absolutePathWithSpaces);
        StringAssert.EndsWith(resolved, ";Mode=ReadWrite");
    }

    [TestMethod]
    public void ResolvePath_WithSingleTilde_ExpandsToUserProfile()
    {
        // Arrange
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Act
        string resolved = PathResolutionHelper.ResolvePath("~");

        // Assert
        Assert.AreEqual(userProfile, resolved);
    }

    [TestMethod]
    public void ResolveConnectionString_WithQuotedPathContainingSemicolon_ResolvesCorrectly()
    {
        // Arrange
        string rootDir = Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\";
        string pathWithSemicolon = Path.Combine(rootDir, "folder;with;semicolons", "test.db");
        string connStr = $"Data Source=\"{pathWithSemicolon}\";Mode=ReadWrite";

        // Act
        string resolved = PathResolutionHelper.ResolveConnectionString(connStr);

        // Assert
        Assert.IsFalse(resolved.Contains(AppContext.BaseDirectory));
        StringAssert.StartsWith(resolved, "Data Source=");
        StringAssert.Contains(resolved, $"\"{pathWithSemicolon}\"");
        StringAssert.EndsWith(resolved, ";Mode=ReadWrite");
    }

    [TestMethod]
    [DataRow("Data Source=:memory:")]
    [DataRow("Data Source=:memory:;Mode=Memory;Cache=Shared")]
    [DataRow("Data Source=file::memory:?cache=shared")]
    [DataRow("Data Source=file:memdb1?mode=memory&cache=shared")]
    [DataRow("Data Source=\"\"")]
    [DataRow("Data Source=;Mode=Memory")]
    public void ResolveConnectionString_WithInMemorySqlite_ReturnsUnmodifiedConnectionString(string inMemoryConnStr)
    {
        // Act
        string resolved = PathResolutionHelper.ResolveConnectionString(inMemoryConnStr);

        // Assert - in-memory SQLite targets must never be treated as relative filesystem paths
        Assert.AreEqual(inMemoryConnStr, resolved);
    }
}
