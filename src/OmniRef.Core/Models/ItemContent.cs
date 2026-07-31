using System.Text.Json.Serialization;

namespace OmniRef.Core.Models;

public sealed record SourceDescriptor(
    string? AbsolutePath,
    string? RelativePath,
    AssetMode Mode,
    Guid? EmbeddedAssetId,
    string OriginalFileName,
    long? Size,
    DateTimeOffset? ModifiedUtc);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(FileContent), "file")]
[JsonDerivedType(typeof(FolderContent), "folder")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(UrlContent), "url")]
[JsonDerivedType(typeof(FrameContent), "frame")]
public abstract record ItemContent(int Version = 1);

public sealed record ImageContent(SourceDescriptor Source, string? AltText = null, int Version = 1)
    : ItemContent(Version);

public sealed record FileContent(SourceDescriptor Source, string? Extension = null, int Version = 1)
    : ItemContent(Version);

public sealed record FolderContent(SourceDescriptor Source, int Version = 1)
    : ItemContent(Version);

public sealed record TextContent(
    string Text,
    double FontSize = 18,
    string Foreground = "#FFF5F7FA",
    string Background = "#FF2E3440",
    TextHorizontalAlignment Alignment = TextHorizontalAlignment.Left,
    int Version = 1)
    : ItemContent(Version);

public sealed record UrlContent(string Url, string? DisplayHost = null, int Version = 1)
    : ItemContent(Version);

public sealed record FrameContent(string Color = "#337C8CFF", int Version = 1)
    : ItemContent(Version);
