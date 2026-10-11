using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Data;

namespace SemanticAgentBroker.Api.Features.Auth;

public static class PermissionEvaluator
{
    public static async Task<List<EvaluatedPermission>> GetEffectivePermissionsAsync(this AuthDbContext db, Guid userId)
    {
        // 1. Get user's direct permissions
        var userPerms = await db.UserTaskPermissions
            .Where(p => p.UserId == userId)
            .ToListAsync();

        // 2. Get user's group permissions
        var groupPerms = await db.UserGroups
            .Where(g => g.Users.Any(u => u.Id == userId))
            .SelectMany(g => g.TaskPermissions)
            .ToListAsync();

        // 3. Combine and group by PluginId and PluginTaskId
        var allPerms = userPerms.Select(p => new { p.PluginId, p.PluginTaskId, p.AllowedOptions })
            .Concat(groupPerms.Select(p => new { p.PluginId, p.PluginTaskId, p.AllowedOptions }));

        var evaluated = allPerms
            .GroupBy(p => new { p.PluginId, p.PluginTaskId })
            .Select(g => new EvaluatedPermission
            {
                PluginId = g.Key.PluginId,
                PluginTaskId = g.Key.PluginTaskId,
                AllowedOptions = g.SelectMany(x => x.AllowedOptions).Distinct().ToList()
            })
            .ToList();

        return evaluated;
    }

    /// <summary>
    /// Evaluates whether the effective permissions allow accessing a specific plugin task,
    /// checking both exact task ID matches and wildcard '*' matches, and verifying required options.
    /// </summary>
    public static bool HasPermission(this IEnumerable<EvaluatedPermission> perms, string pluginId, string pluginTaskId, string? requiredOption = null)
    {
        var matching = perms.Where(p => string.Equals(p.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) &&
                                        (string.Equals(p.PluginTaskId, pluginTaskId, StringComparison.OrdinalIgnoreCase) || p.PluginTaskId == "*"))
                            .ToList();

        if (matching.Count == 0) return false;
        if (string.IsNullOrEmpty(requiredOption)) return true;

        return matching.Any(p => p.AllowedOptions.Contains(requiredOption, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the union of all allowed options across matching exact task rules and wildcard '*' rules for a plugin.
    /// </summary>
    public static List<string> GetAllowedOptions(this IEnumerable<EvaluatedPermission> perms, string pluginId, string pluginTaskId)
    {
        return perms
            .Where(p => string.Equals(p.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(p.PluginTaskId, pluginTaskId, StringComparison.OrdinalIgnoreCase) || p.PluginTaskId == "*"))
            .SelectMany(p => p.AllowedOptions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public class EvaluatedPermission
{
    public string PluginId { get; set; } = string.Empty;
    public string PluginTaskId { get; set; } = string.Empty;
    public List<string> AllowedOptions { get; set; } = new();
}
