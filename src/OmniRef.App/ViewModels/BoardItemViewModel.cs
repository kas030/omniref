using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using OmniRef.Core.Models;

namespace OmniRef.App.ViewModels;

public sealed class BoardItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isMissing;
    private ImageSource? _preview;
    private bool _previewLoading;

    public BoardItemViewModel(BoardItem model)
    {
        Model = model;
    }

    public BoardItem Model { get; }
    public Guid Id => Model.Id;
    public ItemKind Kind => Model.Kind;
    public bool IsText => Kind == ItemKind.Text;

    public string Title
    {
        get => Model.Title;
        set
        {
            if (Model.Title == value)
            {
                return;
            }

            Model.Title = value;
            Touch();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Model.Title)
        ? Kind.ToString()
        : Model.Title;

    public WorldRect Bounds
    {
        get => Model.Bounds;
        private set
        {
            if (Model.Bounds == value)
            {
                return;
            }

            Model.Bounds = value;
            Touch();
            OnPropertyChanged();
        }
    }

    public Guid? ParentFrameId
    {
        get => Model.ParentFrameId;
        private set
        {
            if (Model.ParentFrameId == value)
            {
                return;
            }

            Model.ParentFrameId = value;
            Touch();
            OnPropertyChanged();
        }
    }

    public string TagsText
    {
        get => string.Join(", ", Model.Tags);
        set
        {
            var tags = value
                .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (Model.Tags.SequenceEqual(tags, StringComparer.CurrentCultureIgnoreCase))
            {
                return;
            }

            Model.Tags = tags;
            Touch();
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsMissing
    {
        get => _isMissing;
        set => SetProperty(ref _isMissing, value);
    }

    public ImageSource? Preview
    {
        get => _preview;
        set => SetProperty(ref _preview, value);
    }

    public bool PreviewLoading
    {
        get => _previewLoading;
        set => SetProperty(ref _previewLoading, value);
    }

    public string SecondaryText => Model.Content switch
    {
        ImageContent image => image.Source.OriginalFileName,
        FileContent file => file.Extension?.ToUpperInvariant() ?? file.Source.OriginalFileName,
        FolderContent folder => folder.Source.AbsolutePath ?? folder.Source.OriginalFileName,
        TextContent text => text.Text,
        UrlContent url => url.Url,
        FrameContent => string.Empty,
        _ => string.Empty
    };

    public string SecondaryPreviewText => SecondaryText.ReplaceLineEndings(" ");

    public double TextFontSize
    {
        get => Model.Content is TextContent text ? text.FontSize : 18;
        set
        {
            if (Model.Content is TextContent text && double.IsFinite(value))
            {
                ReplaceTextContent(text with { FontSize = Math.Clamp(value, 8, 96) });
            }
        }
    }

    public string TextForeground
    {
        get => Model.Content is TextContent text ? text.Foreground : "#FFF5F7FA";
        set
        {
            if (Model.Content is TextContent text && TryNormalizeColor(value, out var color))
            {
                ReplaceTextContent(text with { Foreground = color });
            }
        }
    }

    public string TextBackground
    {
        get => Model.Content is TextContent text ? text.Background : "#FF2E3440";
        set
        {
            if (Model.Content is TextContent text && TryNormalizeColor(value, out var color))
            {
                ReplaceTextContent(text with { Background = color });
            }
        }
    }

    public TextHorizontalAlignment TextAlignment =>
        Model.Content is TextContent text ? text.Alignment : TextHorizontalAlignment.Left;

    public event EventHandler? ModelChanged;
    public event EventHandler? VisualChanged;

    public void UpdateBounds(WorldRect bounds)
    {
        Bounds = bounds;
        VisualChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateParentFrame(Guid? frameId)
    {
        ParentFrameId = frameId;
        VisualChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateZIndex(int zIndex)
    {
        if (Model.ZIndex == zIndex)
        {
            return;
        }

        Model.ZIndex = zIndex;
        Touch();
    }

    public void ReplaceContent(ItemContent content)
    {
        Model.Content = content;
        Touch();
        Preview = null;
        OnPropertyChanged(nameof(SecondaryText));
        OnPropertyChanged(nameof(SecondaryPreviewText));
        VisualChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetTextAlignment(TextHorizontalAlignment alignment)
    {
        if (Model.Content is TextContent text)
        {
            ReplaceTextContent(text with { Alignment = alignment });
        }
    }

    public void RefreshAll()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(Bounds));
        OnPropertyChanged(nameof(ParentFrameId));
        OnPropertyChanged(nameof(TagsText));
        OnPropertyChanged(nameof(SecondaryText));
        OnPropertyChanged(nameof(SecondaryPreviewText));
        OnPropertyChanged(nameof(TextFontSize));
        OnPropertyChanged(nameof(TextForeground));
        OnPropertyChanged(nameof(TextBackground));
        OnPropertyChanged(nameof(TextAlignment));
        VisualChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReplaceTextContent(TextContent content)
    {
        if (Equals(Model.Content, content))
        {
            return;
        }

        Model.Content = content;
        Touch();
        OnPropertyChanged(nameof(SecondaryText));
        OnPropertyChanged(nameof(SecondaryPreviewText));
        OnPropertyChanged(nameof(TextFontSize));
        OnPropertyChanged(nameof(TextForeground));
        OnPropertyChanged(nameof(TextBackground));
        OnPropertyChanged(nameof(TextAlignment));
    }

    private static bool TryNormalizeColor(string value, out string color)
    {
        try
        {
            if (ColorConverter.ConvertFromString(value) is Color parsed)
            {
                color = parsed.ToString();
                return true;
            }
        }
        catch (FormatException)
        {
        }
        catch (NotSupportedException)
        {
        }

        color = string.Empty;
        return false;
    }

    private void Touch()
    {
        Model.ModifiedUtc = DateTimeOffset.UtcNow;
        ModelChanged?.Invoke(this, EventArgs.Empty);
        VisualChanged?.Invoke(this, EventArgs.Empty);
    }
}
