namespace OmniRef.Core.Models;

public sealed class BoardItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ItemKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public WorldRect Bounds { get; set; } = new(0, 0, 240, 160);
    public int ZIndex { get; set; }
    public Guid? ParentFrameId { get; set; }
    public ItemStyle Style { get; set; } = new();
    public ItemContent Content { get; set; } = new TextContent(string.Empty);
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;

    public BoardItem DeepClone() => new()
    {
        Id = Id,
        Kind = Kind,
        Title = Title,
        Bounds = Bounds,
        ZIndex = ZIndex,
        ParentFrameId = ParentFrameId,
        Style = Style with { },
        Content = Content switch
        {
            ImageContent image => image with { Source = image.Source with { } },
            FileContent file => file with { Source = file.Source with { } },
            FolderContent folder => folder with { Source = folder.Source with { } },
            TextContent text => text with { },
            UrlContent url => url with { },
            FrameContent frame => frame with { },
            _ => throw new InvalidOperationException($"Unsupported item content: {Content.GetType().Name}")
        },
        Tags = [.. Tags],
        CreatedUtc = CreatedUtc,
        ModifiedUtc = ModifiedUtc
    };
}
