using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using FluentIcons.Common;
using FluentIcons.Wpf;
using OmniRef.App.ViewModels;
using OmniRef.Core.Models;
using OmniRef.Core.Services;

namespace OmniRef.App.Controls;

public sealed class InfiniteCanvas : Canvas
{
    private static readonly FontFamily CardTextFontFamily = new("Segoe UI Variable Text, Segoe UI");
    private static readonly Color DefaultCardBackgroundColor = Color.FromArgb(0xFF, 0x25, 0x29, 0x32);
    private static readonly Color DefaultCardForegroundColor = Color.FromArgb(0xFF, 0xF5, 0xF7, 0xFA);
    private static readonly Color DefaultTextBackgroundColor = Color.FromArgb(0xFF, 0x2E, 0x34, 0x40);
    private static readonly FluentIconGlyph ImageFilledGlyph = CreateFluentIconGlyph(Icon.Image);
    private static readonly FluentIconGlyph FolderFilledGlyph = CreateFluentIconGlyph(Icon.Folder);
    private static readonly FluentIconGlyph DocumentTextFilledGlyph = CreateFluentIconGlyph(Icon.DocumentText);
    private static readonly FluentIconGlyph LinkFilledGlyph = CreateFluentIconGlyph(Icon.Link);
    private static readonly ResizeCorner[] ResizeCorners =
    [
        ResizeCorner.TopLeft,
        ResizeCorner.TopRight,
        ResizeCorner.BottomLeft,
        ResizeCorner.BottomRight
    ];
    private const double ResizeHandleSize = 8;
    private const double ResizeHandleHitSize = 14;
    private const double SelectedBorderThickness = 1.5;
    private const double CardHorizontalPadding = 14;
    private const double CardVerticalPadding = 12;
    private const double TextEditorIntrinsicHorizontalInset = 2;
    private const double TextLineHeightMultiplier = 1.35;

