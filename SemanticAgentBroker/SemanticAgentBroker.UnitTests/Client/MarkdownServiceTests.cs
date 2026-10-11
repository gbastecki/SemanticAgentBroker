using SemanticAgentBroker.Client.Features.Markdown.Implementations;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class MarkdownServiceTests
{
    private MarkdownService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _service = new MarkdownService();
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void ToHtml_NullOrWhitespace_ReturnsEmptyString(string? input)
    {
        // Act
        var html = _service.ToHtml(input);

        // Assert
        Assert.AreEqual(string.Empty, html);
    }

    [TestMethod]
    public void ToHtml_DisablesRawHtml_PreventsXss()
    {
        // Arrange
        string maliciousMarkdown = "Hello <script>alert('xss')</script> <img src=x onerror=alert(1) />";

        // Act
        var html = _service.ToHtml(maliciousMarkdown);

        // Assert - Raw html tags must be disabled / escaped into text or entities
        Assert.IsFalse(html.Contains("<script>"));
        Assert.IsFalse(html.Contains("<img src="));
        StringAssert.Contains(html, "&lt;script&gt;alert('xss')&lt;/script&gt;");
    }

    [TestMethod]
    public void ToHtml_WithMarkdownElements_RendersExpectedHtml()
    {
        // Arrange
        string markdown = """
            # Header 1
            
            This is **bold** and *italic* text.
            
            - [x] Completed task item
            - [ ] Pending task item
            
            Check https://example.com/ for details.
            
            ```csharp
            var x = 42;
            ```
            """;

        // Act
        var html = _service.ToHtml(markdown);

        // Assert
        StringAssert.Contains(html, "Header 1</h1>");
        StringAssert.Contains(html, "<strong>bold</strong>");
        StringAssert.Contains(html, "<em>italic</em>");
        StringAssert.Contains(html, "href=\"https://example.com/\"");
        StringAssert.Contains(html, "<pre><code class=\"language-csharp\">");
        StringAssert.Contains(html, "var x = 42;");
    }

    [TestMethod]
    public async Task ToHtmlAsync_LargeMarkdown_YieldsAndRendersCorrectly()
    {
        // Arrange - Large document > 16384 characters
        string largeContent = "# Big Document\n\n" + string.Join("\n\n", Enumerable.Range(0, 1000).Select(i => $"Paragraph {i}: This is a long detailed section with some **bold** text and `code`."));
        Assert.IsTrue(largeContent.Length >= 16384);

        // Act
        var html = await _service.ToHtmlAsync(largeContent, CancellationToken.None);

        // Assert
        Assert.IsNotNull(html);
        StringAssert.Contains(html, "Big Document</h1>");
        StringAssert.Contains(html, "<p>Paragraph 0: This is a long detailed section");
    }

    [TestMethod]
    public async Task ToHtmlAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        string largeContent = new string('x', 20000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await _service.ToHtmlAsync(largeContent, cts.Token);
        });
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public async Task ToHtmlAsync_NullOrWhitespace_ReturnsEmptyString(string? input)
    {
        // Act
        var html = await _service.ToHtmlAsync(input, CancellationToken.None);

        // Assert
        Assert.AreEqual(string.Empty, html);
    }

    [TestMethod]
    public void ToHtml_WithHtmlTableMarkdown_RendersTableCorrectly()
    {
        // Arrange
        string tableMarkdown = """
            | Item | Quantity | Price |
            | :--- | :------: | ----: |
            | Apple | 10 | $1.50 |
            | Orange | 5 | $2.00 |
            """;

        // Act
        var html = _service.ToHtml(tableMarkdown);

        // Assert
        StringAssert.Contains(html, "<table>");
        StringAssert.Contains(html, "Item</th>");
        StringAssert.Contains(html, "Apple</td>");
        StringAssert.Contains(html, "$1.50</td>");
    }

    [TestMethod]
    public async Task ToHtmlAsync_SmallContent_RendersDirectlyWithoutDelay()
    {
        // Arrange
        string smallMarkdown = "Hello **world** :smile: with [link](https://example.com)";

        // Act
        var html = await _service.ToHtmlAsync(smallMarkdown, CancellationToken.None);

        // Assert
        StringAssert.Contains(html, "<strong>world</strong>");
        StringAssert.Contains(html, "href=\"https://example.com\"");
        StringAssert.Contains(html, "😄");
    }
}
