using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SemanticAgentBroker.Core.LoadBalancing;
using SemanticAgentBroker.Shared.Configuration;
using System.Net;

namespace SemanticAgentBroker.UnitTests.Core.LoadBalancing;

[TestClass]
public class EndpointHealthCheckServiceTests
{
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
    public void Constructor_InitializesEnabledConnectionsAsUnhealthy_PessimisticStartup()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection { Id = "conn-1", IsEnabled = true, Endpoint = "http://localhost:11434" },
                new ProviderConnection { Id = "conn-2", IsEnabled = false, Endpoint = "http://localhost:11435" }
            ]
        };

        var httpFactoryMock = new Mock<IHttpClientFactory>();

        // Act
        var service = new EndpointHealthCheckService(options, httpFactoryMock.Object, NullLogger<EndpointHealthCheckService>.Instance);

        // Assert
        Assert.IsFalse(service.IsHealthy("conn-1"));
        // conn-2 is disabled so it returns true as default (not tracked in pessimistic startup list)
        Assert.IsTrue(service.IsHealthy("conn-2"));
    }

    [TestMethod]
    public async Task StartAsync_WhenHealthChecksDisabled_ExitsCleanly()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            HealthCheck = new HealthCheckOptions { Enabled = false },
            Connections =
            [
                new ProviderConnection { Id = "conn-1", IsEnabled = true, Endpoint = "http://localhost:11434" }
            ]
        };

        var httpFactoryMock = new Mock<IHttpClientFactory>();
        var service = new EndpointHealthCheckService(options, httpFactoryMock.Object, NullLogger<EndpointHealthCheckService>.Instance);

        using var cts = new CancellationTokenSource();

        // Act
        await service.StartAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);

        // Assert - When health checks are disabled, endpoints must be considered healthy
        Assert.IsTrue(service.IsHealthy("conn-1"));
    }

    [TestMethod]
    public async Task StartAsync_WhenEndpointReturns200_TransitionsToHealthy()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            HealthCheck = new HealthCheckOptions { Enabled = true, IntervalSeconds = 10, TimeoutSeconds = 5 },
            Connections =
            [
                new ProviderConnection { Id = "conn-1", ProviderType = "vLLM", IsEnabled = true, Endpoint = "http://localhost:8000" }
            ]
        };

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };

        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient("conn-1")).Returns(client);

        var service = new EndpointHealthCheckService(options, httpFactoryMock.Object, NullLogger<EndpointHealthCheckService>.Instance);

        using var cts = new CancellationTokenSource();

        // Act
        await service.StartAsync(cts.Token);

        // Allow initial health check to complete
        await Task.Delay(150);

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert - conn-1 should now be healthy
        Assert.IsTrue(service.IsHealthy("conn-1"));
    }

    [TestMethod]
    public async Task StartAsync_WhenEndpointReturns500_RemainsUnhealthy()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            HealthCheck = new HealthCheckOptions { Enabled = true, IntervalSeconds = 10, TimeoutSeconds = 5 },
            Connections =
            [
                new ProviderConnection { Id = "conn-fail", ProviderType = "vLLM", IsEnabled = true, Endpoint = "http://localhost:8000" }
            ]
        };

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };

        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient("conn-fail")).Returns(client);

        var service = new EndpointHealthCheckService(options, httpFactoryMock.Object, NullLogger<EndpointHealthCheckService>.Instance);

        using var cts = new CancellationTokenSource();

        // Act
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert - conn-fail should remain unhealthy
        Assert.IsFalse(service.IsHealthy("conn-fail"));
    }

    [TestMethod]
    public async Task StartAsync_WhenOllamaEndpointConnects_ConfiguresContextAndUnloadsOldRunner()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            HealthCheck = new HealthCheckOptions { Enabled = true, IntervalSeconds = 10, TimeoutSeconds = 5 },
            Connections =
            [
                new ProviderConnection
                {
                    Id = "ollama-local",
                    ProviderType = "Ollama",
                    IsEnabled = true,
                    Endpoint = "http://localhost:11434",
                    Models = [new ModelConfig { Id = "qwen2.5-coder:7b", MaxContextTokens = 16384 }]
                }
            ]
        };

        var requestsMade = new List<string>();
        var handler = new MockHttpMessageHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            requestsMade.Add(path);

            if (path == "/") // Health check probe
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (path == "/api/show")
            {
                // Return json showing mismatched / old context (e.g. 4096)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"parameters\":\"num_ctx 4096\"}")
                };
            }
            if (path == "/api/create")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (path == "/api/generate")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient("ollama-local")).Returns(client);

        var service = new EndpointHealthCheckService(options, httpFactoryMock.Object, NullLogger<EndpointHealthCheckService>.Instance);
        using var cts = new CancellationTokenSource();

        // Act
        await service.StartAsync(cts.Token);
        await Task.Delay(200);

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.IsTrue(service.IsHealthy("ollama-local"));
        CollectionAssert.Contains(requestsMade, "/");
        CollectionAssert.Contains(requestsMade, "/api/show");
        CollectionAssert.Contains(requestsMade, "/api/create");
        CollectionAssert.Contains(requestsMade, "/api/generate");
    }

    [TestMethod]
    public void IsHealthy_UnknownConnectionId_ReturnsTrueByDefault()
    {
        // Arrange
        var options = new AiProviderOptions();
        var httpFactoryMock = new Mock<IHttpClientFactory>();
        var service = new EndpointHealthCheckService(options, httpFactoryMock.Object, NullLogger<EndpointHealthCheckService>.Instance);

        // Act & Assert - Untracked connection IDs default to true
        Assert.IsTrue(service.IsHealthy("completely-unknown-connection-id"));
    }

    [TestMethod]
    public async Task StartAsync_WhenOllamaContextAlreadyMatches_SkipsModelCreateAndUnload()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            HealthCheck = new HealthCheckOptions { Enabled = true, IntervalSeconds = 10, TimeoutSeconds = 5 },
            Connections =
            [
                new ProviderConnection
                {
                    Id = "ollama-matching",
                    ProviderType = "Ollama",
                    IsEnabled = true,
                    Endpoint = "http://localhost:11434",
                    Models = [new ModelConfig { Id = "qwen2.5-coder:7b", MaxContextTokens = 16384 }]
                }
            ]
        };

        var requestsMade = new List<string>();
        var handler = new MockHttpMessageHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            requestsMade.Add(path);

            if (path == "/") // Health check probe
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (path == "/api/show")
            {
                // Return json showing context ALREADY matching 16384
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"parameters\":\"num_ctx 16384\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient("ollama-matching")).Returns(client);

        var service = new EndpointHealthCheckService(options, httpFactoryMock.Object, NullLogger<EndpointHealthCheckService>.Instance);
        using var cts = new CancellationTokenSource();

        // Act
        await service.StartAsync(cts.Token);
        await Task.Delay(200);

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.IsTrue(service.IsHealthy("ollama-matching"));
        CollectionAssert.Contains(requestsMade, "/");
        CollectionAssert.Contains(requestsMade, "/api/show");
        CollectionAssert.DoesNotContain(requestsMade, "/api/create");
        CollectionAssert.DoesNotContain(requestsMade, "/api/generate");
    }

    [TestMethod]
    public async Task StartAsync_WhenEndpointThrowsUnexpectedException_MarksUnhealthyAndDoesNotCrash()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            HealthCheck = new HealthCheckOptions { Enabled = true, IntervalSeconds = 10, TimeoutSeconds = 5 },
            Connections =
            [
                new ProviderConnection { Id = "conn-invalid-op", ProviderType = "vLLM", IsEnabled = true, Endpoint = "http://localhost:8000" }
            ]
        };

        // Throws InvalidOperationException (not HttpRequestException)
        var handler = new MockHttpMessageHandler(req => throw new InvalidOperationException("Invalid request URI or BaseAddress missing"));
        var client = new HttpClient(handler);

        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient("conn-invalid-op")).Returns(client);

        var service = new EndpointHealthCheckService(options, httpFactoryMock.Object, NullLogger<EndpointHealthCheckService>.Instance);

        using var cts = new CancellationTokenSource();

        // Act - StartAsync should not throw InvalidOperationException
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert - conn-invalid-op should remain unhealthy, and background service should NOT be faulted
        Assert.IsFalse(service.IsHealthy("conn-invalid-op"));
        Assert.IsTrue(service.ExecuteTask == null || !service.ExecuteTask.IsFaulted, 
            $"ExecuteTask faulted with: {service.ExecuteTask?.Exception?.GetBaseException().Message}");
    }

    [TestMethod]
    public async Task StartAsync_WhenOllamaInitFails_DoesNotMarkInitializedAndRetries()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            HealthCheck = new HealthCheckOptions { Enabled = true, IntervalSeconds = 1, TimeoutSeconds = 5 },
            Connections =
            [
                new ProviderConnection
                {
                    Id = "ollama-retry",
                    ProviderType = "Ollama",
                    IsEnabled = true,
                    Endpoint = "http://localhost:11434",
                    Models = [new ModelConfig { Id = "test-model:latest", MaxContextTokens = 8192 }]
                }
            ]
        };

        int healthCheckCalls = 0;
        int showCallCount = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/")
            {
                healthCheckCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (req.RequestUri.AbsolutePath == "/api/show")
            {
                showCallCount++;
                if (showCallCount == 1)
                {
                    // Fail first attempt
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                // Succeed second attempt with context size
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"model_info\":{\"llama.context_length\":8192}}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient("ollama-retry"))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:11434") });

        var service = new EndpointHealthCheckService(options, httpFactoryMock.Object, NullLogger<EndpointHealthCheckService>.Instance);
        using var cts = new CancellationTokenSource();

        // Act
        await service.StartAsync(cts.Token);
        // Wait long enough for 2 health check cycles
        await Task.Delay(2500);

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert - /api/show should have been called more than once (retried on next cycle)
        Assert.IsTrue(healthCheckCalls >= 2, $"Health check GET / was called {healthCheckCalls} times");
        Assert.IsTrue(showCallCount >= 2, $"Expected at least 2 calls to /api/show, but was {showCallCount}");
    }

    [TestMethod]
    public async Task StartAsync_WhenOllamaEndpointBecomesUnhealthyThenHealthyAgain_ReInitializesModelContext()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            HealthCheck = new HealthCheckOptions { Enabled = true, IntervalSeconds = 1, TimeoutSeconds = 5 },
            Connections =
            [
                new ProviderConnection
                {
                    Id = "ollama-reconnect",
                    ProviderType = "Ollama",
                    IsEnabled = true,
                    Endpoint = "http://localhost:11434",
                    Models = [new ModelConfig { Id = "test-model:latest", MaxContextTokens = 8192 }]
                }
            ]
        };

        int cycle = 0;
        int showCallCount = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/")
            {
                cycle++;
                if (cycle == 2)
                {
                    // Unhealthy cycle
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (req.RequestUri.AbsolutePath == "/api/show")
            {
                showCallCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"model_info\":{\"llama.context_length\":8192}}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient("ollama-reconnect"))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:11434") });

        var service = new EndpointHealthCheckService(options, httpFactoryMock.Object, NullLogger<EndpointHealthCheckService>.Instance);
        using var cts = new CancellationTokenSource();

        // Act
        await service.StartAsync(cts.Token);
        // Wait long enough for 3 cycles (1: healthy, 2: unhealthy, 3: healthy again)
        await Task.Delay(3500);

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert - /api/show should have been called at least twice (cycle 1, and re-initialized in cycle 3)
        Assert.IsTrue(showCallCount >= 2, $"Expected at least 2 calls to /api/show across recovery, but was {showCallCount}");
    }

    [TestMethod]
    public void IsHealthy_WhenHealthChecksDisabled_ReturnsTrueForAllConnections()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            HealthCheck = new HealthCheckOptions { Enabled = false },
            Connections =
            [
                new ProviderConnection { Id = "conn-1", IsEnabled = true, Endpoint = "http://localhost:11434" },
                new ProviderConnection { Id = "conn-2", IsEnabled = true, Endpoint = "http://localhost:11435" }
            ]
        };

        var httpFactoryMock = new Mock<IHttpClientFactory>();
        var service = new EndpointHealthCheckService(options, httpFactoryMock.Object, NullLogger<EndpointHealthCheckService>.Instance);

        // Act & Assert
        Assert.IsTrue(service.IsHealthy("conn-1"));
        Assert.IsTrue(service.IsHealthy("conn-2"));
        Assert.IsTrue(service.IsHealthy("unknown-conn"));
    }

    [TestMethod]
    public async Task IsHealthy_IsCaseInsensitive()
    {
        // Arrange
        var options = new AiProviderOptions
        {
            HealthCheck = new HealthCheckOptions { Enabled = true, IntervalSeconds = 10, TimeoutSeconds = 5 },
            Connections =
            [
                new ProviderConnection { Id = "OpenAI-Primary", ProviderType = "OpenAiCompatible", IsEnabled = true, Endpoint = "http://localhost:8000" }
            ]
        };

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };

        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient("OpenAI-Primary")).Returns(client);

        var service = new EndpointHealthCheckService(options, httpFactoryMock.Object, NullLogger<EndpointHealthCheckService>.Instance);

        // Pessimistic startup check: should be false regardless of casing queried
        Assert.IsFalse(service.IsHealthy("OpenAI-Primary"));
        Assert.IsFalse(service.IsHealthy("openai-primary"));

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Once probed healthy, should be true regardless of casing queried
        Assert.IsTrue(service.IsHealthy("OpenAI-Primary"));
        Assert.IsTrue(service.IsHealthy("openai-primary"));
        Assert.IsTrue(service.IsHealthy("OPENAI-PRIMARY"));
    }
}
