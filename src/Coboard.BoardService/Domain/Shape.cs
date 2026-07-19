namespace Coboard.BoardService.Domain;

public enum ShapeKind { Rectangle, Ellipse, Line, Path, Text }

/// <summary>
/// Shape on a board. Geometry is an owned value type (no separate table).
/// </summary>
public class Shape
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public ShapeKind Kind { get; set; }
    public int ZOrder { get; set; }
    public string? StyleJson { get; set; }

    // Owned value object — persisted as columns on shapes
    public ShapeGeometry Geometry { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public class ShapeGeometry
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
