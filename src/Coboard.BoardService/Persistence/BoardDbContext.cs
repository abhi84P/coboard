using System.Text.Json;
 using Coboard.BoardService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Coboard.BoardService.Persistence;

public class BoardDbContext : DbContext
{
    public BoardDbContext(DbContextOptions<BoardDbContext> options) : base(options) { }

    public DbSet<Board> Boards => Set<Board>();
    public DbSet<Shape> Shapes => Set<Shape>();
    public DbSet<ShapeHistory> ShapeHistory => Set<ShapeHistory>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ---- Pattern: Owned types (ShapeGeometry stored as columns on shapes) ----
        mb.Entity<Shape>(b =>
        {
            b.ToTable("shapes");
            b.HasKey(s => s.Id);
            b.OwnsOne(s => s.Geometry, g =>
            {
                g.Property(p => p.X).HasColumnName("x");
                g.Property(p => p.Y).HasColumnName("y");
                g.Property(p => p.Width).HasColumnName("width");
                g.Property(p => p.Height).HasColumnName("height");
            });
        });
        // ---- Pattern: Value comparers (primitive collection reordering detection) ----
        // Tags is a List<string> — primitive collection that EF can re-read from jsonb.
        // The comparer makes EF detect reorders via deep equality, not just reference.
        var tagsComparer = new ValueComparer<List<string>>(
            (a, b) => a!.SequenceEqual(b!),
            v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode())),
            v => v.ToList());

        mb.Entity<Board>(b =>
        {
            b.ToTable("boards");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Tags)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v),
                    v => JsonSerializer.Deserialize<List<string>>(v) ?? new List<string>(),
                    tagsComparer);
            b.HasMany<Shape>("_shapes")
                .WithOne()
                .HasForeignKey(s => s.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
            b.Metadata.FindNavigation("_shapes")!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        mb.Entity<ShapeHistory>(b =>
        {
            b.ToTable("shape_history");
            b.HasKey(h => h.Id);
            b.Property(h => h.ChangeKind).HasMaxLength(20);
            b.Property(h => h.SnapshotJson).HasColumnType("jsonb");
            b.HasIndex(h => new { h.ShapeId, h.ChangedAt });
        });
    }
}
