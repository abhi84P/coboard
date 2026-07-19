using Microsoft.EntityFrameworkCore.ChangeTracking;
using Coboard.AuthService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Coboard.AuthService.Persistence;

public class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<User>(b =>
        {
            b.ToTable("users");
            b.HasKey(u => u.Id);
            b.Property(u => u.Email).HasMaxLength(320).IsRequired();
            b.Property(u => u.PasswordHash).HasMaxLength(255).IsRequired();
            b.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
            b.HasIndex(u => u.Email).IsUnique();
        });
    }
}

/// <summary>
/// Stamps audit fields + soft-deletes. Mirrors the Board service interceptor pattern.
/// </summary>
public class AuditFieldsInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    private static void Stamp(DbContext? ctx)
    {
        if (ctx is null) return;
        var now = DateTime.UtcNow;
        foreach (var entry in ctx.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    SetIfExists(entry, "CreatedAt", now);
                    SetIfExists(entry, "UpdatedAt", now);
                    break;
                case EntityState.Modified:
                    SetIfExists(entry, "UpdatedAt", now);
                    break;
                case EntityState.Deleted:
                    if (entry.Entity is User)
                    {
                        entry.State = EntityState.Modified;
                        SetIfExists(entry, "DeletedAt", now);
                    }
                    break;
            }
        }
    }

    private static void SetIfExists(EntityEntry entry, string prop, object value)
    {
        var p = entry.Metadata.FindProperty(prop);
        if (p is not null) entry.Property(prop).CurrentValue = value;
    }
}
