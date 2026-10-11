using Moq;
using SemanticAgentBroker.Plugins.BuiltIn.Optional;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Net;

namespace SemanticAgentBroker.UnitTests.Plugins;

[TestClass]
public class HttpClientAgentPluginTests
{
    private TestWorkspaceFixture _workspaceFixture = null!;
    private Mock<IPluginContext> _contextMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _workspaceFixture = new TestWorkspaceFixture();
        _contextMock = new Mock<IPluginContext>();
        _contextMock.Setup(c => c.WorkspacePath).Returns(_workspaceFixture.WorkspacePath);
        _contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string>());
    }

    [TestCleanup]
    public void Cleanup()
    {
        _workspaceFixture.Dispose();
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [TestMethod]
    public void HttpClientPlugin_Metadata_IsConfiguredCorrectly()
    {
        var plugin = new HttpClientAgentPlugin();
        Assert.AreEqual("HttpClient", plugin.Id);
        Assert.AreEqual(PluginCategory.BuiltInOptional, plugin.Category);
        Assert.AreEqual(2, plugin.GetMethods().Count);
    }

    [TestMethod]
    public async Task HttpGet_SuccessfulResponse_ReturnsStatusAndBody()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual(HttpMethod.Get, req.Method);
            Assert.AreEqual("https://api.example.com/data", req.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"success\"}", System.Text.Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(factoryMock.Object);

        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpGet");

        var args = new Dictionary<string, object?>
        {
            ["url"] = "https://api.example.com/data"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "HTTP 200 OK");
        StringAssert.Contains(result, "{\"status\":\"success\"}");
    }

    [TestMethod]
    public async Task HttpGet_WithCustomHeaders_AppliesHeaders()
    {
        // Arrange
        bool headerReceived = false;
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.Headers.TryGetValues("X-Api-Key", out var values) && values.Contains("secret-123"))
            {
                headerReceived = true;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
        });

        var client = new HttpClient(handler);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(factoryMock.Object);

        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpGet");

        var args = new Dictionary<string, object?>
        {
            ["url"] = "https://api.example.com/secure",
            ["headers"] = "{\"X-Api-Key\": \"secret-123\"}"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.IsTrue(headerReceived);
        StringAssert.Contains(result, "HTTP 200 OK");
    }

    [TestMethod]
    public async Task HttpGet_LargeResponse_TruncatesResponseBody()
    {
        // Arrange - configure MaxResponseChars to 50
        _contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string> { ["MaxResponseChars"] = "50" });

        var handler = new MockHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('Z', 200))
            });

        var client = new HttpClient(handler);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(factoryMock.Object);

        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpGet");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/big" }, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "[TRUNCATED - response was 200 chars]");
    }

    private class TrackingStream : MemoryStream
    {
        public long BytesRead { get; private set; }

        public TrackingStream(byte[] buffer) : base(buffer) { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
    }

    [TestMethod]
    public async Task HttpGet_LargeStream_StreamsBoundedWithoutBufferingEntireStream()
    {
        // Arrange - configure MaxResponseChars to 50
        _contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string> { ["MaxResponseChars"] = "50" });

        // Stream with 1,000,000 bytes
        var largeData = new byte[1_000_000];
        Array.Fill(largeData, (byte)'A');
        var trackingStream = new TrackingStream(largeData);

        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(trackingStream)
            };
        });

        var client = new HttpClient(handler);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(factoryMock.Object);

        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpGet");

        // Act
        var result = await method.ExecuteAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/huge-stream" }, _contextMock.Object, CancellationToken.None);

        // Assert - Should only read around maxChars (plus buffer), NOT the entire 1,000,000 bytes!
        StringAssert.Contains(result, "[TRUNCATED");
        Assert.IsTrue(trackingStream.BytesRead < 50_000, $"Expected bounded streaming but read {trackingStream.BytesRead} bytes from stream.");
    }

    [TestMethod]
    public async Task HttpPost_WithJsonBody_SendsBodyAndReturnsResponse()
    {
        // Arrange
        string receivedBody = string.Empty;
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual(HttpMethod.Post, req.Method);
            receivedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"id\": 101}")
            };
        });

        var client = new HttpClient(handler);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(factoryMock.Object);

        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpPost");

        var args = new Dictionary<string, object?>
        {
            ["url"] = "https://api.example.com/items",
            ["body"] = "{\"name\": \"Widget\"}",
            ["contentType"] = "application/json"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("{\"name\": \"Widget\"}", receivedBody);
        StringAssert.Contains(result, "HTTP 201 Created");
        StringAssert.Contains(result, "{\"id\": 101}");
    }

    [TestMethod]
    public async Task HttpPost_MissingFactory_ReturnsErrorMessage()
    {
        // Arrange
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns((IHttpClientFactory?)null);

        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpPost");

        // Act
        var result = await method.ExecuteAsync(
            new Dictionary<string, object?> { ["url"] = "https://example.com", ["body"] = "{}" },
            _contextMock.Object,
            CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "IHttpClientFactory is not available");
    }

    [TestMethod]
    public async Task HttpGet_MissingFactory_ReturnsErrorMessage()
    {
        // Arrange
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns((IHttpClientFactory?)null);

        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpGet");

        // Act
        var result = await method.ExecuteAsync(
            new Dictionary<string, object?> { ["url"] = "https://example.com" },
            _contextMock.Object,
            CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "IHttpClientFactory is not available");
    }

    [TestMethod]
    public async Task HttpGet_WhenNetworkFails_ReturnsErrorMessage()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(req => throw new HttpRequestException("Connection refused by remote host"));
        var client = new HttpClient(handler);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(factoryMock.Object);

        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpGet");

        // Act
        var result = await method.ExecuteAsync(
            new Dictionary<string, object?> { ["url"] = "https://down.example.com" },
            _contextMock.Object,
            CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "HTTP GET failed:");
        StringAssert.Contains(result, "Connection refused");
    }

    [TestMethod]
    public async Task HttpPost_WhenNetworkFails_ReturnsErrorMessage()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(req => throw new HttpRequestException("DNS resolution failed"));
        var client = new HttpClient(handler);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(factoryMock.Object);

        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpPost");

        // Act
        var result = await method.ExecuteAsync(
            new Dictionary<string, object?> { ["url"] = "https://invalid.example.com", ["body"] = "{}" },
            _contextMock.Object,
            CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "HTTP POST failed:");
        StringAssert.Contains(result, "DNS resolution failed");
    }

    [TestMethod]
    public void ApplyHeaders_WithInvalidJson_DoesNotThrowAndContinues()
    {
        // Arrange
        using var client = new HttpClient();

        // Act & Assert - Malformed JSON should not throw
        HttpGetMethod.ApplyHeaders(client, "{invalid json content");
        Assert.AreEqual(0, client.DefaultRequestHeaders.Count());
    }

    [TestMethod]
    public async Task HttpGet_MissingUrlArgument_ThrowsArgumentException()
    {
        // Arrange
        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpGet");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            method.ExecuteAsync(new Dictionary<string, object?>(), _contextMock.Object));
    }

    [TestMethod]
    public async Task HttpPost_MissingBodyArgument_ThrowsArgumentException()
    {
        // Arrange
        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpPost");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            method.ExecuteAsync(new Dictionary<string, object?> { ["url"] = "https://example.com" }, _contextMock.Object));
    }

    [TestMethod]
    [DataRow("http://127.0.0.1/admin")]
    [DataRow("http://localhost:8080/api")]
    [DataRow("http://169.254.169.254/latest/meta-data/")]
    [DataRow("http://10.0.0.1/internal")]
    [DataRow("http://192.168.1.1/router")]
    [DataRow("http://[::1]/secret")]
    public async Task HttpGet_PrivateOrLoopbackUrl_IsBlockedBySsrfProtection(string url)
    {
        // Arrange
        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpGet");

        var factoryMock = new Mock<IHttpClientFactory>();
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(factoryMock.Object);

        var sysOptions = new SemanticAgentBroker.Shared.Configuration.AgentSystemOptions
        {
            HttpClientSecurity = new SemanticAgentBroker.Shared.Configuration.HttpClientSecurityOptions
            {
                EnableBlocklist = true,
                EnableAllowlist = false
            }
        };
        _contextMock.Setup(c => c.GetService<SemanticAgentBroker.Shared.Configuration.AgentSystemOptions>()).Returns(sysOptions);

        var args = new Dictionary<string, object?>
        {
            ["url"] = url
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert - should be blocked without calling IHttpClientFactory.CreateClient
        StringAssert.Contains(result, "blocked");
        factoryMock.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task HttpGet_WithAllowlistEnabled_BlocksNonMatchingUrls()
    {
        // Arrange
        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpGet");

        var factoryMock = new Mock<IHttpClientFactory>();
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(factoryMock.Object);

        var sysOptions = new SemanticAgentBroker.Shared.Configuration.AgentSystemOptions
        {
            HttpClientSecurity = new SemanticAgentBroker.Shared.Configuration.HttpClientSecurityOptions
            {
                EnableBlocklist = true,
                EnableAllowlist = true,
                AllowedPatterns = ["https://api.github.com/*", "*.trusted.org"]
            }
        };
        _contextMock.Setup(c => c.GetService<SemanticAgentBroker.Shared.Configuration.AgentSystemOptions>()).Returns(sysOptions);

        var args = new Dictionary<string, object?>
        {
            ["url"] = "https://untrusted-site.com/data"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "blocked");
        factoryMock.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task HttpGet_WhenRedirectReceived_ReturnsLocationHeaderWithoutAutoFollowing()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://127.0.0.1:5100/internal-api");
            return response;
        });

        var client = new HttpClient(handler);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("HttpClientAgentPlugin")).Returns(client);
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(factoryMock.Object);

        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpGet");

        var args = new Dictionary<string, object?>
        {
            ["url"] = "https://example.com/redirect-me"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "HTTP 302");
        StringAssert.Contains(result, "Location: http://127.0.0.1:5100/internal-api");
        factoryMock.Verify(f => f.CreateClient("HttpClientAgentPlugin"), Times.Once);
    }

    [TestMethod]
    [DataRow("https://evil-attacker.com/?redirect=https://api.github.com/data")]
    [DataRow("https://evil-attacker.com/?search=trusted.org")]
    [DataRow("https://evil-attacker.com/api.stripe.com/data")]
    [DataRow("https://trusted.org.evil-attacker.com/data")]
    [DataRow("https://evil-trusted.org/data")]
    [DataRow("https://attacker.com/#api.github.com")]
    public async Task HttpGet_WithAllowlistEnabled_RejectsUrlWithAllowedPatternInQueryOrPathOrSubdomain(string attackerUrl)
    {
        // Arrange
        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpGet");

        var factoryMock = new Mock<IHttpClientFactory>();
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(factoryMock.Object);

        var sysOptions = new SemanticAgentBroker.Shared.Configuration.AgentSystemOptions
        {
            HttpClientSecurity = new SemanticAgentBroker.Shared.Configuration.HttpClientSecurityOptions
            {
                EnableBlocklist = true,
                EnableAllowlist = true,
                AllowedPatterns = ["https://api.github.com/*", "*.trusted.org", "api.stripe.com"]
            }
        };
        _contextMock.Setup(c => c.GetService<SemanticAgentBroker.Shared.Configuration.AgentSystemOptions>()).Returns(sysOptions);

        var args = new Dictionary<string, object?>
        {
            ["url"] = attackerUrl
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert - malicious URL containing allowed pattern as substring or query must be blocked
        StringAssert.Contains(result, "HTTP request blocked: URL");
        StringAssert.Contains(result, "is not in the allowed URLs list");
        factoryMock.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    [DataRow("https://api.github.com/repos/test/project")]
    [DataRow("https://sub.trusted.org/api/v1")]
    [DataRow("https://trusted.org/index.html")]
    [DataRow("https://api.stripe.com/v1/charges")]
    public async Task HttpGet_WithAllowlistEnabled_PermitsValidTargetUrls(string validUrl)
    {
        // Arrange
        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\"}", System.Text.Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("HttpClientAgentPlugin")).Returns(client);
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(factoryMock.Object);

        var plugin = new HttpClientAgentPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "HttpGet");

        var sysOptions = new SemanticAgentBroker.Shared.Configuration.AgentSystemOptions
        {
            HttpClientSecurity = new SemanticAgentBroker.Shared.Configuration.HttpClientSecurityOptions
            {
                EnableBlocklist = true,
                EnableAllowlist = true,
                AllowedPatterns = ["https://api.github.com/*", "*.trusted.org", "api.stripe.com"]
            }
        };
        _contextMock.Setup(c => c.GetService<SemanticAgentBroker.Shared.Configuration.AgentSystemOptions>()).Returns(sysOptions);

        var args = new Dictionary<string, object?>
        {
            ["url"] = validUrl
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "HTTP 200 OK");
        factoryMock.Verify(f => f.CreateClient("HttpClientAgentPlugin"), Times.Once);
    }
}

