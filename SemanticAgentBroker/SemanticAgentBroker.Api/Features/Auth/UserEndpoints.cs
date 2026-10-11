using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Api.Features.Auth;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users");

        // Helper filter inside to check for admin manually when policies are not used
        group.AddEndpointFilter(async (context, next) =>
        {
            var session = context.HttpContext.Items["SessionInfo"] as SessionInfo;
            if (session == null || session.Role != "Admin") return Results.Forbid();
            return await next(context);
        });

        group.MapGet("/", async (AuthDbContext db) =>
        {
            var users = await db.Users
                .Include(u => u.TaskPermissions)
                .Include(u => u.Groups)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.Role,
                    Permissions = u.TaskPermissions.Select(p => new
                    {
                        p.Id,
                        p.PluginId,
                        p.PluginTaskId,
                        p.AllowedOptions
                    }),
                    GroupIds = u.Groups.Select(g => g.Id).ToList()
                })
                .ToListAsync();
            return Results.Ok(users);
        });

        group.MapPost("/", async ([FromBody] CreateUserRequest req, AuthDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username))
                return Results.BadRequest("Username cannot be empty.");
            if (string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest("Password cannot be empty.");
            if (await db.Users.AnyAsync(u => u.Username.ToLower() == req.Username.ToLower()))
                return Results.BadRequest("Username already exists.");

            var passwordHasher = new Microsoft.AspNetCore.Identity.PasswordHasher<User>();
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = req.Username,
                Role = req.Role,
                TaskPermissions = req.Permissions.Select(p => new UserTaskPermission
                {
                    Id = Guid.NewGuid(),
                    PluginId = p.PluginId,
                    PluginTaskId = p.PluginTaskId,
                    AllowedOptions = p.AllowedOptions
                }).ToList()
            };

            user.PasswordHash = passwordHasher.HashPassword(user, req.Password);

            if (req.GroupIds != null && req.GroupIds.Any())
            {
                var groups = await db.UserGroups.Where(g => req.GroupIds.Contains(g.Id)).ToListAsync();
                user.Groups = groups;
            }

            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        group.MapPut("/{id:guid}", async (Guid id, [FromBody] UpdateUserRequest req, AuthDbContext db, AuthService authService, SemanticAgentBroker.Shared.Configuration.AgentSystemOptions systemOptions) =>
        {
            var user = await db.Users
                .Include(u => u.TaskPermissions)
                .Include(u => u.Groups)
                .FirstOrDefaultAsync(u => u.Id == id);
            if (user == null) return Results.NotFound();

            if (string.Equals(user.Username, systemOptions.DefaultAdminUsername, StringComparison.OrdinalIgnoreCase) && req.Role != "Admin")
            {
                return Results.BadRequest(new { Error = "Cannot remove Admin role from primary admin user." });
            }

            if (!string.IsNullOrEmpty(req.Password))
            {
                var passwordHasher = new Microsoft.AspNetCore.Identity.PasswordHasher<User>();
                user.PasswordHash = passwordHasher.HashPassword(user, req.Password);
            }
            
            user.Role = req.Role;

            // Update permissions
            var oldPermissions = await db.UserTaskPermissions.Where(p => p.UserId == id).ToListAsync();
            db.UserTaskPermissions.RemoveRange(oldPermissions);

            var newPermissions = req.Permissions.Select(p => new UserTaskPermission
            {
                Id = Guid.NewGuid(),
                UserId = id,
                PluginId = p.PluginId,
                PluginTaskId = p.PluginTaskId,
                AllowedOptions = p.AllowedOptions
            }).ToList();
            
            db.UserTaskPermissions.AddRange(newPermissions);

            // Update groups
            user.Groups.Clear();
            if (req.GroupIds != null && req.GroupIds.Any())
            {
                var groups = await db.UserGroups.Where(g => req.GroupIds.Contains(g.Id)).ToListAsync();
                user.Groups.AddRange(groups);
            }

            await db.SaveChangesAsync();
            await authService.RevokeSessionsForUserAsync(id);
            return Results.Ok();
        });

        group.MapDelete("/{id:guid}", async (Guid id, AuthDbContext db, AuthService authService, SemanticAgentBroker.Shared.Configuration.AgentSystemOptions systemOptions) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user == null) return Results.NotFound();
            if (string.Equals(user.Username, systemOptions.DefaultAdminUsername, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { Error = "Cannot delete primary admin." });

            db.Users.Remove(user);
            await db.SaveChangesAsync();
            await authService.RevokeSessionsForUserAsync(id);
            return Results.Ok();
        });
    }
}

public class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = "User";
    public List<PermissionDto> Permissions { get; set; } = new();
    public List<Guid> GroupIds { get; set; } = new();
}

public class UpdateUserRequest
{
    public string? Password { get; set; }
    public string Role { get; set; } = "User";
    public List<PermissionDto> Permissions { get; set; } = new();
    public List<Guid> GroupIds { get; set; } = new();
}

public class PermissionDto
{
    public string PluginId { get; set; } = string.Empty;
    public string PluginTaskId { get; set; } = string.Empty;
    public List<string> AllowedOptions { get; set; } = new();
}
