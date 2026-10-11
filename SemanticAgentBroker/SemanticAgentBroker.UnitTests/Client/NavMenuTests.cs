using SemanticAgentBroker.Client.Layout;
using System.Reflection;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class NavMenuTests
{
    [TestMethod]
    public void NavMenu_Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var navMenu = new NavMenu();

        // Act & Assert - Calling Dispose multiple times should be idempotent
        navMenu.Dispose();
        navMenu.Dispose();

        var disposedField = typeof(NavMenu).GetField("_isDisposed", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(disposedField, "NavMenu must track _isDisposed flag for lifecycle safety.");
        var isDisposed = (bool)disposedField.GetValue(navMenu)!;
        Assert.IsTrue(isDisposed);
    }
}