    public static readonly DependencyProperty WorkspaceProperty = DependencyProperty.Register(
        nameof(Workspace),
        typeof(WorkspaceViewModel),
        typeof(InfiniteCanvas),
        new FrameworkPropertyMetadata(null, OnWorkspaceChanged));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid),
        typeof(bool),
        typeof(InfiniteCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SnapToGridProperty = DependencyProperty.Register(
        nameof(SnapToGrid),
        typeof(bool),
        typeof(InfiniteCanvas),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsCanvasLockedProperty = DependencyProperty.Register(
        nameof(IsCanvasLocked),
        typeof(bool),
        typeof(InfiniteCanvas),
        new FrameworkPropertyMetadata(false, OnCanvasLockChanged));

    private readonly SpatialHashIndex<BoardItemViewModel> _index = new(512);
    private readonly HashSet<BoardItemViewModel> _subscribedItems = [];
    private WorldPoint _origin;
    private double _zoom = 1;
    private InteractionMode _interaction;
    private Point _mouseDownScreen;
    private WorldPoint _mouseDownWorld;
    private WorldPoint _panStartOrigin;
    private Dictionary<Guid, ItemLayoutState>? _layoutBefore;
    private WorldRect? _selectionBox;
    private BoardItemViewModel? _resizeItem;
    private ResizeCorner? _resizeCorner;
    private bool _spacePressed;
    private TextBox? _textEditor;
    private BoardItemViewModel? _editingItem;
    private string? _editingOriginal;
    private bool _closingEditor;
    private BoardItemViewModel? _hoveredItem;

    public InfiniteCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        AllowDrop = true;
        Background = Brushes.Transparent;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        MouseWheel += OnMouseWheel;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseMove += OnMouseMove;
        MouseDown += OnAnyMouseDown;
        MouseUp += OnAnyMouseUp;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        LostKeyboardFocus += OnLostKeyboardFocus;
        DragOver += OnDragOver;
        Drop += OnDrop;
        MouseLeave += OnMouseLeave;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    public WorkspaceViewModel? Workspace
    {
        get => (WorkspaceViewModel?)GetValue(WorkspaceProperty);
        set => SetValue(WorkspaceProperty, value);
    }

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public bool SnapToGrid
    {
        get => (bool)GetValue(SnapToGridProperty);
        set => SetValue(SnapToGridProperty, value);
    }

    public bool IsCanvasLocked
    {
        get => (bool)GetValue(IsCanvasLockedProperty);
        set => SetValue(IsCanvasLockedProperty, value);
    }

    private static void OnCanvasLockChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not InfiniteCanvas canvas)
        {
            return;
        }

        if ((bool)eventArgs.NewValue)
        {
            canvas.CancelActiveInteraction();
            canvas._hoveredItem = null;
            canvas._spacePressed = false;
        }

        canvas.InvalidateVisual();
    }

    public WorldPoint ViewportCenter => ScreenToWorld(new Point(ActualWidth / 2, ActualHeight / 2));

    public event EventHandler<CanvasPasteEventArgs>? PasteRequested;
    public event EventHandler? CopyRequested;
    public event EventHandler<CanvasDropEventArgs>? ExternalDrop;

    public void CenterOn(BoardItemViewModel item)
    {
        if (IsCanvasLocked)
        {
            return;
        }

        _origin = new WorldPoint(
            item.Bounds.Center.X - ((ActualWidth / 2) / _zoom),
            item.Bounds.Center.Y - ((ActualHeight / 2) / _zoom));
        Workspace?.SetViewport(_origin, _zoom, interactionComplete: true);
        InvalidateVisual();
    }

    public void ResetViewport()
    {
        if (IsCanvasLocked)
        {
            return;
        }

        _origin = new WorldPoint(-120, -90);
        _zoom = 1;
        Workspace?.SetViewport(_origin, _zoom, interactionComplete: true);
        InvalidateVisual();
    }

    public void BeginTextEdit(BoardItemViewModel item)
    {
        if (IsCanvasLocked || Workspace?.IsReadOnly == true || item.Model.Content is not TextContent text)
        {
            return;
        }

        CommitTextEditor(save: true);
        Workspace?.SelectOnly(item);
        _editingItem = item;
        _editingOriginal = text.Text;
        var rect = GetTextEditorScreenRect(item.Bounds);
        _textEditor = new TextBox
        {
            Text = text.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = GetTextEditorFontSize(text),
            FontFamily = CardTextFontFamily,
            FontStyle = FontStyles.Normal,
            FontWeight = FontWeights.Normal,
            FontStretch = FontStretches.Normal,
            Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag),
            FlowDirection = FlowDirection.LeftToRight,
            Foreground = GetTextForeground(text),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = GetTextEditorPadding(),
            VerticalContentAlignment = VerticalAlignment.Top,
            TextAlignment = GetTextAlignment(text),
            FocusVisualStyle = null,
            Tag = item.Id
        };
        TextOptions.SetTextFormattingMode(_textEditor, TextFormattingMode.Ideal);
        _textEditor.KeyDown += OnEditorKeyDown;
        _textEditor.LostKeyboardFocus += OnEditorLostKeyboardFocus;
        AutomationProperties.SetName(_textEditor, "OmniRef text editor");
        Children.Add(_textEditor);
        SetLeft(_textEditor, rect.Left);
        SetTop(_textEditor, rect.Top);
        _textEditor.Width = Math.Max(80, rect.Width);
        _textEditor.Height = Math.Max(60, rect.Height);
        Panel.SetZIndex(_textEditor, int.MaxValue);
        var editor = _textEditor;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(
                () =>
                {
                    if (!ReferenceEquals(_textEditor, editor))
                    {
                        return;
                    }

                    FocusManager.SetFocusedElement(FocusManager.GetFocusScope(editor), editor);
                    editor.Focus();
                    Keyboard.Focus(editor);
                    editor.Select(editor.Text.Length, 0);
                }));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var background = TryFindResource("CanvasBackgroundBrush") as Brush ?? Brushes.Black;
        drawingContext.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        DrawGrid(drawingContext);

        if (Workspace is null)
        {
            return;
        }

        var visible = VisibleWorldRect().Inflate(220 / _zoom);
        var visibleItems = _index.Query(visible)
            .OrderBy(item => item.Kind == ItemKind.Frame ? 0 : 1)
            .ThenBy(item => item.Model.ZIndex)
            .ToList();
        var worldToScreen = CreateWorldToScreenTransform();
        foreach (var item in visibleItems)
        {
            DrawItem(drawingContext, item, worldToScreen);
        }

        if (_selectionBox.HasValue)
        {
            var rect = ToScreenRect(_selectionBox.Value);
            var fill = new SolidColorBrush(Color.FromArgb(35, 131, 145, 255));
            fill.Freeze();
            drawingContext.DrawRectangle(
                fill,
                new Pen((Brush)FindResource("AccentBrush"), 1),
                rect);
        }
    }

    private static void OnWorkspaceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        var canvas = (InfiniteCanvas)dependencyObject;
        canvas.DetachWorkspace((WorkspaceViewModel?)eventArgs.OldValue);
        canvas.AttachWorkspace((WorkspaceViewModel?)eventArgs.NewValue);
    }

    private void AttachWorkspace(WorkspaceViewModel? workspace)
    {
        if (workspace is null)
        {
            _index.Clear();
            InvalidateVisual();
            return;
        }

        _origin = workspace.Document.ViewportOrigin;
        _zoom = Math.Clamp(workspace.Document.Zoom, ViewportMath.MinimumZoom, ViewportMath.MaximumZoom);
        workspace.ItemsChanged += OnItemsChanged;
        workspace.VisualInvalidated += OnVisualInvalidated;
        workspace.FocusItemRequested += OnFocusItemRequested;
        RebuildIndex();
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(
                () =>
                {
                    if (!ReferenceEquals(Workspace, workspace))
                    {
                        return;
                    }

                    RequestVisiblePreviews();
                    InvalidateVisual();
                }));
    }

    private void DetachWorkspace(WorkspaceViewModel? workspace)
    {
        CommitTextEditor(save: true);
        if (workspace is not null)
        {
            workspace.ItemsChanged -= OnItemsChanged;
            workspace.VisualInvalidated -= OnVisualInvalidated;
            workspace.FocusItemRequested -= OnFocusItemRequested;
        }
        foreach (var item in _subscribedItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
        _subscribedItems.Clear();
        _index.Clear();
        _hoveredItem = null;
    }

    private void RebuildIndex()
    {
        foreach (var item in _subscribedItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
        _subscribedItems.Clear();
        _index.Clear();

        if (Workspace is not null)
        {
            foreach (var item in Workspace.Items)
            {
                _index.AddOrUpdate(item, item.Bounds);
                item.PropertyChanged += OnItemPropertyChanged;
                _subscribedItems.Add(item);
            }
        }
        InvalidateVisual();
    }

    private void OnItemsChanged(object? sender, EventArgs eventArgs) => RebuildIndex();

    private void RequestVisiblePreviews()
    {
        if (Workspace is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var visible = VisibleWorldRect().Inflate(220 / _zoom);
        foreach (var item in _index.Query(visible))
        {
            Rect previewRect;
            switch (item.Model.Content)
            {
                case ImageContent:
                    previewRect = ToScreenRect(item.Bounds);
                    break;
                case FileContent:
                case FolderContent:
                    var inner = GetCardInnerWorldRect(item.Bounds);
                    var iconSize = Math.Min(inner.Height * 0.56, Math.Min(inner.Width * 0.35, 88));
                    var screenIconSize = iconSize * _zoom;
                    previewRect = new Rect(0, 0, screenIconSize, screenIconSize);
                    break;
                default:
                    continue;
            }

            RequestPreview(item, previewRect);
        }
    }

    private void OnVisualInvalidated(object? sender, EventArgs eventArgs) => InvalidateVisual();

    private void OnFocusItemRequested(object? sender, BoardItemViewModel item) => CenterOn(item);

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (sender is not BoardItemViewModel item)
        {
            return;
        }
        if (eventArgs.PropertyName == nameof(BoardItemViewModel.Bounds))
        {
            _index.AddOrUpdate(item, item.Bounds);
        }
        InvalidateVisual();
    }

    private void DrawGrid(DrawingContext drawingContext)
    {
        if (!ShowGrid || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var brush = TryFindResource("CanvasGridBrush") as Brush ?? Brushes.DimGray;
        var step = GridMath.GetVisualStep(_zoom);
        var visible = VisibleWorldRect();
        var startX = Math.Floor(visible.Left / step) * step;
        var startY = Math.Floor(visible.Top / step) * step;
        var firstPoint = WorldToScreen(new WorldPoint(startX, startY));
        var screenStep = step * _zoom;
        const double radius = 1.2;
        for (var x = firstPoint.X; x <= ActualWidth; x += screenStep)
        {
            for (var y = firstPoint.Y; y <= ActualHeight; y += screenStep)
            {
                drawingContext.DrawEllipse(
                    brush,
                    null,
                    new Point(x, y),
                    radius,
                    radius);
            }
        }
    }

    private void DrawItem(
        DrawingContext context,
        BoardItemViewModel item,
        MatrixTransform worldToScreen)
    {
        var worldRect = new Rect(item.Bounds.X, item.Bounds.Y, item.Bounds.Width, item.Bounds.Height);
        var rect = ToScreenRect(item.Bounds);
        if (rect.Width < 2 || rect.Height < 2)
        {
            return;
        }

        if (item.Kind == ItemKind.Frame)
        {
            DrawFrame(context, item, worldRect, rect, worldToScreen);
            return;
        }

        var isHovered = !IsCanvasLocked && ReferenceEquals(_hoveredItem, item);

        var background = GetCardBackground(item);
        var borderBrush = item.IsMissing
            ? FindBrush("DangerBrush", Brushes.OrangeRed)
            : item.IsSelected
                ? FindBrush("AccentBrush", Brushes.CornflowerBlue)
                : isHovered
                    ? FindBrush("CardHoverBorderBrush", Brushes.LightGray)
                    : FindBrush("CardBorderBrush", FindBrush("BorderBrush", Brushes.Gray));
        context.DrawRectangle(background, null, rect);

        var inner = GetCardInnerWorldRect(item.Bounds);
        context.PushTransform(worldToScreen);
        try
        {
            switch (item.Model.Content)
            {
                case ImageContent:
                    DrawImageCard(context, item, worldRect, rect);
                    break;
                case FileContent:
                    DrawFileCard(context, item, inner, isFolder: false);
                    break;
                case FolderContent:
                    DrawFileCard(context, item, inner, isFolder: true);
                    break;
                case TextContent text when !ReferenceEquals(item, _editingItem):
                    DrawTextCard(context, item, inner, text);
                    break;
                case UrlContent url:
                    DrawUrlCard(context, item, inner, url);
                    break;
            }
        }
        finally
        {
            context.Pop();
        }

        context.DrawRectangle(
            null,
            new Pen(
                borderBrush,
                item.IsSelected || item.IsMissing ? SelectedBorderThickness : isHovered ? 1.35 : 1),
            rect);

        if (!IsCanvasLocked && item.IsSelected && Workspace?.SelectedItems.Count == 1)
        {
            DrawResizeHandles(context, rect);
        }

        if (item.IsMissing)
        {
            DrawBadge(context, rect, "!", FindBrush("DangerBrush", Brushes.OrangeRed));
        }
    }

    private void DrawFrame(
        DrawingContext context,
        BoardItemViewModel item,
        Rect worldRect,
        Rect screenRect,
        MatrixTransform worldToScreen)
    {
        var content = (FrameContent)item.Model.Content;
        var fill = IsColor(content.Color, "#337C8CFF")
            ? FindBrush("CardFrameFillBrush", new SolidColorBrush(Color.FromArgb(28, 124, 140, 255)))
            : ParseBrush(content.Color, FindBrush("CardFrameFillBrush", Brushes.Transparent));
        var stroke = item.IsSelected
            ? FindBrush("AccentBrush", Brushes.CornflowerBlue)
            : IsColor(item.Model.Style.Accent, "#FF7C8CFF")
                ? FindBrush("CardFrameBorderBrush", FindBrush("AccentBrush", Brushes.SlateBlue))
                : ParseBrush(item.Model.Style.Accent, Brushes.SlateBlue);
        context.DrawRectangle(fill, null, screenRect);
        context.PushTransform(worldToScreen);
        try
        {
            DrawFormattedText(
                context,
                item.DisplayTitle,
                new Rect(worldRect.X + 16, worldRect.Y + 10, Math.Max(1, worldRect.Width - 32), 30),
                15,
                FindBrush("TextPrimaryBrush", Brushes.White),
                FontWeights.SemiBold,
                TextAlignment.Left);
        }
        finally
        {
            context.Pop();
        }
        context.DrawRectangle(
            null,
            new Pen(stroke, item.IsSelected ? SelectedBorderThickness : 1),
            screenRect);

        if (!IsCanvasLocked && item.IsSelected && Workspace?.SelectedItems.Count == 1)
        {
            DrawResizeHandles(context, screenRect);
        }
    }

    private void DrawImageCard(
        DrawingContext context,
        BoardItemViewModel item,
        Rect destination,
        Rect previewDestination)
    {
        if (item.Preview is not null)
        {
            DrawImageCover(context, item.Preview, destination);
        }
        else
        {
            DrawFluentIcon(
                context,
                destination,
                ImageFilledGlyph,
                GetPreviewPlaceholderForeground(item));
            RequestPreview(item, previewDestination);
        }
    }

    private void DrawFileCard(DrawingContext context, BoardItemViewModel item, Rect inner, bool isFolder)
    {
        var iconSize = Math.Min(inner.Height * 0.56, Math.Min(inner.Width * 0.35, 88));
        var iconRect = new Rect(inner.X, inner.Y + ((inner.Height - iconSize) / 2), iconSize, iconSize);
        if (item.Preview is not null)
        {
            DrawImageFit(context, item.Preview, iconRect);
        }
        else
        {
            DrawFluentIcon(
                context,
                iconRect,
                isFolder ? FolderFilledGlyph : DocumentTextFilledGlyph,
                GetPreviewPlaceholderForeground(item));
            RequestPreview(item, ToScreenRect(iconRect));
        }

        var textRect = new Rect(
            iconRect.Right + 12,
            inner.Y + 4,
            Math.Max(1, inner.Right - iconRect.Right - 12),
            Math.Max(1, inner.Height - 8));
        DrawFormattedText(
            context,
            item.DisplayTitle,
            textRect,
            14,
            GetCardForeground(item),
            FontWeights.SemiBold,
            TextAlignment.Left,
            maxLines: 2,
            verticalCenter: !isFolder);
        if (!isFolder)
        {
            return;
        }

        var secondaryRect = new Rect(
            textRect.X,
            textRect.Y + Math.Min(textRect.Height * 0.55, 42),
            textRect.Width,
            Math.Max(1, textRect.Height * 0.4));
        DrawFormattedText(
            context,
            item.SecondaryText,
            secondaryRect,
            11,
            WithOpacity(GetCardForeground(item), 0.62),
            FontWeights.Normal,
            TextAlignment.Left,
            maxLines: 2);
    }

    private Brush GetPreviewPlaceholderForeground(BoardItemViewModel item)
    {
        if (item.IsMissing)
        {
            return FindBrush("DangerBrush", Brushes.OrangeRed);
        }

        var accent = GetCardAccent(item);
        return item.PreviewLoading ? WithOpacity(accent, 0.58) : accent;
    }

    private void DrawTextCard(
        DrawingContext context,
        BoardItemViewModel item,
        Rect inner,
        TextContent text)
    {
        var foreground = GetTextForeground(text);
        var isOverflowing = DrawFormattedText(
            context,
            text.Text,
            inner,
            GetTextWorldFontSize(text),
            foreground,
            FontWeights.Normal,
            GetTextAlignment(text));
        if (isOverflowing)
        {
            var background = GetTextBackground(text, item);
            DrawTextOverflowIndicator(context, inner, foreground, background);
        }
    }

    private void DrawTextOverflowIndicator(
        DrawingContext context,
        Rect textRect,
        Brush foreground,
        Brush background)
    {
        const double indicatorHeight = 24;
        var fadeHeight = Math.Min(textRect.Height, indicatorHeight * 1.6);
        var fadeRect = new Rect(
            textRect.X,
            textRect.Bottom - fadeHeight,
            textRect.Width,
            fadeHeight);
        if (background is SolidColorBrush solidBackground)
        {
            var transparent = solidBackground.Color;
            transparent.A = 0;
            var fade = new LinearGradientBrush(
                transparent,
                solidBackground.Color,
                new Point(0, 0),
                new Point(0, 1));
            fade.Freeze();
            context.DrawRectangle(fade, null, fadeRect);
        }

        const double indicatorWidth = 32;
        var indicatorRect = new Rect(
            textRect.Right - indicatorWidth,
            textRect.Bottom - indicatorHeight,
            indicatorWidth,
            indicatorHeight);
        context.DrawRoundedRectangle(
            background,
            new Pen(WithOpacity(foreground, 0.22), 1 / _zoom),
            indicatorRect,
            indicatorHeight / 2,
            indicatorHeight / 2);
        DrawFormattedText(
            context,
            "\u2026",
            indicatorRect,
            15,
            foreground,
            FontWeights.SemiBold,
            TextAlignment.Center,
            verticalCenter: true);
    }

    private void DrawUrlCard(DrawingContext context, BoardItemViewModel item, Rect inner, UrlContent url)
    {
        var iconSize = Math.Max(1, Math.Min(52, Math.Min(inner.Width * 0.25, inner.Height)));
        var glyphRect = new Rect(
            inner.X,
            inner.Y + ((inner.Height - iconSize) / 2),
            iconSize,
            iconSize);
        var glyphCorner = Math.Min(10, Math.Min(glyphRect.Width, glyphRect.Height) / 2);
        context.DrawRoundedRectangle(
            FindBrush("CardIconSurfaceBrush", FindBrush("SurfaceRaisedBrush", Brushes.DimGray)),
            null,
            glyphRect,
            glyphCorner,
            glyphCorner);
        var cardForeground = GetCardForeground(item);
        DrawFluentIcon(context, glyphRect, LinkFilledGlyph, cardForeground);
        var textRect = new Rect(glyphRect.Right + 10, inner.Y, Math.Max(1, inner.Right - glyphRect.Right - 10), inner.Height);
        DrawFormattedText(
            context,
            item.DisplayTitle,
            textRect,
            15,
            cardForeground,
            FontWeights.SemiBold,
            TextAlignment.Left,
            maxLines: 1);
        DrawFormattedText(
            context,
            url.Url,
            new Rect(textRect.X, textRect.Y + Math.Min(32, textRect.Height * 0.5), textRect.Width, textRect.Height * 0.45),
            11,
            WithOpacity(cardForeground, 0.62),
            FontWeights.Normal,
            TextAlignment.Left,
            maxLines: 2);
    }

    private void DrawFluentIcon(
        DrawingContext context,
        Rect destination,
        FluentIconGlyph icon,
        Brush foreground)
    {
        var availableSize = Math.Min(destination.Width, destination.Height);
        if (availableSize <= 0 || string.IsNullOrEmpty(icon.GlyphText))
        {
            return;
        }

        var iconSize = Math.Min(availableSize * 0.72, 64);
        var iconRect = new Rect(
            destination.X + ((destination.Width - iconSize) / 2),
            destination.Y + ((destination.Height - iconSize) / 2),
            iconSize,
            iconSize);
        var formatted = new FormattedText(
            icon.GlyphText,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            icon.GlyphTypeface,
            iconSize,
            foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = iconSize,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.None
        };
        var y = iconRect.Y + Math.Max(0, (iconRect.Height - formatted.Height) / 2);
        context.DrawText(formatted, new Point(iconRect.X, y));
    }

    private void DrawBadge(DrawingContext context, Rect cardRect, string text, Brush background)
    {
        var rect = new Rect(cardRect.Right - 24, cardRect.Top + 7, 17, 17);
        context.DrawEllipse(background, null, new Point(rect.X + 8.5, rect.Y + 8.5), 8.5, 8.5);
        DrawFormattedText(context, text, rect, 12, Brushes.White, FontWeights.Bold, TextAlignment.Center, verticalCenter: true);
    }

    private void RequestPreview(BoardItemViewModel item, Rect rect)
    {
        var pixels = (int)Math.Clamp(Math.Max(rect.Width, rect.Height) * VisualTreeHelper.GetDpi(this).DpiScaleX, 64, 2048);
        _ = Workspace?.EnsurePreviewAsync(item, pixels);
    }

    private static void DrawImageFit(DrawingContext context, ImageSource image, Rect destination)
    {
        var imageWidth = image.Width;
        var imageHeight = image.Height;
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return;
        }
        var scale = Math.Min(destination.Width / imageWidth, destination.Height / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        context.DrawImage(
            image,
            new Rect(
                destination.X + ((destination.Width - width) / 2),
                destination.Y + ((destination.Height - height) / 2),
                width,
                height));
    }

    private static void DrawImageCover(DrawingContext context, ImageSource image, Rect destination)
    {
        var imageWidth = image.Width;
        var imageHeight = image.Height;
        if (imageWidth <= 0 || imageHeight <= 0 || destination.Width <= 0 || destination.Height <= 0)
        {
            return;
        }

        var scale = Math.Max(destination.Width / imageWidth, destination.Height / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        context.PushClip(new RectangleGeometry(destination));
        context.DrawImage(
            image,
            new Rect(
                destination.X + ((destination.Width - width) / 2),
                destination.Y + ((destination.Height - height) / 2),
                width,
                height));
        context.Pop();
    }

    private bool DrawFormattedText(
        DrawingContext context,
        string text,
        Rect rect,
        double fontSize,
        Brush brush,
        FontWeight fontWeight,
        TextAlignment alignment,
        int maxLines = 0,
        bool verticalCenter = false)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || string.IsNullOrEmpty(text))
        {
            return false;
        }
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(CardTextFontFamily, FontStyles.Normal, fontWeight, FontStretches.Normal),
            fontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = rect.Width,
            TextAlignment = alignment,
            Trimming = TextTrimming.CharacterEllipsis
        };
        var availableHeight = maxLines > 0
            ? Math.Min(rect.Height, fontSize * TextLineHeightMultiplier * maxLines)
            : rect.Height;
        var isOverflowing = formatted.Height > availableHeight + 0.5;
        formatted.MaxTextHeight = availableHeight;
        var y = verticalCenter ? rect.Y + Math.Max(0, (rect.Height - formatted.Height) / 2) : rect.Y;
        context.DrawText(formatted, new Point(rect.X, y));
        return isOverflowing;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (Workspace is null || IsCanvasLocked)
        {
            eventArgs.Handled = IsCanvasLocked;
            return;
        }
        TakeKeyboardFocus();
        var anchor = eventArgs.GetPosition(this);
        var factor = eventArgs.Delta > 0 ? 1.12 : 1 / 1.12;
        var result = ViewportMath.ZoomAt(
            new WorldPoint(anchor.X, anchor.Y),
            _origin,
            _zoom,
            _zoom * factor);
        _origin = result.Origin;
        _zoom = result.Zoom;
        Workspace.SetViewport(_origin, _zoom, interactionComplete: true);
        RepositionEditor();
        InvalidateVisual();
        eventArgs.Handled = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (Workspace is null)
        {
            return;
        }
        if (_textEditor?.IsKeyboardFocusWithin == true)
        {
            if (_textEditor.IsMouseOver)
            {
                return;
            }
            CommitTextEditor(save: true);
        }
        TakeKeyboardFocus();
        _mouseDownScreen = eventArgs.GetPosition(this);
        _mouseDownWorld = ScreenToWorld(_mouseDownScreen);
        if (eventArgs.ClickCount == 2 && !IsCanvasLocked)
        {
            var doubleClicked = HitTestItem(_mouseDownWorld);
            if (doubleClicked is not null)
            {
                if (doubleClicked.Kind == ItemKind.Text)
                {
                    BeginTextEdit(doubleClicked);
                }
                else
                {
                    _ = Workspace.OpenItemAsync(doubleClicked);
                }
                eventArgs.Handled = true;
                return;
            }
        }
        if (_spacePressed)
        {
            BeginPan();
            eventArgs.Handled = true;
            return;
        }

        if (IsCanvasLocked)
        {
            eventArgs.Handled = true;
            return;
        }

        var selectedItem = Workspace.SelectedItems.Count == 1
            ? Workspace.SelectedItems[0]
            : null;
        if (!IsCanvasLocked &&
            !Workspace.IsReadOnly &&
            selectedItem is not null &&
            TryGetResizeCorner(ToScreenRect(selectedItem.Bounds), _mouseDownScreen, out var resizeCorner))
        {
            _resizeItem = selectedItem;
            _resizeCorner = resizeCorner;
            _layoutBefore = Workspace.CaptureLayout([selectedItem]);
            Workspace.BeginInteraction();
            _interaction = InteractionMode.Resize;
            CaptureMouse();
            eventArgs.Handled = true;
            return;
        }

        var hit = HitTestItem(_mouseDownWorld);

        if (hit is not null)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                Workspace.ToggleSelection(hit);
            }
            else if (!hit.IsSelected)
            {
                Workspace.SelectOnly(hit);
            }

            if (!IsCanvasLocked && !Workspace.IsReadOnly && hit.IsSelected)
            {
                var movable = Workspace.GetMovableSelection();
                _layoutBefore = Workspace.CaptureLayout(movable);
                Workspace.BeginInteraction();
                _interaction = InteractionMode.Move;
                CaptureMouse();
            }
        }
        else
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                Workspace.SelectOnly(null);
            }
            _interaction = InteractionMode.SelectBox;
            _selectionBox = new WorldRect(_mouseDownWorld.X, _mouseDownWorld.Y, 0, 0);
            CaptureMouse();
        }
        eventArgs.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (Workspace is null)
        {
            return;
        }

        var currentScreen = eventArgs.GetPosition(this);
        if (_interaction == InteractionMode.None)
        {
            if (!IsCanvasLocked)
            {
                UpdateHoveredItem(currentScreen);
            }
            return;
        }

        var currentWorld = ScreenToWorld(currentScreen);
        switch (_interaction)
        {
            case InteractionMode.Pan:
                _origin = new WorldPoint(
                    _panStartOrigin.X - ((currentScreen.X - _mouseDownScreen.X) / _zoom),
                    _panStartOrigin.Y - ((currentScreen.Y - _mouseDownScreen.Y) / _zoom));
                RepositionEditor();
                InvalidateVisual();
                break;
            case InteractionMode.Move when _layoutBefore is not null:
                UpdateMoveInteraction(currentWorld, IsGridSnappingActive);
                break;
            case InteractionMode.Resize when
                _layoutBefore is not null &&
                _resizeItem is not null &&
                _resizeCorner is { } resizeCorner:
                {
                    UpdateResizeInteraction(currentWorld, resizeCorner, IsGridSnappingActive);
                    break;
                }
            case InteractionMode.SelectBox:
                _selectionBox = WorldRect.FromPoints(_mouseDownWorld, currentWorld);
                InvalidateVisual();
                break;
        }
        eventArgs.Handled = true;
    }

    private void UpdateMoveInteraction(WorldPoint currentWorld, bool snapToGrid)
    {
        if (Workspace is null || _layoutBefore is null)
        {
            return;
        }

        var deltaX = currentWorld.X - _mouseDownWorld.X;
        var deltaY = currentWorld.Y - _mouseDownWorld.Y;
        if (snapToGrid)
        {
            var selectionBounds = WorldRect.Union(_layoutBefore.Values.Select(state => state.Bounds));
            var snappedDelta = GridMath.SnapTranslation(selectionBounds, deltaX, deltaY);
            deltaX = snappedDelta.X;
            deltaY = snappedDelta.Y;
        }

        foreach (var item in Workspace.Items)
        {
            if (_layoutBefore.TryGetValue(item.Id, out var before))
            {
                item.UpdateBounds(before.Bounds.Translate(deltaX, deltaY));
            }
        }
    }

    private void UpdateResizeInteraction(
        WorldPoint currentWorld,
        ResizeCorner resizeCorner,
        bool snapToGrid)
    {
        if (_layoutBefore is null || _resizeItem is null ||
            !_layoutBefore.TryGetValue(_resizeItem.Id, out var initial))
        {
            return;
        }

        var minWidth = _resizeItem.Kind == ItemKind.Frame ? 240 : 80;
        var minHeight = _resizeItem.Kind switch
        {
            ItemKind.Frame => 160,
            ItemKind.Text when _resizeItem.Model.Content is TextContent text =>
                GetMinimumTextCardHeight(text),
            _ => 60
        };
        var isLeft = resizeCorner is ResizeCorner.TopLeft or ResizeCorner.BottomLeft;
        var isTop = resizeCorner is ResizeCorner.TopLeft or ResizeCorner.TopRight;
        var draggedX = (isLeft ? initial.Bounds.Left : initial.Bounds.Right) +
                       (currentWorld.X - _mouseDownWorld.X);
        var draggedY = (isTop ? initial.Bounds.Top : initial.Bounds.Bottom) +
                       (currentWorld.Y - _mouseDownWorld.Y);
        if (snapToGrid)
        {
            draggedX = GridMath.Snap(draggedX);
            draggedY = GridMath.Snap(draggedY);
        }
        var fixedX = isLeft ? initial.Bounds.Right : initial.Bounds.Left;
        var fixedY = isTop ? initial.Bounds.Bottom : initial.Bounds.Top;
        var requestedSize = new WorldSize(
            isLeft ? fixedX - draggedX : draggedX - fixedX,
            isTop ? fixedY - draggedY : draggedY - fixedY);
        WorldSize size;
        if (_resizeItem.Kind == ItemKind.Image &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            // The bounds captured at mouse-down are the stable aspect-ratio
            // source for this gesture. Preview loading must not change the
            // ratio underneath the pointer and cause the first move to jump.
            var aspectSize = new WorldSize(
                initial.Bounds.Width,
                initial.Bounds.Height);
            size = ResizeMath.ConstrainToAspectRatio(
                aspectSize,
                requestedSize,
                new WorldSize(minWidth, minHeight));
        }
        else
        {
            size = new WorldSize(
                Math.Max(minWidth, requestedSize.Width),
                Math.Max(minHeight, requestedSize.Height));
        }
        _resizeItem.UpdateBounds(new(
            isLeft ? fixedX - size.Width : fixedX,
            isTop ? fixedY - size.Height : fixedY,
            size.Width,
            size.Height));
    }

    private void OnMouseLeave(object sender, MouseEventArgs eventArgs)
    {
        if (_interaction == InteractionMode.None && _hoveredItem is not null)
        {
            _hoveredItem = null;
            InvalidateVisual();
        }
    }

    private void UpdateHoveredItem(Point screenPoint)
    {
        var hoveredItem = HitTestItem(ScreenToWorld(screenPoint));
        if (ReferenceEquals(_hoveredItem, hoveredItem))
        {
            return;
        }

        _hoveredItem = hoveredItem;
        InvalidateVisual();
    }

    private bool IsGridSnappingActive =>
        SnapToGrid && (Keyboard.Modifiers & ModifierKeys.Control) == 0;

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (_interaction is InteractionMode.Pan or InteractionMode.Move or InteractionMode.Resize or InteractionMode.SelectBox)
        {
            CompleteInteraction();
            eventArgs.Handled = true;
        }
    }

    private void OnAnyMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        if (Workspace is null || IsCanvasLocked)
        {
            eventArgs.Handled = true;
            return;
        }

        TakeKeyboardFocus();
        _mouseDownScreen = eventArgs.GetPosition(this);
        _mouseDownWorld = ScreenToWorld(_mouseDownScreen);
        BeginPan();
        eventArgs.Handled = true;
    }

    private void OnAnyMouseUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Middle && _interaction == InteractionMode.Pan)
        {
            CompleteInteraction();
            eventArgs.Handled = true;
        }
    }

    private void BeginPan()
    {
        _interaction = InteractionMode.Pan;
        _panStartOrigin = _origin;
        CaptureMouse();
        Cursor = Cursors.Hand;
    }

    private void CompleteInteraction()
    {
        if (Workspace is null)
        {
            ResetInteraction();
            return;
        }
        switch (_interaction)
        {
            case InteractionMode.Pan:
                Workspace.SetViewport(_origin, _zoom, interactionComplete: true);
                break;
            case InteractionMode.Move when _layoutBefore is not null:
                Workspace.EndInteraction(_layoutBefore, "Move items", assignFrames: true);
                break;
            case InteractionMode.Resize when _layoutBefore is not null:
                Workspace.EndInteraction(_layoutBefore, "Resize item", assignFrames: false);
                break;
            case InteractionMode.SelectBox when _selectionBox.HasValue:
                Workspace.SelectInside(
                    _selectionBox.Value,
                    (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                break;
        }
        ResetInteraction();
    }

    private void CancelActiveInteraction()
    {
        if (Workspace is not null && _layoutBefore is not null)
        {
            Workspace.CancelInteraction(_layoutBefore);
        }

        CommitTextEditor(save: true);
        ResetInteraction();
    }

    private void ResetInteraction()
    {
        _interaction = InteractionMode.None;
        _selectionBox = null;
        _layoutBefore = null;
        _resizeItem = null;
        _resizeCorner = null;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    private void OnKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (Workspace is null || _textEditor?.IsKeyboardFocusWithin == true)
        {
            return;
        }
        if (IsCanvasLocked)
        {
            eventArgs.Handled = true;
            return;
        }
        if (eventArgs.Key == Key.Space)
        {
            _spacePressed = true;
            Cursor = Cursors.Hand;
            eventArgs.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            switch (eventArgs.Key)
            {
                case Key.A:
                    Workspace.SelectAll();
                    eventArgs.Handled = true;
                    return;
                case Key.Z when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                case Key.Y:
                    Workspace.Redo();
                    eventArgs.Handled = true;
                    return;
                case Key.Z:
                    Workspace.Undo();
                    eventArgs.Handled = true;
                    return;
                case Key.C:
                    CopyRequested?.Invoke(this, EventArgs.Empty);
                    eventArgs.Handled = true;
                    return;
                case Key.V:
                    PasteRequested?.Invoke(this, new CanvasPasteEventArgs(ViewportCenter));
                    eventArgs.Handled = true;
                    return;
                case Key.D0:
                case Key.NumPad0:
                    ResetViewport();
                    eventArgs.Handled = true;
                    return;
            }
        }

        switch (eventArgs.Key)
        {
            case Key.Delete when !Workspace.IsReadOnly:
            case Key.Back when !Workspace.IsReadOnly:
                Workspace.RemoveSelected();
                eventArgs.Handled = true;
                break;
            case Key.T when !Workspace.IsReadOnly:
                var text = Workspace.AddText(string.Empty, ViewportCenter);
                BeginTextEdit(text);
                eventArgs.Handled = true;
                break;
            case Key.F when !Workspace.IsReadOnly:
                Workspace.AddFrame("Group", ViewportCenter);
                eventArgs.Handled = true;
                break;
            case Key.Left when !Workspace.IsReadOnly:
                Workspace.NudgeSelected((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -10 : -1, 0);
                eventArgs.Handled = true;
                break;
            case Key.Right when !Workspace.IsReadOnly:
                Workspace.NudgeSelected((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1, 0);
                eventArgs.Handled = true;
                break;
            case Key.Up when !Workspace.IsReadOnly:
                Workspace.NudgeSelected(0, (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -10 : -1);
                eventArgs.Handled = true;
                break;
            case Key.Down when !Workspace.IsReadOnly:
                Workspace.NudgeSelected(0, (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1);
                eventArgs.Handled = true;
                break;
            case Key.Escape:
                if (_layoutBefore is not null)
                {
                    Workspace.CancelInteraction(_layoutBefore);
                }
                ResetInteraction();
                eventArgs.Handled = true;
                break;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key is Key.LeftCtrl or Key.RightCtrl && SnapToGrid)
        {
            RefreshActiveGridSnap();
        }

        if (eventArgs.Key == Key.Space)
        {
            _spacePressed = false;
            if (_interaction == InteractionMode.None)
            {
                Cursor = Cursors.Arrow;
            }
            eventArgs.Handled = true;
        }
    }

    private void RefreshActiveGridSnap()
    {
        var currentWorld = ScreenToWorld(Mouse.GetPosition(this));
        switch (_interaction)
        {
            case InteractionMode.Move when _layoutBefore is not null:
                UpdateMoveInteraction(currentWorld, snapToGrid: true);
                break;
            case InteractionMode.Resize when
                _layoutBefore is not null &&
                _resizeItem is not null &&
                _resizeCorner is { } resizeCorner:
                UpdateResizeInteraction(currentWorld, resizeCorner, snapToGrid: true);
                break;
        }
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        if (_textEditor is null)
        {
            _spacePressed = false;
            if (_interaction == InteractionMode.None)
            {
                Cursor = Cursors.Arrow;
            }
        }
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            CommitTextEditor(save: false);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            CommitTextEditor(save: true);
            eventArgs.Handled = true;
        }
    }

    private void OnEditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        if (!_closingEditor)
        {
            CommitTextEditor(save: true);
        }
    }

    private void CommitTextEditor(bool save)
    {
        if (_textEditor is null || _editingItem is null)
        {
            return;
        }
        _closingEditor = true;
        var editor = _textEditor;
        var item = _editingItem;
        var original = _editingOriginal ?? string.Empty;
        _textEditor = null;
        _editingItem = null;
        _editingOriginal = null;
        editor.KeyDown -= OnEditorKeyDown;
        editor.LostKeyboardFocus -= OnEditorLostKeyboardFocus;
        Children.Remove(editor);
        if (save)
        {
            Workspace?.UpdateTextWithUndo(item, editor.Text);
        }
        else if (item.Model.Content is TextContent text && text.Text != original)
        {
            item.ReplaceContent(text with { Text = original });
        }
        TakeKeyboardFocus();
        _closingEditor = false;
        InvalidateVisual();
    }

    private void RepositionEditor()
    {
        if (_textEditor is null || _editingItem is null)
        {
            return;
        }
        var rect = GetTextEditorScreenRect(_editingItem.Bounds);
        SetLeft(_textEditor, rect.Left);
        SetTop(_textEditor, rect.Top);
        _textEditor.Width = Math.Max(80, rect.Width);
        _textEditor.Height = Math.Max(60, rect.Height);
        if (_editingItem.Model.Content is TextContent text)
        {
            _textEditor.FontSize = GetTextEditorFontSize(text);
            _textEditor.Padding = GetTextEditorPadding();
        }
    }

    private static double GetTextWorldFontSize(TextContent text) =>
        double.IsFinite(text.FontSize) && text.FontSize > 0 ? text.FontSize : 18;

    private double GetTextEditorFontSize(TextContent text) => GetTextWorldFontSize(text) * _zoom;

    private double GetMinimumTextCardHeight(TextContent text)
    {
        // Keep the persisted card bounds large enough for one world-space line
        // of text plus the fixed world-space top and bottom padding.
        var lineHeight = GetTextWorldFontSize(text) * TextLineHeightMultiplier;
        return Math.Max(60, lineHeight + (CardVerticalPadding * 2));
    }

    private Thickness GetTextEditorPadding()
    {
        // WPF's TextBoxView adds a fixed two-DIP horizontal inset of its own.
        var horizontal = Math.Max(
            0,
            (CardHorizontalPadding * _zoom) - TextEditorIntrinsicHorizontalInset);
        var vertical = CardVerticalPadding * _zoom;
        return new Thickness(horizontal, vertical, horizontal, vertical);
    }

    private Rect GetTextEditorScreenRect(WorldRect bounds)
    {
        var rect = ToScreenRect(bounds);
        var horizontalOverflow = Math.Max(
            0,
            TextEditorIntrinsicHorizontalInset - (CardHorizontalPadding * _zoom));
        rect.Inflate(horizontalOverflow, 0);
        return rect;
    }

    private static TextAlignment GetTextAlignment(TextContent text) => text.Alignment switch
    {
        TextHorizontalAlignment.Center => TextAlignment.Center,
        TextHorizontalAlignment.Right => TextAlignment.Right,
        _ => TextAlignment.Left
    };

    private void OnDragOver(object sender, DragEventArgs eventArgs)
    {
        eventArgs.Effects = !IsCanvasLocked &&
                            (eventArgs.Data.GetDataPresent(DataFormats.FileDrop) ||
                             eventArgs.Data.GetDataPresent(DataFormats.UnicodeText))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs eventArgs)
    {
        if (Workspace is null || IsCanvasLocked || Workspace.IsReadOnly)
        {
            return;
        }
        var position = ScreenToWorld(eventArgs.GetPosition(this));
        var files = eventArgs.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var text = eventArgs.Data.GetData(DataFormats.UnicodeText) as string;
        ExternalDrop?.Invoke(this, new CanvasDropEventArgs(files, text, position));
        eventArgs.Handled = true;
    }

    private BoardItemViewModel? HitTestItem(WorldPoint point)
    {
        var area = new WorldRect(point.X - 0.5, point.Y - 0.5, 1, 1);
        return _index.Query(area)
            .Where(item => item.Bounds.Contains(point))
            .OrderByDescending(item => item.Kind != ItemKind.Frame)
            .ThenByDescending(item => item.Model.ZIndex)
            .FirstOrDefault();
    }

    private WorldRect VisibleWorldRect() => new(
        _origin.X,
        _origin.Y,
        ActualWidth / Math.Max(_zoom, 0.001),
        ActualHeight / Math.Max(_zoom, 0.001));

    private Point WorldToScreen(WorldPoint world)
    {
        var point = ViewportMath.WorldToScreen(world, _origin, _zoom);
        return new Point(point.X, point.Y);
    }

    private WorldPoint ScreenToWorld(Point screen) =>
        ViewportMath.ScreenToWorld(new WorldPoint(screen.X, screen.Y), _origin, _zoom);

    private Rect ToScreenRect(WorldRect world)
    {
        return ToScreenRect(new Rect(world.X, world.Y, world.Width, world.Height));
    }

    private Rect ToScreenRect(Rect world)
    {
        var topLeft = WorldToScreen(new WorldPoint(world.X, world.Y));
        return new Rect(topLeft.X, topLeft.Y, world.Width * _zoom, world.Height * _zoom);
    }

    private static Rect GetCardInnerWorldRect(WorldRect bounds) => new(
        bounds.X + CardHorizontalPadding,
        bounds.Y + CardVerticalPadding,
        Math.Max(1, bounds.Width - (CardHorizontalPadding * 2)),
        Math.Max(1, bounds.Height - (CardVerticalPadding * 2)));

    private MatrixTransform CreateWorldToScreenTransform()
    {
        var transform = new MatrixTransform(new Matrix(
            _zoom,
            0,
            0,
            _zoom,
            -_origin.X * _zoom,
            -_origin.Y * _zoom));
        transform.Freeze();
        return transform;
    }

    private void DrawResizeHandles(DrawingContext context, Rect rect)
    {
        var fill = Brushes.White;
        var stroke = new Pen(FindBrush("AccentBrush", Brushes.CornflowerBlue), SelectedBorderThickness);
        foreach (var corner in ResizeCorners)
        {
            var center = ResizeHandleCenter(rect, corner);
            context.DrawRectangle(
                fill,
                stroke,
                new Rect(
                    center.X - (ResizeHandleSize / 2),
                    center.Y - (ResizeHandleSize / 2),
                    ResizeHandleSize,
                    ResizeHandleSize));
        }
    }

    private static bool TryGetResizeCorner(Rect rect, Point point, out ResizeCorner corner)
    {
        var closestCorner = ResizeCorner.TopLeft;
        var closestDistance = double.PositiveInfinity;
        foreach (var candidate in ResizeCorners)
        {
            var center = ResizeHandleCenter(rect, candidate);
            var delta = point - center;
            var distance = delta.LengthSquared;
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestCorner = candidate;
            }
        }

        var deltaFromClosestCorner = point - ResizeHandleCenter(rect, closestCorner);
        var hitHalfSize = ResizeHandleHitSize / 2;
        if (Math.Abs(deltaFromClosestCorner.X) <= hitHalfSize &&
            Math.Abs(deltaFromClosestCorner.Y) <= hitHalfSize)
        {
            corner = closestCorner;
            return true;
        }

        corner = default;
        return false;
    }

    private static Point ResizeHandleCenter(Rect rect, ResizeCorner corner) => corner switch
    {
        ResizeCorner.TopLeft => new Point(rect.Left, rect.Top),
        ResizeCorner.TopRight => new Point(rect.Right, rect.Top),
        ResizeCorner.BottomLeft => new Point(rect.Left, rect.Bottom),
        ResizeCorner.BottomRight => new Point(rect.Right, rect.Bottom),
        _ => throw new ArgumentOutOfRangeException(nameof(corner), corner, null)
    };

    private Brush GetCardBackground(BoardItemViewModel item)
    {
        var themedBackground = FindBrush("CardBackgroundBrush", FindBrush("SurfaceBrush", Brushes.DimGray));
        var styleBackground = IsColor(item.Model.Style.Background, DefaultCardBackgroundColor.ToString())
            ? themedBackground
            : ParseBrush(item.Model.Style.Background, themedBackground);
        if (item.Model.Content is not TextContent text)
        {
            return styleBackground;
        }

        return IsColor(text.Background, DefaultTextBackgroundColor.ToString())
            ? FindBrush("CardTextBackgroundBrush", styleBackground)
            : ParseBrush(text.Background, styleBackground);
    }

    private Brush GetCardForeground(BoardItemViewModel item) => item.Model.Content is TextContent text
        ? GetTextForeground(text)
        : GetThemeAwareBrush(
            item.Model.Style.Foreground,
            DefaultCardForegroundColor,
            "TextPrimaryBrush",
            Brushes.White);

    private Brush GetCardAccent(BoardItemViewModel item) => GetThemeAwareBrush(
        item.Model.Style.Accent,
        Color.FromArgb(0xFF, 0x7C, 0x8C, 0xFF),
        "AccentBrush",
        Brushes.CornflowerBlue);

    private Brush GetTextForeground(TextContent text) => GetThemeAwareBrush(
        text.Foreground,
        DefaultCardForegroundColor,
        "TextPrimaryBrush",
        Brushes.White);

    private Brush GetTextBackground(TextContent text, BoardItemViewModel item) =>
        IsColor(text.Background, DefaultTextBackgroundColor.ToString())
            ? FindBrush("CardTextBackgroundBrush", GetCardBackground(item))
            : ParseBrush(text.Background, GetCardBackground(item));

    private Brush GetThemeAwareBrush(
        string value,
        Color legacyDefault,
        string resourceKey,
        Brush fallback)
    {
        return IsColor(value, legacyDefault.ToString())
            ? FindBrush(resourceKey, fallback)
            : ParseBrush(value, fallback);
    }

    private Brush FindBrush(string resourceKey, Brush fallback) =>
        TryFindResource(resourceKey) as Brush ?? fallback;

    private static bool IsColor(string value, string expected)
    {
        try
        {
            if (ColorConverter.ConvertFromString(value) is not Color parsed ||
                ColorConverter.ConvertFromString(expected) is not Color expectedColor)
            {
                return false;
            }

            return parsed.A == expectedColor.A &&
                   parsed.R == expectedColor.R &&
                   parsed.G == expectedColor.G &&
                   parsed.B == expectedColor.B;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static Brush ParseBrush(string value, Brush fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(value) is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
        }
        return fallback;
    }

    private static Brush WithOpacity(Brush brush, double opacity)
    {
        var clone = brush.Clone();
        clone.Opacity = opacity;
        clone.Freeze();
        return clone;
    }

    private static FluentIconGlyph CreateFluentIconGlyph(Icon icon) => new()
    {
        Icon = icon,
        IconSize = IconSize.Size48,
        IconVariant = IconVariant.Filled
    };

    private sealed class FluentIconGlyph : FluentIcon
    {
        public string GlyphText => IconText;

        public Typeface GlyphTypeface => IconFont;
    }

    private void TakeKeyboardFocus()
    {
        Focus();
        Keyboard.Focus(this);
    }

    private enum InteractionMode
    {
        None,
        Pan,
        Move,
        Resize,
        SelectBox
    }

    private enum ResizeCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }
}

public sealed class CanvasPasteEventArgs(WorldPoint position) : EventArgs
{
    public WorldPoint Position { get; } = position;
}

public sealed class CanvasDropEventArgs(
    IReadOnlyList<string> files,
    string? text,
    WorldPoint position) : EventArgs
{
    public IReadOnlyList<string> Files { get; } = files;
    public string? Text { get; } = text;
    public WorldPoint Position { get; } = position;
}
