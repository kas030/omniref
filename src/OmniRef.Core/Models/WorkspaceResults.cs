namespace OmniRef.Core.Models;

public sealed record WorkspaceOpenResult(
    WorkspaceDocument Document,
    WorkspaceOpenMode Mode,
    string? Warning = null);

public sealed record EmbeddedAssetInfo(
    Guid Id,
    string FileName,
    string? MediaType,
    long Length,
    string Sha256);

public sealed record ThumbnailData(byte[] PngBytes, int PixelWidth, int PixelHeight);
