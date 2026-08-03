using OmniRef.Core.Models;

namespace OmniRef.Core.Services;

public static class BoardItemFactory
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico"
    };

    public static BoardItem FromPath(string workspacePath, string path, WorldPoint position, int zIndex)
    {
        var fullPath = Path.GetFullPath(path);
        var isFolder = Directory.Exists(fullPath);
        var extension = Path.GetExtension(fullPath);
        var isImage = !isFolder && ImageExtensions.Contains(extension);
        var info = isFolder ? null : new FileInfo(fullPath);
        var source = new SourceDescriptor(
            fullPath,
            PathResolver.CreateRelativePath(workspacePath, fullPath),
            AssetMode.ExternalReference,
            null,
            Path.GetFileName(fullPath),
            info?.Exists == true ? info.Length : null,
            info?.Exists == true ? info.LastWriteTimeUtc : null);

        return new BoardItem
        {
            Kind = isFolder ? ItemKind.Folder : isImage ? ItemKind.Image : ItemKind.File,
            Title = isFolder
                ? new DirectoryInfo(fullPath).Name
                : isImage
                    ? Path.GetFileNameWithoutExtension(fullPath)
                    : Path.GetFileName(fullPath),
            Bounds = new(position.X, position.Y, isImage ? 300 : 240, isImage ? 220 : 150),
            ZIndex = zIndex,
            Content = isFolder
                ? new FolderContent(source)
                : isImage
                    ? new ImageContent(source)
                    : new FileContent(source, extension)
        };
    }

    public static BoardItem Text(string text, WorldPoint position, int zIndex) => new()
    {
        Kind = ItemKind.Text,
        Title = string.Empty,
        Bounds = new(position.X, position.Y, 280, 180),
        ZIndex = zIndex,
        Content = new TextContent(text)
    };

    public static BoardItem Url(string url, WorldPoint position, int zIndex)
    {
        var uri = new Uri(url);
        return new BoardItem
        {
            Kind = ItemKind.Url,
            Title = uri.Host,
            Bounds = new(position.X, position.Y, 300, 140),
            ZIndex = zIndex,
            Content = new UrlContent(url, uri.Host)
        };
    }

    public static BoardItem Frame(string title, WorldPoint position, int zIndex) => new()
    {
        Kind = ItemKind.Frame,
        Title = title,
        Bounds = new(position.X, position.Y, 640, 420),
        ZIndex = zIndex,
        Content = new FrameContent(),
        Style = new ItemStyle(Background: "#1A7C8CFF", Accent: "#FF7C8CFF")
    };
}
