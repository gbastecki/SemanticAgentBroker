namespace SemanticAgentBroker.UnitTests.TestInfrastructure;

[TestClass]
public class TestDatabaseFixtureTests
{
    [TestMethod]
    public void Dispose_AfterDatabaseOperations_CleansUpTestDirectoryWithoutLeaks()
    {
        string testDir;
        using (var fixture = new TestDatabaseFixture())
        {
            testDir = fixture.TestDirectory;
            using var db = fixture.CreateAppDbContext();
            db.Database.EnsureCreated();
        }

        Assert.IsFalse(Directory.Exists(testDir), $"Test directory {testDir} should be completely removed after Dispose.");
    }
}
