using Coboard.BoardService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Coboard.BoardService.Persistence;

/// <summary>
/// Pattern: Compiled queries. Hot read path compiled once, reused on every call.
/// </summary>
public static class CompiledQueries
{
    public static readonly Func<BoardDbContext, Guid, CancellationToken, Task<List<Shape>>>
        GetActiveShapesByBoard = EF.CompileAsyncQuery(
            (BoardDbContext ctx, Guid boardId, CancellationToken ct) =>
                ctx.Shapes
                    .AsNoTracking()
                    .Where(s => s.BoardId == boardId && s.DeletedAt == null)
                    .OrderBy(s => s.ZOrder)
                    .ToList());
}
