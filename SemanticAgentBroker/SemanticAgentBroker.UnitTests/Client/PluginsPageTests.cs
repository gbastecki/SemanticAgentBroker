using System.Reflection;
using PluginsPage = SemanticAgentBroker.Client.Pages.Plugins;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class PluginsPageTests
{
    [TestMethod]
    public void PluginsPage_ImplementsIAsyncDisposable()
    {
        // Assert
        Assert.IsTrue(typeof(IAsyncDisposable).IsAssignableFrom(typeof(PluginsPage)), "Plugins page must implement IAsyncDisposable for component lifecycle cleanup.");
    }

    [TestMethod]
    public async Task PluginsPage_DisposeAsync_SetsIsDisposedFlag()
    {
        // Arrange
        var pluginsPage = new PluginsPage();

        // Act
        if (pluginsPage is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }

        // Assert
        var disposedField = typeof(PluginsPage).GetField("_isDisposed", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(disposedField, "Plugins page must track _isDisposed flag for lifecycle safety.");
        var isDisposed = (bool)disposedField.GetValue(pluginsPage)!;
        Assert.IsTrue(isDisposed);
    }
}
