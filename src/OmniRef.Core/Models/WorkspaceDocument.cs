namespace OmniRef.Core.Models;

public sealed class WorkspaceDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public WorldPoint ViewportOrigin { get; set; }
    public double Zoom { get; set; } = 1;
    public List<BoardItem> Items { get; set; } = [];

    public WorkspaceDocument DeepClone() => new()
    {
        SchemaVersion = SchemaVersion,
        Id = Id,
        CreatedUtc = CreatedUtc,
        ModifiedUtc = ModifiedUtc,
        ViewportOrigin = ViewportOrigin,
        Zoom = Zoom,
        Items = Items.Select(item => item.DeepClone()).ToList()
    };
}
