namespace Coboard.BoardService.Domain;

/// <summary>
/// Append-only history row written by AuditFieldsInterceptor on every shape change.
/// Replaces SQL Server temporal tables (Npgsql provider does not support those).
/// </summary>
public class ShapeHistory
{
    public long Id { get; set; }
    public Guid ShapeId { get; set; }
    public Guid BoardId { get; set; }
    public string ChangeKind { get; set; } = string.Empty; // Insert | Update | Delete
    public string SnapshotJson { get; set; } = string.Empty;
    public Guid ChangedByUserId { get; set; }
    public DateTime ChangedAt { get; set; }
}
