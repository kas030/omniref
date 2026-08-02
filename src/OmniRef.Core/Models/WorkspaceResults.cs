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

public enum SearchFieldKind
{
    Title,
    Tag,
    FileName,
    AbsolutePath,
    RelativePath,
    AltText,
    Extension,
    Text,
    Url,
    DisplayHost
}

public readonly record struct SearchField(SearchFieldKind Kind, string Value);

public sealed record SearchDocument(IReadOnlyList<SearchField> Fields);

/// <summary>
/// Describes whether a document matched and its normalized relevance score.
/// Scores are in the inclusive range 0 to 1; an empty query matches with a score of 0.
/// </summary>
public readonly record struct SearchMatch(bool IsMatch, double Score);

public sealed record WorkspaceSearchResult(BoardItem Item, double Score);
