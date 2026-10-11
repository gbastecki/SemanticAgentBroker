using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Api.Data;

public class TokenDbContext : DbContext
{
    public TokenDbContext(DbContextOptions<TokenDbContext> options) : base(options) { }

    public DbSet<UserSessionRecord> Sessions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserSessionRecord>(entity =>
        {
            entity.HasKey(e => e.Token);
            entity.Property(e => e.Token).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Username).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Role).HasMaxLength(32).IsRequired();
            entity.Property(e => e.IPAddress).HasMaxLength(64).IsRequired();
            entity.Property(e => e.BrowserId).HasMaxLength(128).IsRequired();
            
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.IPAddress, e.BrowserId });
        });

        base.OnModelCreating(modelBuilder);
    }
}
