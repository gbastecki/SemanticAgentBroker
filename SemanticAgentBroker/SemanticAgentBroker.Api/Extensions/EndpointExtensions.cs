using SemanticAgentBroker.Api.Features.Plugins.Endpoints;
using SemanticAgentBroker.Api.Features.Agents.Endpoints;
using SemanticAgentBroker.Api.Features.Tasks.Endpoints;
using SemanticAgentBroker.Api.Features.Auth;

namespace SemanticAgentBroker.Api.Extensions;

public static class EndpointExtensions 
{
    public static void MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAuthEndpoints();

        var protectedGroup = app.MapGroup("").AddEndpointFilter<SemanticAgentBroker.Api.Filters.AuthFilter>();

        protectedGroup.MapPluginEndpoints();
        protectedGroup.MapAgentEndpoints();
        protectedGroup.MapTaskEndpoints();
        protectedGroup.MapWorkspaceEndpoints();
        protectedGroup.MapUserEndpoints();
        protectedGroup.MapGroupEndpoints();
    }
}
