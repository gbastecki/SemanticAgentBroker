using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SemanticAgentBroker.Core.LoadBalancing;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Core.LoadBalancing;

[TestClass]
public class LeastUsedConnectionLoadBalancerTests
{
    private Mock<IEndpointHealthMonitor> _healthMonitorMock = null!;
    private AiProviderOptions _options = null!;

    [TestInitialize]
    public void Setup()
    {
        _healthMonitorMock = new Mock<IEndpointHealthMonitor>();

        _options = new AiProviderOptions
        {
            Connections =
            [
                new ProviderConnection
                {
                    Id = "conn-1",
                    EndpointName = "Endpoint 1",
                    IsEnabled = true,
                    Models = [new ModelConfig { Id = "gpt-4" }]
                },
                new ProviderConnection
                {
                    Id = "conn-2",
                    EndpointName = "Endpoint 2",
                    IsEnabled = true,
                    Models = [new ModelConfig { Id = "gpt-4" }]
                },
                new ProviderConnection
                {
                    Id = "conn-3-disabled",
                    EndpointName = "Endpoint 3",
                    IsEnabled = false,
                    Models = [new ModelConfig { Id = "gpt-4" }]
                }
            ]
        };

        _healthMonitorMock.Setup(h => h.IsHealthy(It.IsAny<string>())).Returns(true);
    }

    [TestMethod]
    public async Task SelectConnectionAsync_PicksConnectionWithLeastActiveRequests()
    {
        // Arrange
        var balancer = new LeastUsedConnectionLoadBalancer(_options, _healthMonitorMock.Object, NullLogger<LeastUsedConnectionLoadBalancer>.Instance);

        // Act 1: Select first connection -> conn-1 (or conn-2)
        var first = await balancer.SelectConnectionAsync("gpt-4");

        // Act 2: Select second connection -> should be the other connection
        var second = await balancer.SelectConnectionAsync("gpt-4");

        // Assert
        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(1, balancer.GetActiveRequestCount(first.Id));
        Assert.AreEqual(1, balancer.GetActiveRequestCount(second.Id));
    }

    [TestMethod]
    public async Task SelectConnectionAsync_FiltersOutUnhealthyEndpointsWhenHealthyExist()
    {
        // Arrange: Mark conn-1 as unhealthy
        _healthMonitorMock.Setup(h => h.IsHealthy("conn-1")).Returns(false);
        _healthMonitorMock.Setup(h => h.IsHealthy("conn-2")).Returns(true);

        var balancer = new LeastUsedConnectionLoadBalancer(_options, _healthMonitorMock.Object, NullLogger<LeastUsedConnectionLoadBalancer>.Instance);

        // Act
        var selected = await balancer.SelectConnectionAsync("gpt-4");

        // Assert
        Assert.AreEqual("conn-2", selected.Id);
    }

    [TestMethod]
    public async Task SelectConnectionAsync_FallsBackToUnhealthyWhenNoHealthyEndpointsExist()
    {
        // Arrange: Mark all connections as unhealthy
        _healthMonitorMock.Setup(h => h.IsHealthy(It.IsAny<string>())).Returns(false);

        var balancer = new LeastUsedConnectionLoadBalancer(_options, _healthMonitorMock.Object, NullLogger<LeastUsedConnectionLoadBalancer>.Instance);

        // Act
        var selected = await balancer.SelectConnectionAsync("gpt-4");

        // Assert - should successfully fall back without throwing
        Assert.IsTrue(selected.Id == "conn-1" || selected.Id == "conn-2");
    }

    [TestMethod]
    public async Task SelectConnectionAsync_ThrowsWhenNoConnectionSupportsModel()
    {
        // Arrange
        var balancer = new LeastUsedConnectionLoadBalancer(_options, _healthMonitorMock.Object, NullLogger<LeastUsedConnectionLoadBalancer>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            balancer.SelectConnectionAsync("non-existent-model"));
    }

