using System.Text.Json;

namespace SemanticAgentBroker.Shared.Domain;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "User"; // "Admin" or "User"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<UserTaskPermission> TaskPermissions { get; set; } = new();
    public List<UserGroup> Groups { get; set; } = new();
}

public class UserTaskPermission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public string PluginTaskId { get; set; } = string.Empty;

    public List<string> AllowedOptions { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public User? User { get; set; }
}

public class FailedLoginAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IPAddress { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime? LockoutEnd { get; set; }
}

public class UserGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<UserGroupTaskPermission> TaskPermissions { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public List<User> Users { get; set; } = new();
}

public class UserGroupTaskPermission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserGroupId { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public string PluginTaskId { get; set; } = string.Empty;

    public List<string> AllowedOptions { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public UserGroup? UserGroup { get; set; }
}

public class UserSessionRecord
{
    public string Token { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string IPAddress { get; set; } = string.Empty;
    public string BrowserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
}
