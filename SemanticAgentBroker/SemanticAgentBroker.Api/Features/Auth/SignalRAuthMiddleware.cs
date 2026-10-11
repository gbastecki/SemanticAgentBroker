namespace SemanticAgentBroker.Api.Features.Auth;

public class SignalRAuthMiddleware
{
    private readonly RequestDelegate _next;

    public SignalRAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AuthService authService)
    {
        if (context.Request.Path.StartsWithSegments("/loghub"))
        {
            var token = context.Request.Query["access_token"].ToString();
            if (string.IsNullOrEmpty(token))
            {
                var authHeader = context.Request.Headers["Authorization"].ToString();
                if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    token = authHeader.Substring("Bearer ".Length).Trim();
                }
            }

            var browserId = context.Request.Query["browserId"].ToString();
            if (string.IsNullOrEmpty(browserId))
            {
                browserId = context.Request.Headers["X-Browser-Id"].ToString();
            }

            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var session = await authService.ValidateTokenAsync(token, ip, browserId);
            if (session == null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.Items["SessionInfo"] = session;
        }

        await _next(context);
    }
}

public static class SignalRAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseSignalRAuth(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<SignalRAuthMiddleware>();
    }
}
