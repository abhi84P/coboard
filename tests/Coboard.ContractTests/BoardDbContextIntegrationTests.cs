using Coboard.BoardService.Domain;
using Coboard.BoardService.Endpoints;
using Coboard.BoardService.Persistence;
using Coboard.BoardService.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Coboard.ContractTests;

// =============================================================================
// SQLite-backed tests (no Docker required). Real relational provider so
// ExecuteUpdateAsync, transactions, FK constraints are exercised.
// =============================================================================

public class InterceptorLogicTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public InterceptorLogicTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
    }

    public void Dispose() => _conn.Dispose();

    private BoardDbContext NewContext()
    {
        var http = new HttpContextAccessor { HttpContext = null };
        var opts = new DbContextOptionsBuilder<BoardDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new SoftDeleteAndAuditInterceptor(http))
            .Options;
        var ctx = new BoardDbContext(opts);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public async Task InsertShape_WritesInsertHistoryRow()
    {
        var boardId = Guid.NewGuid();
        var shapeId = Guid.NewGuid();

        await using (var ctx = NewContext())
        {
            ctx.Boards.Add(new Board { Id = boardId, Name = "h", OwnerId = Guid.NewGuid() });
            ctx.Shapes.Add(new Shape
            {
                Id = shapeId,
                BoardId = boardId,
                Kind = ShapeKind.Rectangle,
                ZOrder = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var rows = await ctx.ShapeHistory.Where(h => h.ShapeId == shapeId).ToListAsync();
            rows.Should().ContainSingle().Which.ChangeKind.Should().Be("Insert");
        }
    }

    [Fact]
    public async Task HardDeleteShape_ConvertsToSoftDeleteAndWritesHistory()
    {
        // Geometry uses defaults (0,0,0,0). If the interceptor fails to flip
        // owned entries back from Deleted, this throws NOT NULL on shapes.x/y/etc.
        var boardId = Guid.NewGuid();
        var shapeId = Guid.NewGuid();

        await using (var ctx = NewContext())
        {
            ctx.Boards.Add(new Board { Id = boardId, Name = "sd", OwnerId = Guid.NewGuid() });
            ctx.Shapes.Add(new Shape
            {
                Id = shapeId,
                BoardId = boardId,
                Kind = ShapeKind.Ellipse,
                ZOrder = 1,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var shape = await ctx.Shapes.FirstAsync(s => s.Id == shapeId);
            ctx.Shapes.Remove(shape);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var shape = await ctx.Shapes.FirstOrDefaultAsync(s => s.Id == shapeId);
            shape.Should().NotBeNull("soft delete must keep the row");
            shape!.DeletedAt.Should().NotBeNull();
            // Geometry must survive the soft-delete (owned-type columns preserved).
            shape.Geometry.X.Should().Be(0);
            shape.Geometry.Y.Should().Be(0);

            var history = await ctx.ShapeHistory
                .Where(h => h.ShapeId == shapeId)
                .OrderBy(h => h.ChangedAt)
                .ToListAsync();
            history.Should().HaveCount(2);
            history[0].ChangeKind.Should().Be("Insert");
            history[1].ChangeKind.Should().Be("Delete");
        }
    }

    [Fact]
    public async Task BulkDeleteShapes_SoftDeletesAllAndWritesHistoryForEach()
    {
        // ExecuteUpdateAsync bypasses SaveChanges so the interceptor can't see it.
        // The bulk endpoint must hand-write a ShapeHistory row per shape in the same transaction.
        var boardId = Guid.NewGuid();
        const int N = 4;
        var ids = new List<Guid>();

        await using (var ctx = NewContext())
        {
            ctx.Boards.Add(new Board { Id = boardId, Name = "bulk", OwnerId = Guid.NewGuid() });
            for (int i = 0; i < N; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                ctx.Shapes.Add(new Shape
                {
                    Id = id,
                    BoardId = boardId,
                    Kind = ShapeKind.Rectangle,
                    ZOrder = i,
                    Geometry = new ShapeGeometry { X = i, Y = i, Width = 1, Height = 1 },
                });
            }
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var rows = await ShapesEndpoints.BulkDeleteShapesAsync(ctx, boardId);
            rows.Should().Be(N);
        }

        await using (var ctx = NewContext())
        {
            var bulkHistory = await ctx.ShapeHistory
                .Where(h => h.BoardId == boardId && h.ChangeKind == "BulkDelete")
                .ToListAsync();
            bulkHistory.Should().HaveCount(N, "one BulkDelete history row per soft-deleted shape");
            bulkHistory.Should().AllSatisfy(h => h.ChangeKind.Should().Be("BulkDelete"));
            bulkHistory.Select(h => h.ShapeId).Should().BeEquivalentTo(ids);
            // Snapshot must capture the full row (geometry included), not "{}"
            bulkHistory.Should().AllSatisfy(h => h.SnapshotJson.Should().NotBe("{}"));
            bulkHistory.First().SnapshotJson.Should().Contain("Geometry");
        }
    }

    [Fact]
    public async Task InsertWithMultipleShapes_DoesNotThrowCollectionModified()
    {
        // Regression: interceptor previously added history rows mid-enumeration.
        var boardId = Guid.NewGuid();
        await using var ctx = NewContext();
        ctx.Boards.Add(new Board { Id = boardId, Name = "multi", OwnerId = Guid.NewGuid() });
        for (int i = 0; i < 5; i++)
        {
            ctx.Shapes.Add(new Shape
            {
                Id = Guid.NewGuid(),
                BoardId = boardId,
                Kind = ShapeKind.Rectangle,
                ZOrder = i,
            });
        }
        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TagsReorder_IsDetectedAsChange()
    {
        // Pattern #5: value comparer on primitive collection reordering
        var boardId = Guid.NewGuid();
        await using (var ctx = NewContext())
        {
            ctx.Boards.Add(new Board
            {
                Id = boardId,
                Name = "tags",
                OwnerId = Guid.NewGuid(),
                Tags = new List<string> { "alpha", "beta", "gamma" },
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var board = await ctx.Boards.FirstAsync(b => b.Id == boardId);
            board.Tags.Reverse();
            var changes = ctx.ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Modified)
                .ToList();
            changes.Should().NotBeEmpty("Tags reorder should be detected as Modified");
        }
    }
}

// =============================================================================
// Testcontainers/Postgres tests — gated behind [Trait("Docker")] so the suite
// passes on hosts without a Docker daemon. Run with:
//   dotnet test --filter "Docker=true"
// =============================================================================

[Trait("Docker", "true")]
public class PostgresIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await using var ctx = NewContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    private BoardDbContext NewContext()
    {
        var http = new HttpContextAccessor { HttpContext = null };
        var opts = new DbContextOptionsBuilder<BoardDbContext>()
            .UseNpgsql(_pg.GetConnectionString())
            .AddInterceptors(new SoftDeleteAndAuditInterceptor(http))
            .Options;
        return new BoardDbContext(opts);
    }

    [Fact]
    public async Task PostgresSoftDelete_StampsDeletedAt()
    {
        var boardId = Guid.NewGuid();
        await using (var ctx = NewContext())
        {
            ctx.Boards.Add(new Board { Id = boardId, Name = "pg", OwnerId = Guid.NewGuid() });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = NewContext())
        {
            var b = await ctx.Boards.FirstAsync(x => x.Id == boardId);
            ctx.Boards.Remove(b);
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = NewContext())
        {
            var b = await ctx.Boards.FirstOrDefaultAsync(x => x.Id == boardId);
            b.Should().NotBeNull();
            b!.DeletedAt.Should().NotBeNull();
        }
    }
}
