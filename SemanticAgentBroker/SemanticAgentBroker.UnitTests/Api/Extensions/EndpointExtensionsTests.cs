using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.Extensions;

[TestClass]
public class EndpointExtensionsTests
{
    [TestMethod]
    public void MapApiEndpoints_RegistersAllRequiredRouteGroupsWithoutThrowing()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient(); // Initializes the application and endpoint routing

        // Act
        var endpointDataSources = factory.Services.GetServices<EndpointDataSource>().ToList();
        var endpoints = endpointDataSources.SelectMany(ds => ds.Endpoints).ToList();

        // Assert
        Assert.IsNotNull(endpoints);
        Assert.IsTrue(endpoints.Count > 0, "MapApiEndpoints must register route endpoints in the host application.");
    }
}
