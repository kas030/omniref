using System.Globalization;
using OmniRef.Core.Models;

namespace OmniRef.Core.Services;

public static class WorkspaceSearch
{
    public static IReadOnlyList<BoardItem> Search(IEnumerable<BoardItem> items, string query)
    {
        var normalized = query.Trim();
        return items
            .Where(item => normalized.Length == 0 || SearchableText(item).Contains(
                normalized,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace))
            .OrderByDescending(item => item.ModifiedUtc)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string SearchableText(BoardItem item)
    {
        var content = item.Content switch
        {
            ImageContent image => $"{image.Source.OriginalFileName} {image.Source.AbsolutePath} {image.AltText}",
            FileContent file => $"{file.Source.OriginalFileName} {file.Source.AbsolutePath} {file.Extension}",
            FolderContent folder => $"{folder.Source.OriginalFileName} {folder.Source.AbsolutePath}",
            TextContent text => text.Text,
            UrlContent url => $"{url.Url} {url.DisplayHost}",
            FrameContent => string.Empty,
            _ => string.Empty
        };

        return string.Join(' ', item.Title, content, string.Join(' ', item.Tags));
    }

    private static bool Contains(this string source, string value, CompareOptions options) =>
        CultureInfo.CurrentCulture.CompareInfo.IndexOf(source, value, options) >= 0;
}
