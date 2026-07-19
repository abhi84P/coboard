using System.Text.Json;
using Coboard.BoardService.Domain;
using Coboard.BoardService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coboard.BoardService.Endpoints;

public static class ShapesEndpoints
{
    public static void MapShapes(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/boards/{boardId:guid}/shapes").WithTags("Shapes");

        // Compiled query hot path + NoTracking default from DbContext config
        group.MapGet("/", async (Guid boardId, BoardDbContext db, CancellationToken ct) =>
            Results.Ok(await CompiledQueries.GetActiveShapesByBoard(db, boardId, ct)));

        // Bulk soft-delete via ExecuteUpdateAsync + explicit history rows
        // (ExecuteUpdateAsync bypasses SaveChanges, so the interceptor can't see it)
        group.MapDelete("/all", async (Guid boardId, BoardDbContext db, CancellationToken ct) =>
        {
            var rows = await BulkDeleteShapesAsync(db, boardId, ct);
            return Results.Ok(new { softDeleted = rows });
        });

        group.MapPost("/", async (Guid boardId, Shape shape, BoardDbContext db, CancellationToken ct) =>
        {
            shape.BoardId = boardId;
            if (shape.Id == Guid.Empty) shape.Id = Guid.NewGuid();
            db.Shapes.Add(shape);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/boards/{boardId}/shapes/{shape.Id}", shape);
        });
    }

    /// <summary>
    /// Soft-deletes all active shapes for a board using ExecuteUpdateAsync,
    /// then writes one <see cref="ShapeHistory"/> row per shape in the same transaction.
    /// ExecuteUpdateAsync bypasses the SaveChanges interceptor, so history must be captured
    /// explicitly here. Exposed as a static method so tests can exercise the bulk path
    /// without spinning up the HTTP host.
    /// </summary>
    public static async Task<int> BulkDeleteShapesAsync(BoardDbContext db, Guid boardId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Wrap the bulk update + history capture in a single transaction. ExecuteUpdateAsync
        // and SaveChangesAsync each commit autonomously otherwise — process death between
        // them would leave shapes soft-deleted with no audit history.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Snapshot the full row (not just id) BEFORE the update so audit captures geometry.
        var snapshot = await db.Shapes
            .Where(s => s.BoardId == boardId && s.DeletedAt == null)
            .ToListAsync(ct);

        var rows = await db.Shapes
            .Where(s => s.BoardId == boardId && s.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, now), ct);

        db.ShapeHistory.AddRange(snapshot.Select(shape => new ShapeHistory
        {
            ShapeId = shape.Id,
            BoardId = boardId,
            ChangeKind = "BulkDelete",
            SnapshotJson = JsonSerializer.Serialize(shape),
            ChangedByUserId = Guid.Empty,
            ChangedAt = now,
        }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return rows;
    }
}
