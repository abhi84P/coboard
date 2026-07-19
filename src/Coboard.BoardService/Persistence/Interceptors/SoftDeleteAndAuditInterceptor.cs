using Coboard.BoardService.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;

namespace Coboard.BoardService.Persistence.Interceptors;

/// <summary>
/// Stamps audit fields, converts hard-delete into soft-delete for soft-deletable entities,
/// and writes a row to <see cref="ShapeHistory"/> for every shape Insert/Update/Delete.
///
/// Replaces SQL Server temporal tables (Npgsql provider does not support them) with an
/// append-only history table written from this interceptor.
/// </summary>
public class SoftDeleteAndAuditInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _http;

    public SoftDeleteAndAuditInterceptor(IHttpContextAccessor http)
    {
        _http = http;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        StampAndCaptureHistory(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        StampAndCaptureHistory(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    private void StampAndCaptureHistory(DbContext? ctx)
    {
        if (ctx is null) return;
        var now = DateTime.UtcNow;
        var userId = ResolveUserId();

        // Snapshot: adding tracked entities (ShapeHistory rows) inside the loop would
        // mutate the change tracker mid-enumeration and throw.
        var pendingHistory = new List<ShapeHistory>();

        foreach (EntityEntry entry in ctx.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    SetIfExists(entry, "CreatedAt", now);
                    SetIfExists(entry, "UpdatedAt", now);
                    if (entry.Entity is Shape s)
                        pendingHistory.Add(BuildHistory(s, "Insert", userId, now));
                    break;

                case EntityState.Modified:
                    if (entry.Entity is Shape sm && IsBeingSoftDeleted(entry))
                        pendingHistory.Add(BuildHistory(sm, "Delete", userId, now));
                    else if (entry.Entity is Shape su)
                        pendingHistory.Add(BuildHistory(su, "Update", userId, now));
                    SetIfExists(entry, "UpdatedAt", now);
                    break;

                case EntityState.Deleted:
                    if (entry.Entity is Board || entry.Entity is Shape)
                    {
                        // Flip principal back to Modified (soft delete).
                        entry.State = EntityState.Modified;
                        SetIfExists(entry, "DeletedAt", now);
                        // Cascade-deletion marked owned entries (e.g. ShapeGeometry) as Deleted too.
                        // EF would then null their columns on the UPDATE. Flip them back to their
                        // pre-delete state so the soft-delete UPDATE preserves geometry values.
                        foreach (var nav in entry.References)
                        {
                            var target = nav.TargetEntry;
                            if (target is { State: EntityState.Deleted } && target.Metadata.IsOwned())
                                target.State = EntityState.Unchanged;
                        }
                        if (entry.Entity is Shape sd)
                            pendingHistory.Add(BuildHistory(sd, "Delete", userId, now));
                    }
                    break;
            }
        }

        // Flush history rows AFTER the loop so the change tracker is stable.
        if (pendingHistory.Count > 0)
            ctx.Set<ShapeHistory>().AddRange(pendingHistory);
    }

    private static ShapeHistory BuildHistory(Shape shape, string kind, Guid userId, DateTime now) =>
        new()
        {
            ShapeId = shape.Id,
            BoardId = shape.BoardId,
            ChangeKind = kind,
            SnapshotJson = JsonSerializer.Serialize(shape),
            ChangedByUserId = userId,
            ChangedAt = now,
        };

    private static bool IsBeingSoftDeleted(EntityEntry entry)
        => entry.Property("DeletedAt").CurrentValue is DateTime;

    private static void SetIfExists(EntityEntry entry, string prop, object value)
    {
        var p = entry.Metadata.FindProperty(prop);
        if (p is not null) entry.Property(prop).CurrentValue = value;
    }

    private Guid ResolveUserId()
    {
        var sub = _http.HttpContext?.User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var g) ? g : Guid.Empty;
    }
}
