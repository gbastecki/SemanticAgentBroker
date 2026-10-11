using SemanticAgentBroker.Client.Pages;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class ClientComponentModelsTests
{
    [TestMethod]
    public void About_DocInfo_RecordCreationAndDefaultValues()
    {
        // Arrange & Act
        var doc = new About.DocInfo(
            "readme",
            "README.md",
            "README.md",
            "description",
            "Project overview and quickstart guide.",
            true
        );

        // Assert
        Assert.AreEqual("readme", doc.Id);
        Assert.AreEqual("README.md", doc.Title);
        Assert.AreEqual("README.md", doc.FileName);
        Assert.AreEqual("description", doc.Icon);
        Assert.AreEqual("Project overview and quickstart guide.", doc.Description);
        Assert.IsTrue(doc.IsMarkdown);
    }

    [TestMethod]
    public void About_DocViewMode_EnumValues()
    {
        // Assert enum members
        var values = Enum.GetValues<About.DocViewMode>();
        Assert.AreEqual(2, values.Length);
        CollectionAssert.Contains(values, About.DocViewMode.Rendered);
        CollectionAssert.Contains(values, About.DocViewMode.Raw);
    }
}