    [TestMethod]
    public async Task ReleaseConnection_DecrementsActiveRequestCount()
    {
        // Arrange
        var balancer = new LeastUsedConnectionLoadBalancer(_options, _healthMonitorMock.Object, NullLogger<LeastUsedConnectionLoadBalancer>.Instance);

        var conn = await balancer.SelectConnectionAsync("gpt-4");
        Assert.AreEqual(1, balancer.GetActiveRequestCount(conn.Id));

        // Act
        balancer.ReleaseConnection(conn.Id);

        // Assert
        Assert.AreEqual(0, balancer.GetActiveRequestCount(conn.Id));
    }

    [TestMethod]
    public void ReleaseConnection_NeverGoesBelowZero()
    {
        // Arrange
        var balancer = new LeastUsedConnectionLoadBalancer(_options, _healthMonitorMock.Object, NullLogger<LeastUsedConnectionLoadBalancer>.Instance);

        // Act
        balancer.ReleaseConnection("conn-1");

        // Assert
        Assert.AreEqual(0, balancer.GetActiveRequestCount("conn-1"));
    }

    [TestMethod]
    public async Task SelectConnectionAsync_IgnoresDisabledConnections()
    {
        // Arrange: Only conn-3 has model "special-model", but conn-3 is disabled
        _options.Connections.First(c => c.Id == "conn-3-disabled").Models.Add(new ModelConfig { Id = "special-model" });

        var balancer = new LeastUsedConnectionLoadBalancer(_options, _healthMonitorMock.Object, NullLogger<LeastUsedConnectionLoadBalancer>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            balancer.SelectConnectionAsync("special-model"));
    }

    [TestMethod]
    public async Task SelectConnectionAsync_ConcurrentCalls_DistributeLoadEvenly()
    {
        // Arrange: 2 healthy endpoints, 10 concurrent selections
        var balancer = new LeastUsedConnectionLoadBalancer(_options, _healthMonitorMock.Object,
            NullLogger<LeastUsedConnectionLoadBalancer>.Instance);
        var tasks = Enumerable.Range(0, 10).Select(_ => balancer.SelectConnectionAsync("gpt-4")).ToArray();

        // Act
        var results = await Task.WhenAll(tasks);

        // Assert: With atomic selection, load should be distributed evenly (5 each)
        var conn1Count = results.Count(r => r.Id == "conn-1");
        var conn2Count = results.Count(r => r.Id == "conn-2");
        Assert.AreEqual(10, conn1Count + conn2Count);
        Assert.AreEqual(5, conn1Count);
        Assert.AreEqual(5, conn2Count);
    }

    [TestMethod]
    public void GetActiveRequestCount_UnknownConnection_ReturnsZero()
    {
        // Arrange
        var balancer = new LeastUsedConnectionLoadBalancer(_options, _healthMonitorMock.Object,
            NullLogger<LeastUsedConnectionLoadBalancer>.Instance);

        // Act & Assert
        Assert.AreEqual(0, balancer.GetActiveRequestCount("non-existent-connection-id"));
    }

    [TestMethod]
    public async Task SelectAndReleaseConnection_IsCaseInsensitive()
    {
        // Arrange
        var balancer = new LeastUsedConnectionLoadBalancer(_options, _healthMonitorMock.Object,
            NullLogger<LeastUsedConnectionLoadBalancer>.Instance);

        // Act 1: Select connection -> increments "conn-1"
        var selected = await balancer.SelectConnectionAsync("gpt-4");
        Assert.AreEqual("conn-1", selected.Id);

        // Assert 1: Querying with uppercase should return 1
        Assert.AreEqual(1, balancer.GetActiveRequestCount("CONN-1"));
        Assert.AreEqual(1, balancer.GetActiveRequestCount("conn-1"));

        // Act 2: Release using different casing
        balancer.ReleaseConnection("CONN-1");

        // Assert 2: Counter should be decremented to 0 regardless of queried casing
        Assert.AreEqual(0, balancer.GetActiveRequestCount("conn-1"));
        Assert.AreEqual(0, balancer.GetActiveRequestCount("CONN-1"));
    }
}
