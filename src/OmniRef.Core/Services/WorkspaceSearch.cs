using OmniRef.Core.Interfaces;
using OmniRef.Core.Models;

namespace OmniRef.Core.Services;

public static class WorkspaceSearch
{
    private static readonly IWorkspaceSearchScorer DefaultScorer = new FuzzyWorkspaceSearchScorer();

    public static IReadOnlyList<BoardItem> Search(IEnumerable<BoardItem> items, string query)
    {
        return SearchWithScores(items, query)
            .Select(result => result.Item)
            .ToList();
    }

    public static IReadOnlyList<WorkspaceSearchResult> SearchWithScores(
        IEnumerable<BoardItem> items,
        string query,
        IWorkspaceSearchScorer? scorer = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(query);

        var normalized = query.Trim();
        scorer ??= DefaultScorer;

        return items
            .Select(item =>
            {
                var match = normalized.Length == 0
                    ? new SearchMatch(true, 0)
                    : scorer.Score(normalized, CreateDocument(item));
                return (Item: item, Match: match);
            })
            .Where(result => result.Match.IsMatch)
            .OrderByDescending(result => result.Item.Kind != ItemKind.Frame)
            .ThenByDescending(result => result.Item.ZIndex)
            .Select(result => new WorkspaceSearchResult(result.Item, result.Match.Score))
            .ToList();
    }

    public static SearchDocument CreateDocument(BoardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var fields = new List<SearchField>();
        AddField(fields, SearchFieldKind.Title, item.Title);

        foreach (var tag in item.Tags)
        {
            AddField(fields, SearchFieldKind.Tag, tag);
        }

        switch (item.Content)
        {
            case ImageContent image:
                AddSourceFields(fields, image.Source);
                AddField(fields, SearchFieldKind.AltText, image.AltText);
                break;
            case FileContent file:
                AddSourceFields(fields, file.Source);
                AddField(fields, SearchFieldKind.Extension, file.Extension);
                break;
            case FolderContent folder:
                AddSourceFields(fields, folder.Source);
                break;
            case TextContent text:
                AddField(fields, SearchFieldKind.Text, text.Text);
                break;
            case UrlContent url:
                AddField(fields, SearchFieldKind.Url, url.Url);
                AddField(fields, SearchFieldKind.DisplayHost, url.DisplayHost);
                break;
            case FrameContent:
                break;
        }

        return new SearchDocument(fields);
    }

    public static string SearchableText(BoardItem item) =>
        string.Join(' ', CreateDocument(item).Fields.Select(field => field.Value));

    private static void AddSourceFields(List<SearchField> fields, SourceDescriptor source)
    {
        AddField(fields, SearchFieldKind.FileName, source.OriginalFileName);
        AddField(fields, SearchFieldKind.AbsolutePath, source.AbsolutePath);
        AddField(fields, SearchFieldKind.RelativePath, source.RelativePath);
    }

    private static void AddField(List<SearchField> fields, SearchFieldKind kind, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields.Add(new SearchField(kind, value));
        }
    }
}
