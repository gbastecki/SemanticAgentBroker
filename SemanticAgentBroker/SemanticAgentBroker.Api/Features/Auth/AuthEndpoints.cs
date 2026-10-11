using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Data;

namespace SemanticAgentBroker.Api.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", async (
            [FromBody] LoginRequest req,
            HttpContext context,
            AuthDbContext db,
            AuthService auth) =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            try
            {
                var token = await auth.LoginAsync(req.Username, req.Password, ip, req.BrowserId);
                var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
                return Results.Ok(new { Token = token, Role = user?.Role ?? "User", ServerInstanceId = AuthService.ServerInstanceId });
            }
            catch (UnauthorizedAccessException ex)
            {
                // Map exception messages to static, non-leaking responses
                var safeMessage = ex.Message switch
                {
                    var m when m.Contains("Too many failed attempts") => "Account temporarily locked. Try again later.",
                    _ => "Invalid username or password."
                };
                return Results.Json(new { Error = safeMessage }, statusCode: 401);
            }
        }).RequireRateLimiting("AuthPolicy");

        group.MapPost("/logout", async (
            HttpContext context,
            AuthService auth,
            [FromBody] LogoutRequest? bodyReq) =>
        {
            var authHeader = context.Request.Headers["Authorization"].ToString();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();

            var token = authHeader.Substring("Bearer ".Length).Trim();
            var browserId = context.Request.Headers["X-Browser-Id"].ToString();
            if (string.IsNullOrEmpty(browserId) && !string.IsNullOrEmpty(bodyReq?.BrowserId))
            {
                browserId = bodyReq.BrowserId;
            }
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (!await auth.LogoutByTokenAsync(token, ip, browserId))
                return Results.Unauthorized();

            return Results.Ok();
        });

        group.MapGet("/validate", async (
            HttpContext context,
            AuthService auth) =>
        {
            var authHeader = context.Request.Headers["Authorization"].ToString();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();

            var token = authHeader.Substring("Bearer ".Length).Trim();
            var browserId = context.Request.Headers["X-Browser-Id"].ToString();
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var session = await auth.ValidateTokenAsync(token, ip, browserId);
            if (session == null)
                return Results.Unauthorized();

            return Results.Ok(new { session.Username, session.Role, session.UserId, ServerInstanceId = AuthService.ServerInstanceId });
        }).RequireRateLimiting("AuthPolicy");
    }
}

public record LoginRequest(string Username, string Password, string BrowserId);
public record LogoutRequest(string? BrowserId = null);
