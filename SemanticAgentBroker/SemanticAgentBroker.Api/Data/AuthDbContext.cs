using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Shared.Domain;
using System.Text.Json;

namespace SemanticAgentBroker.Api.Data;

public class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }
    public DbSet<UserTaskPermission> UserTaskPermissions { get; set; }
    public DbSet<FailedLoginAttempt> FailedLoginAttempts { get; set; }
    public DbSet<UserGroup> UserGroups { get; set; }
    public DbSet<UserGroupTaskPermission> UserGroupTaskPermissions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var stringListComparer = new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<string>>(
            (c1, c2) => c1 != null && c2 != null ? c1.SequenceEqual(c2) : c1 == c2,
            c => c != null ? c.Aggregate(0, (a, v) => HashCode.Combine(a, v != null ? v.GetHashCode() : 0)) : 0,
            c => c != null ? c.ToList() : new List<string>());

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.Username).HasMaxLength(128).IsRequired();
            entity.Property(e => e.PasswordHash).IsRequired();
            entity.Property(e => e.Role).HasMaxLength(32).IsRequired();

            entity.HasMany(e => e.TaskPermissions)
                .WithOne(p => p.User)
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Groups)
                .WithMany(g => g.Users)
                .UsingEntity(j => j.ToTable("UserUserGroups"));
        });

        modelBuilder.Entity<UserTaskPermission>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.PluginId, e.PluginTaskId }).IsUnique();
            entity.Property(e => e.PluginId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.PluginTaskId).HasMaxLength(128).IsRequired();

            entity.Property(e => e.AllowedOptions)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType("TEXT")
                .Metadata.SetValueComparer(stringListComparer);
        });

        modelBuilder.Entity<FailedLoginAttempt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.IPAddress, e.Username }).IsUnique();
            entity.Property(e => e.IPAddress).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<UserGroup>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(128).IsRequired();

            entity.HasMany(e => e.TaskPermissions)
                .WithOne(p => p.UserGroup)
                .HasForeignKey(p => p.UserGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserGroupTaskPermission>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserGroupId, e.PluginId, e.PluginTaskId }).IsUnique();
            entity.Property(e => e.PluginId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.PluginTaskId).HasMaxLength(128).IsRequired();

            entity.Property(e => e.AllowedOptions)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType("TEXT")
                .Metadata.SetValueComparer(stringListComparer);
        });

        base.OnModelCreating(modelBuilder);
    }
}
