using SemanticAgentBroker.Core.Infrastructure.Http;
using System.Net;
using System.Text;

namespace SemanticAgentBroker.UnitTests.Core.Infrastructure.Http;

[TestClass]
public class OllamaCompatibilityDelegatingHandlerTests
{
    private class InspectableStringContent : StringContent
    {
        public bool IsDisposed { get; private set; }

        public InspectableStringContent(string content, Encoding encoding, string mediaType) 
            : base(content, encoding, mediaType) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private class StubInnerHandler : HttpMessageHandler
    {
        public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.OK);
        public HttpRequestMessage? CapturedRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(ResponseToReturn);
        }
    }

    [TestMethod]
    public async Task SendAsync_WhenRequestModified_DisposesOldRequestContent()
    {
        // Arrange
        var innerHandler = new StubInnerHandler();
        var handler = new OllamaCompatibilityDelegatingHandler
        {
            InnerHandler = innerHandler
        };
        var client = new HttpClient(handler);

        var originalContent = new InspectableStringContent("{\"model\":\"gemma\"}", Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/v1/chat/completions")
        {
            Content = originalContent
        };

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.IsTrue(originalContent.IsDisposed, "Replaced request HttpContent must be disposed to avoid resource leaks.");
    }

    [TestMethod]
    public async Task SendAsync_WhenResponseModified_DisposesOldResponseContent()
    {
        // Arrange
        var oldResponseContent = new InspectableStringContent(
            "{\"choices\":[{\"finish_reason\":\"tool_call\"}]}", Encoding.UTF8, "application/json");

        var innerHandler = new StubInnerHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = oldResponseContent
            }
        };
        var handler = new OllamaCompatibilityDelegatingHandler
        {
            InnerHandler = innerHandler
        };
        var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/v1/chat/completions");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.IsTrue(oldResponseContent.IsDisposed, "Replaced response HttpContent must be disposed to avoid resource leaks.");
        var newBody = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(newBody.Contains("\"finish_reason\":\"tool_calls\""));
    }
}
