using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<TaskSession> TaskSessions { get; set; }
    public DbSet<TaskEvent> TaskEvents { get; set; }
    public DbSet<ChatMessageSnapshot> ChatMessages { get; set; }
    public DbSet<ImplementationPlan> ImplementationPlans { get; set; }
    public DbSet<TaskEnabledState> TaskEnabledStates { get; set; }

    // ── Dynamic Plugin & Agent System ──────────────────────────────────────
    public DbSet<PluginRegistration> PluginRegistrations { get; set; }
    public DbSet<PluginConfigValue> PluginConfigValues { get; set; }
    public DbSet<AgentDefinition> AgentDefinitions { get; set; }
    public DbSet<AgentMethodBinding> AgentMethodBindings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var readOnlyStringListComparer = new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<IReadOnlyList<string>>(
            (c1, c2) => c1 != null && c2 != null ? c1.SequenceEqual(c2) : c1 == c2,
            c => c != null ? c.Aggregate(0, (a, v) => HashCode.Combine(a, v != null ? v.GetHashCode() : 0)) : 0,
            c => c != null ? c.ToList() : new List<string>());

        // ── TaskSession ──
        modelBuilder.Entity<TaskSession>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.State)
                .HasConversion<string>()
                .HasMaxLength(32);

            entity.Property(e => e.Mode)
                .HasConversion<string>()
                .HasMaxLength(32);

            entity.HasMany(e => e.Events)
                .WithOne(ev => ev.Session)
                .HasForeignKey(ev => ev.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.ChatHistory)
                .WithOne(cm => cm.Session)
                .HasForeignKey(cm => cm.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Plans)
                .WithOne(p => p.Session)
                .HasForeignKey(p => p.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── TaskEvent ──
        modelBuilder.Entity<TaskEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SessionId, e.Timestamp });
            entity.Property(e => e.EventType).HasMaxLength(64);
            entity.Property(e => e.Level).HasMaxLength(32);
        });

        // ── TaskEnabledState ──
        modelBuilder.Entity<TaskEnabledState>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.PluginId, e.TaskId }).IsUnique();
        });

        // ── ChatMessageSnapshot ──
        modelBuilder.Entity<ChatMessageSnapshot>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SessionId, e.SequenceNumber }).IsUnique();
            entity.Property(e => e.Role).HasMaxLength(16);
        });

        // ── ImplementationPlan ──
        modelBuilder.Entity<ImplementationPlan>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SessionId, e.Version });
            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(32);
        });

        // ── PluginRegistration ──
        modelBuilder.Entity<PluginRegistration>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(128);
            entity.Property(e => e.DisplayName).HasMaxLength(256);
            entity.Property(e => e.Version).HasMaxLength(32);
            entity.Property(e => e.Author).HasMaxLength(256);
            entity.Property(e => e.Icon).HasMaxLength(64);
            entity.Property(e => e.DllFileName).HasMaxLength(512);
            entity.Property(e => e.DllHash).HasMaxLength(128);

            entity.Property(e => e.LoadState)
                .HasConversion<string>()
                .HasMaxLength(32);

            entity.Property(e => e.Category)
                .HasConversion<string>()
                .HasMaxLength(32);

            // Tags stored as JSON array
            entity.Property(e => e.Tags)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType("TEXT")
                .Metadata.SetValueComparer(readOnlyStringListComparer);

            // RequiredPlugins stored as JSON array
            entity.Property(e => e.RequiredPlugins)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v.ToList(), (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType("TEXT")
                .Metadata.SetValueComparer(readOnlyStringListComparer);

            entity.HasMany(e => e.ConfigValues)
                .WithOne(c => c.Plugin)
                .HasForeignKey(c => c.PluginId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── PluginConfigValue ──
        modelBuilder.Entity<PluginConfigValue>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.PluginId, e.Key }).IsUnique();
            entity.Property(e => e.Key).HasMaxLength(256);
        });

        // ── AgentDefinition ──
        modelBuilder.Entity<AgentDefinition>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(128);
            entity.Property(e => e.Icon).HasMaxLength(64);
            entity.Property(e => e.AccentClass).HasMaxLength(32);

            entity.HasMany(e => e.MethodBindings)
                .WithOne(mb => mb.Agent)
                .HasForeignKey(mb => mb.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── AgentMethodBinding ──
        modelBuilder.Entity<AgentMethodBinding>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.AgentId, e.PluginId, e.MethodId }).IsUnique();
            entity.Property(e => e.PluginId).HasMaxLength(128);
            entity.Property(e => e.MethodId).HasMaxLength(128);
        });

        base.OnModelCreating(modelBuilder);
    }
}

