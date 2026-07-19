namespace Coboard.BoardService.Domain;

/// <summary>
/// Aggregate root for a collaborative whiteboard.
/// </summary>
public class Board
{
    public Guid Id { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Name { get; set; } = string.Empty;
    public Guid OwnerId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; } // soft delete via interceptor

    // Collection nav: EF reads from the backing field via SetPropertyAccessMode(Field).
    private List<Shape> _shapes = new();
}
