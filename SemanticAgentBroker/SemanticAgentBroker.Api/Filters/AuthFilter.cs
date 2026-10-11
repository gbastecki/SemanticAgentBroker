using SemanticAgentBroker.Api.Features.Auth;

namespace SemanticAgentBroker.Api.Filters;

public class AuthFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var authService = context.HttpContext.RequestServices.GetRequiredService<AuthService>();
        
        var authHeader = context.HttpContext.Request.Headers["Authorization"].ToString();
        var browserId = context.HttpContext.Request.Headers["X-Browser-Id"].ToString();
        var ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Unauthorized();
        }

        var token = authHeader.Substring("Bearer ".Length).Trim();
        var session = await authService.ValidateTokenAsync(token, ip, browserId);

        if (session == null)
        {
            return Results.Unauthorized();
        }

        context.HttpContext.Items["SessionInfo"] = session;

        return await next(context);
    }
}
