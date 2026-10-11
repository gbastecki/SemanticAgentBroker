using Microsoft.Extensions.Logging.Abstractions;
using SemanticAgentBroker.Core.Infrastructure.Http;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Net;
using System.Text;

namespace SemanticAgentBroker.UnitTests.Core.Infrastructure;

public class MockHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.OK);
    public Exception? ExceptionToThrow { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }
        return Task.FromResult(ResponseToReturn);
    }
}

[TestClass]
public class BasicAuthDelegatingHandlerTests
{
    [TestMethod]
    public async Task SendAsync_WithUsernameAndPassword_SetsBasicAuthorizationHeader()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        var handler = new BasicAuthDelegatingHandler("admin", "secret123")
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test");

        // Act
        await client.SendAsync(request);

        // Assert
        var sentAuth = mockHandler.LastRequest?.Headers.Authorization;
        Assert.IsNotNull(sentAuth);
        Assert.AreEqual("Basic", sentAuth.Scheme);
        string expectedBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret123"));
        Assert.AreEqual(expectedBase64, sentAuth.Parameter);
    }

    [TestMethod]
    public async Task SendAsync_WithEmptyUsername_DoesNotSetAuthorizationHeader()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        var handler = new BasicAuthDelegatingHandler("", "")
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test");

        // Act
        await client.SendAsync(request);

        // Assert
        Assert.IsNull(mockHandler.LastRequest?.Headers.Authorization);
    }
}

[TestClass]
public class OllamaCompatibilityDelegatingHandlerTests
{
    [TestMethod]
    public async Task SendAsync_InjectsNumCtxIntoPostRequestPayload()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        var handler = new OllamaCompatibilityDelegatingHandler()
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://ollama.local/api/chat")
        {
            Content = new StringContent("{\"model\": \"llama3\"}", Encoding.UTF8, "application/json")
        };

        // Act
        await client.SendAsync(request);

        // Assert
        var sentBody = await mockHandler.LastRequest!.Content!.ReadAsStringAsync();
        StringAssert.Contains(sentBody, "\"num_ctx\"");
        StringAssert.Contains(sentBody, "\"options\"");
    }

    [TestMethod]
    public async Task SendAsync_NormalizesNonStandardFinishReasonInResponse()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\": [{\"finish_reason\": \"tool_call\"}]}", Encoding.UTF8, "application/json")
            }
        };

        var handler = new OllamaCompatibilityDelegatingHandler()
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://ollama.local/api/chat")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        // Act
        var response = await client.SendAsync(request);

        // Assert
        var responseBody = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(responseBody, "\"finish_reason\":\"tool_calls\"");
        Assert.AreEqual(Encoding.UTF8.GetByteCount(responseBody), response.Content.Headers.ContentLength);
    }

    [TestMethod]
    public async Task OllamaCompatibility_WithSpecificModelMaxContextTokens_InjectsConfiguredNumCtx()
    {
        // Arrange
        var connection = new SemanticAgentBroker.Shared.Configuration.ProviderConnection
        {
            Id = "ollama-custom",
            ProviderType = "Ollama",
            Models =
            [
                new SemanticAgentBroker.Shared.Configuration.ModelConfig
                {
                    Id = "qwen2.5-coder:32b",
                    MaxContextTokens = 16384
                }
            ]
        };

        var mockHandler = new MockHttpMessageHandler();
        var handler = new OllamaCompatibilityDelegatingHandler(connection)
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://ollama.local/api/chat")
        {
            Content = new StringContent("{\"model\": \"qwen2.5-coder:32b\"}", Encoding.UTF8, "application/json")
        };

        // Act
        await client.SendAsync(request);

        // Assert
        var sentBody = await mockHandler.LastRequest!.Content!.ReadAsStringAsync();
        StringAssert.Contains(sentBody, "\"num_ctx\":16384");
    }

    [TestMethod]
    public async Task OllamaCompatibility_WithNonJsonBody_LeavesPayloadIntact()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        var handler = new OllamaCompatibilityDelegatingHandler()
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://ollama.local/api/plain")
        {
            Content = new StringContent("Plain text not json", Encoding.UTF8, "text/plain")
        };

        // Act
        await client.SendAsync(request);

        // Assert
        var sentBody = await mockHandler.LastRequest!.Content!.ReadAsStringAsync();
        Assert.AreEqual("Plain text not json", sentBody);
    }

    [TestMethod]
    public async Task OllamaCompatibility_WithUnknownFinishReason_NormalizesToStop()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\": [{\"finish_reason\": \"eos\"}]}", Encoding.UTF8, "application/json")
            }
        };

        var handler = new OllamaCompatibilityDelegatingHandler()
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://ollama.local/api/chat")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        // Act
        var response = await client.SendAsync(request);

        // Assert
        var responseBody = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(responseBody, "\"finish_reason\":\"stop\"");
    }

    [TestMethod]
    public async Task OllamaCompatibility_WithFunctionCallFinishReason_NormalizesToToolCalls()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\": [{\"finish_reason\": \"function_call\"}]}", Encoding.UTF8, "application/json")
            }
        };

        var handler = new OllamaCompatibilityDelegatingHandler()
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://ollama.local/api/chat")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        // Act
        var response = await client.SendAsync(request);

        // Assert
        var responseBody = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(responseBody, "\"finish_reason\":\"tool_calls\"");
    }
}

