using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Api.Features.Auth;

public static class GroupEndpoints
{
    public static void MapGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/groups");

        // Helper filter inside to check for admin manually when policies are not used
        group.AddEndpointFilter(async (context, next) =>
        {
            var session = context.HttpContext.Items["SessionInfo"] as SessionInfo;
            if (session == null || session.Role != "Admin") return Results.Forbid();
            return await next(context);
        });

        group.MapGet("/", async (AuthDbContext db) =>
        {
            var groups = await db.UserGroups
                .Include(g => g.TaskPermissions)
                .Select(g => new
                {
                    g.Id,
                    g.Name,
                    g.Description,
                    Permissions = g.TaskPermissions.Select(p => new
                    {
                        p.Id,
                        p.PluginId,
                        p.PluginTaskId,
                        p.AllowedOptions
                    })
                })
                .ToListAsync();
            return Results.Ok(groups);
        });

        group.MapPost("/", async ([FromBody] CreateGroupRequest req, AuthDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest("Group name cannot be empty.");

            if (await db.UserGroups.AnyAsync(g => g.Name.ToLower() == req.Name.ToLower()))
                return Results.BadRequest("Group name already exists.");

            var newGroup = new UserGroup
            {
                Id = Guid.NewGuid(),
                Name = req.Name,
                Description = req.Description,
                TaskPermissions = req.Permissions.Select(p => new UserGroupTaskPermission
                {
                    Id = Guid.NewGuid(),
                    PluginId = p.PluginId,
                    PluginTaskId = p.PluginTaskId,
                    AllowedOptions = p.AllowedOptions
                }).ToList()
            };

            db.UserGroups.Add(newGroup);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        group.MapPut("/{id:guid}", async (Guid id, [FromBody] UpdateGroupRequest req, AuthDbContext db, AuthService authService) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest("Group name cannot be empty.");

            var existingGroup = await db.UserGroups
                .Include(g => g.TaskPermissions)
                .Include(g => g.Users)
                .FirstOrDefaultAsync(g => g.Id == id);
            if (existingGroup == null) return Results.NotFound();

            if (!string.Equals(existingGroup.Name, req.Name, StringComparison.OrdinalIgnoreCase) && await db.UserGroups.AnyAsync(g => g.Name.ToLower() == req.Name.ToLower()))
                return Results.BadRequest("Group name already exists.");

            existingGroup.Name = req.Name;
            existingGroup.Description = req.Description;

            // Update permissions
            var oldPermissions = await db.UserGroupTaskPermissions.Where(p => p.UserGroupId == id).ToListAsync();
            db.UserGroupTaskPermissions.RemoveRange(oldPermissions);

            var newPermissions = req.Permissions.Select(p => new UserGroupTaskPermission
            {
                Id = Guid.NewGuid(),
                UserGroupId = id,
                PluginId = p.PluginId,
                PluginTaskId = p.PluginTaskId,
                AllowedOptions = p.AllowedOptions
            }).ToList();
            
            db.UserGroupTaskPermissions.AddRange(newPermissions);

            var affectedUserIds = existingGroup.Users.Select(u => u.Id).ToList();

            await db.SaveChangesAsync();

            // Revoke all active sessions for members of this group so they are forced to re-authenticate with updated permissions
            foreach (var userId in affectedUserIds)
            {
                await authService.RevokeSessionsForUserAsync(userId);
            }

            return Results.Ok();
        });

        group.MapDelete("/{id:guid}", async (Guid id, AuthDbContext db, AuthService authService) =>
        {
            var existingGroup = await db.UserGroups
                .Include(g => g.Users)
                .FirstOrDefaultAsync(g => g.Id == id);
            if (existingGroup == null) return Results.NotFound();

            var affectedUserIds = existingGroup.Users.Select(u => u.Id).ToList();

            db.UserGroups.Remove(existingGroup);
            await db.SaveChangesAsync();

            foreach (var userId in affectedUserIds)
            {
                await authService.RevokeSessionsForUserAsync(userId);
            }

            return Results.Ok();
        });
    }
}

public class CreateGroupRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<PermissionDto> Permissions { get; set; } = new();
}

public class UpdateGroupRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<PermissionDto> Permissions { get; set; } = new();
}
