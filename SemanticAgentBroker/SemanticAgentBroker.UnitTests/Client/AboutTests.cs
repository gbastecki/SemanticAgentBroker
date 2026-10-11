using SemanticAgentBroker.Client.Pages;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class AboutTests
{
    [TestMethod]
    public void About_DocumentsList_ContainsRequiredDocumentationFiles()
    {
        // Arrange
        var about = new About();
        var field = typeof(About).GetField("documents", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(field);

        var docs = field.GetValue(about) as System.Collections.IEnumerable;
        Assert.IsNotNull(docs);

        var docList = docs.Cast<object>().ToList();
        Assert.AreEqual(4, docList.Count);

        var fileNames = docList.Select(d => d.GetType().GetProperty("FileName")?.GetValue(d)?.ToString()).ToList();
        CollectionAssert.Contains(fileNames, "README.md");
        CollectionAssert.Contains(fileNames, "DOCUMENTATION.md");
        CollectionAssert.Contains(fileNames, "LICENSE");
        CollectionAssert.Contains(fileNames, "NOTICE");
    }

    [TestMethod]
    public async Task About_DisposeAsync_DisposesCleanly()
    {
        // Arrange
        var about = new About();

        // Act & Assert
        await about.DisposeAsync();

        var disposedField = typeof(About).GetField("_isDisposed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(disposedField, "About page must track _isDisposed flag for lifecycle safety.");
        var isDisposed = (bool)disposedField.GetValue(about)!;
        Assert.IsTrue(isDisposed);
    }
}