[TestClass]
public class TraceLoggingDelegatingHandlerTests
{
    [TestMethod]
    public async Task SendAsync_WithTraceEnabled_RedactsSensitiveHeadersAndWritesLogFile()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        var logDir = Path.Combine(workspaceFixture.WorkspacePath, "http_traces");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json")
            }
        };

        var handler = new TraceLoggingDelegatingHandler(logDir, "openai-provider", NullLogger<TraceLoggingDelegatingHandler>.Instance)
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Add("Authorization", "Bearer sk-super-secret-token");
        request.Headers.Add("X-Api-Key", "my-secret-key");
        request.Headers.Add("Custom-Header", "public-value");
        request.Content = new StringContent("{\"prompt\":\"hello\"}", Encoding.UTF8, "application/json");

        // Act
        TraceLoggingContext.IsTraceEnabled = true;
        TraceLoggingContext.CurrentTaskId = workspaceFixture.SessionId;
        try
        {
            await client.SendAsync(request);
        }
        finally
        {
            TraceLoggingContext.IsTraceEnabled = false;
            TraceLoggingContext.CurrentTaskId = null;
        }

        // Assert
        Assert.IsTrue(Directory.Exists(logDir));
        var logFiles = Directory.GetFiles(logDir);
        Assert.AreEqual(1, logFiles.Length);

        var logContent = await File.ReadAllTextAsync(logFiles[0]);
        StringAssert.Contains(logContent, "Authorization: [REDACTED]");
        StringAssert.Contains(logContent, "X-Api-Key: [REDACTED]");
        StringAssert.Contains(logContent, "Custom-Header: public-value");
        StringAssert.DoesNotMatch(logContent, new System.Text.RegularExpressions.Regex("sk-super-secret-token"));
    }

    [TestMethod]
    public async Task SendAsync_WithTraceDisabled_DoesNotWriteLogFile()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        var logDir = Path.Combine(workspaceFixture.WorkspacePath, "http_traces");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json")
            }
        };
        var handler = new TraceLoggingDelegatingHandler(logDir, "provider-1", NullLogger<TraceLoggingDelegatingHandler>.Instance)
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent("{\"prompt\":\"hello\"}", Encoding.UTF8, "application/json")
        };

        // Act
        TraceLoggingContext.IsTraceEnabled = false;
        TraceLoggingContext.CurrentTaskId = workspaceFixture.SessionId;
        try
        {
            await client.SendAsync(request);
        }
        finally
        {
            TraceLoggingContext.CurrentTaskId = null;
        }

        // Assert
        Assert.IsTrue(!Directory.Exists(logDir) || Directory.GetFiles(logDir).Length == 0);
    }

    [TestMethod]
    public async Task SendAsync_WhenLogDirectoryIsInvalid_DoesNotFailHttpRequest()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        // Create a file blocking directory creation
        var invalidLogPath = Path.Combine(workspaceFixture.WorkspacePath, "blocking_file.txt");
        await File.WriteAllTextAsync(invalidLogPath, "data");

        var mockHandler = new MockHttpMessageHandler();
        var handler = new TraceLoggingDelegatingHandler(invalidLogPath, "provider-err", NullLogger<TraceLoggingDelegatingHandler>.Instance)
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test");

        // Act
        TraceLoggingContext.IsTraceEnabled = true;
        try
        {
            var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            TraceLoggingContext.IsTraceEnabled = false;
        }
    }

    [TestMethod]
    public async Task SendAsync_WhenResponseIsEventStream_PreservesStreamForCaller()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        var logDir = Path.Combine(workspaceFixture.WorkspacePath, "http_traces");

        var streamData = "data: {\"token\":\"hello\"}\n\ndata: [DONE]\n\n";
        var memStream = new MemoryStream(Encoding.UTF8.GetBytes(streamData));
        var streamContent = new StreamContent(memStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = streamContent
            }
        };

        var handler = new TraceLoggingDelegatingHandler(logDir, "sse-provider", NullLogger<TraceLoggingDelegatingHandler>.Instance)
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");

        // Act
        TraceLoggingContext.IsTraceEnabled = true;
        TraceLoggingContext.CurrentTaskId = workspaceFixture.SessionId;
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request);
        }
        finally
        {
            TraceLoggingContext.IsTraceEnabled = false;
            TraceLoggingContext.CurrentTaskId = null;
        }

        // Assert - The consumer must still be able to read the response stream
        var consumedContent = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(streamData, consumedContent);

        // Verify the trace log recorded the SSE stream placeholder rather than exhausting it
        var logFiles = Directory.GetFiles(logDir);
        Assert.AreEqual(1, logFiles.Length);
        var logText = await File.ReadAllTextAsync(logFiles[0]);
        StringAssert.Contains(logText, "SSE stream");
    }

    [TestMethod]
    public async Task SendAsync_WhenOperationCanceled_WritesCancellationMarkerAndRethrows()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        var logDir = Path.Combine(workspaceFixture.WorkspacePath, "http_traces");

        var mockHandler = new MockHttpMessageHandler
        {
            ExceptionToThrow = new OperationCanceledException("The operation was canceled by token.")
        };

        var handler = new TraceLoggingDelegatingHandler(logDir, "test-provider", NullLogger<TraceLoggingDelegatingHandler>.Instance)
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent("{\"prompt\":\"hello\"}", Encoding.UTF8, "application/json")
        };

        // Act & Assert
        TraceLoggingContext.IsTraceEnabled = true;
        TraceLoggingContext.CurrentTaskId = workspaceFixture.SessionId;
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await client.SendAsync(request);
            });
        }
        finally
        {
            TraceLoggingContext.IsTraceEnabled = false;
            TraceLoggingContext.CurrentTaskId = null;
        }

        var logFiles = Directory.GetFiles(logDir);
        Assert.AreEqual(1, logFiles.Length);
        var logContent = await File.ReadAllTextAsync(logFiles[0]);
        StringAssert.Contains(logContent, "=== CANCELLED / TIMED OUT ===");
        StringAssert.Contains(logContent, "The operation was canceled by token.");
    }

    [TestMethod]
    public async Task SendAsync_WhenExceptionThrown_WritesExceptionMarkerAndRethrows()
    {
        // Arrange
        using var workspaceFixture = new TestWorkspaceFixture();
        var logDir = Path.Combine(workspaceFixture.WorkspacePath, "http_traces");

        var mockHandler = new MockHttpMessageHandler
        {
            ExceptionToThrow = new HttpRequestException("Simulated connection reset")
        };

        var handler = new TraceLoggingDelegatingHandler(logDir, "test-provider", NullLogger<TraceLoggingDelegatingHandler>.Instance)
        {
            InnerHandler = mockHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent("{\"prompt\":\"hello\"}", Encoding.UTF8, "application/json")
        };

        // Act & Assert
        TraceLoggingContext.IsTraceEnabled = true;
        TraceLoggingContext.CurrentTaskId = workspaceFixture.SessionId;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                await client.SendAsync(request);
            });
        }
        finally
        {
            TraceLoggingContext.IsTraceEnabled = false;
            TraceLoggingContext.CurrentTaskId = null;
        }

        var logFiles = Directory.GetFiles(logDir);
        Assert.AreEqual(1, logFiles.Length);
        var logContent = await File.ReadAllTextAsync(logFiles[0]);
        StringAssert.Contains(logContent, "=== EXCEPTION ===");
        StringAssert.Contains(logContent, "Simulated connection reset");
    }
}
