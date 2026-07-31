using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OmniRef.App.ViewModels;
using OmniRef.Core.Models;
using OmniRef.Core.Services;

namespace OmniRef.App.Controls;

public sealed class InfiniteCanvas : Canvas
{
    private const double BaseGridStep = 32;

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
    private bool _spacePressed;
    private TextBox? _textEditor;
    private BoardItemViewModel? _editingItem;
    private string? _editingOriginal;
    private bool _closingEditor;

    public InfiniteCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        AllowDrop = true;
        Background = Brushes.Transparent;
        SnapsToDevicePixels = true;

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

    public WorldPoint ViewportCenter => ScreenToWorld(new Point(ActualWidth / 2, ActualHeight / 2));

    public event EventHandler<CanvasPasteEventArgs>? PasteRequested;
    public event EventHandler? CopyRequested;
    public event EventHandler<CanvasDropEventArgs>? ExternalDrop;

    public void CenterOn(BoardItemViewModel item)
    {
        _origin = new WorldPoint(
            item.Bounds.Center.X - ((ActualWidth / 2) / _zoom),
            item.Bounds.Center.Y - ((ActualHeight / 2) / _zoom));
        Workspace?.SetViewport(_origin, _zoom, interactionComplete: true);
        InvalidateVisual();
    }

    public void ResetViewport()
    {
        _origin = new WorldPoint(-120, -90);
        _zoom = 1;
        Workspace?.SetViewport(_origin, _zoom, interactionComplete: true);
        InvalidateVisual();
    }

    public void BeginTextEdit(BoardItemViewModel item)
    {
        if (Workspace?.IsReadOnly == true || item.Model.Content is not TextContent text)
        {
            return;
        }

        CommitTextEditor(save: true);
        Workspace?.SelectOnly(item);
        _editingItem = item;
        _editingOriginal = text.Text;
        var rect = ToScreenRect(item.Bounds);
        _textEditor = new TextBox
        {
            Text = text.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = Math.Clamp(text.FontSize * _zoom, 10, 42),
            Foreground = ParseBrush(text.Foreground, Brushes.White),
            Background = ParseBrush(text.Background, Brushes.Transparent),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(10),
            Tag = item.Id
        };
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
        foreach (var item in visibleItems)
        {
            DrawItem(drawingContext, item);
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
        if (!ShowGrid || _zoom < 0.2 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var brush = TryFindResource("CanvasGridBrush") as Brush ?? Brushes.DimGray;
        var step = BaseGridStep;
        while (step * _zoom < 28)
        {
            step *= 2;
        }

        var visible = VisibleWorldRect();
        var startX = Math.Floor(visible.Left / step) * step;
        var startY = Math.Floor(visible.Top / step) * step;
        var firstPoint = WorldToScreen(new WorldPoint(startX, startY));
        var screenStep = step * _zoom;
        const double radius = 1;
        for (var x = firstPoint.X; x <= ActualWidth; x += screenStep)
        {
            for (var y = firstPoint.Y; y <= ActualHeight; y += screenStep)
            {
                drawingContext.DrawEllipse(brush, null, new Point(x, y), radius, radius);
            }
        }
    }

    private void DrawItem(DrawingContext context, BoardItemViewModel item)
    {
        var rect = ToScreenRect(item.Bounds);
        if (rect.Width < 2 || rect.Height < 2)
        {
            return;
        }

        if (item.Kind == ItemKind.Frame)
        {
            DrawFrame(context, item, rect);
            return;
        }

        var corner = Math.Clamp(item.Model.Style.CornerRadius * _zoom, 3, 16);
        var shadowRect = new Rect(rect.X + 2, rect.Y + 3, rect.Width, rect.Height);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(45, 0, 0, 0)),
            null,
            shadowRect,
            corner,
            corner);
        var background = item.Model.Content is TextContent textContent
            ? ParseBrush(textContent.Background, ParseBrush(item.Model.Style.Background, Brushes.DimGray))
            : ParseBrush(item.Model.Style.Background, Brushes.DimGray);
        var borderBrush = item.IsMissing
            ? TryFindResource("DangerBrush") as Brush ?? Brushes.OrangeRed
            : item.IsSelected
                ? TryFindResource("AccentBrush") as Brush ?? Brushes.CornflowerBlue
                : TryFindResource("BorderBrush") as Brush ?? Brushes.Gray;
        context.DrawRoundedRectangle(
            background,
            new Pen(borderBrush, item.IsSelected || item.IsMissing ? 2 : 1),
            rect,
            corner,
            corner);

        var inner = new Rect(
            rect.X + Math.Clamp(12 * _zoom, 5, 16),
            rect.Y + Math.Clamp(10 * _zoom, 5, 14),
            Math.Max(1, rect.Width - Math.Clamp(24 * _zoom, 10, 32)),
            Math.Max(1, rect.Height - Math.Clamp(20 * _zoom, 10, 28)));
        switch (item.Model.Content)
        {
            case ImageContent:
                DrawImageCard(context, item, inner);
                break;
            case FileContent:
                DrawFileCard(context, item, inner, isFolder: false);
                break;
            case FolderContent:
                DrawFileCard(context, item, inner, isFolder: true);
                break;
            case TextContent text:
                DrawTextCard(context, item, inner, text);
                break;
            case UrlContent url:
                DrawUrlCard(context, item, inner, url);
                break;
        }

        if (item.IsSelected && Workspace?.SelectedItems.Count == 1)
        {
            var handle = ResizeHandleRect(rect);
            context.DrawRectangle(
                TryFindResource("AccentBrush") as Brush ?? Brushes.CornflowerBlue,
                new Pen(Brushes.White, 1),
                handle);
        }

        if (item.IsMissing)
        {
            DrawBadge(context, rect, "!", TryFindResource("DangerBrush") as Brush ?? Brushes.OrangeRed);
        }
    }

    private void DrawFrame(DrawingContext context, BoardItemViewModel item, Rect rect)
    {
        var content = (FrameContent)item.Model.Content;
        var fill = ParseBrush(content.Color, new SolidColorBrush(Color.FromArgb(28, 124, 140, 255)));
        var stroke = item.IsSelected
            ? TryFindResource("AccentBrush") as Brush ?? Brushes.CornflowerBlue
            : ParseBrush(item.Model.Style.Accent, Brushes.SlateBlue);
        context.DrawRoundedRectangle(fill, new Pen(stroke, item.IsSelected ? 2 : 1), rect, 12, 12);
        DrawFormattedText(
            context,
            item.DisplayTitle,
            new Rect(rect.X + 14, rect.Y + 8, Math.Max(1, rect.Width - 28), 30),
            Math.Clamp(15 * _zoom, 8, 24),
            TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White,
            FontWeights.SemiBold,
            TextAlignment.Left);

        if (item.IsSelected && Workspace?.SelectedItems.Count == 1)
        {
            context.DrawRectangle(
                TryFindResource("AccentBrush") as Brush ?? Brushes.CornflowerBlue,
                new Pen(Brushes.White, 1),
                ResizeHandleRect(rect));
        }
    }

    private void DrawImageCard(DrawingContext context, BoardItemViewModel item, Rect inner)
    {
        if (item.Preview is not null)
        {
            DrawImageFit(context, item.Preview, inner);
        }
        else
        {
            DrawCenteredGlyph(
                context,
                inner,
                "▧",
                ParseBrush(item.Model.Style.Foreground, Brushes.White));
            RequestPreview(item, inner);
        }

        if (_zoom >= 0.35)
        {
            var titleRect = new Rect(inner.X, inner.Bottom - Math.Min(30, inner.Height / 3), inner.Width, Math.Min(30, inner.Height / 3));
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 15, 17, 21)), null, titleRect);
            DrawFormattedText(
                context,
                item.DisplayTitle,
                new Rect(titleRect.X + 7, titleRect.Y + 4, Math.Max(1, titleRect.Width - 14), Math.Max(1, titleRect.Height - 8)),
                Math.Clamp(12 * _zoom, 7, 18),
                Brushes.White,
                FontWeights.SemiBold,
                TextAlignment.Left);
        }
    }

    private void DrawFileCard(DrawingContext context, BoardItemViewModel item, Rect inner, bool isFolder)
    {
        var iconSize = Math.Min(inner.Height * 0.5, Math.Min(inner.Width * 0.35, 88 * _zoom));
        var iconRect = new Rect(inner.X, inner.Y + ((inner.Height - iconSize) / 2), iconSize, iconSize);
        if (item.Preview is not null)
        {
            DrawImageFit(context, item.Preview, iconRect);
        }
        else
        {
            DrawCenteredGlyph(
                context,
                iconRect,
                isFolder ? "▰" : "▤",
                ParseBrush(item.Model.Style.Foreground, Brushes.White));
            RequestPreview(item, iconRect);
        }

        var textRect = new Rect(
            iconRect.Right + Math.Clamp(12 * _zoom, 6, 18),
            inner.Y + 4,
            Math.Max(1, inner.Right - iconRect.Right - Math.Clamp(12 * _zoom, 6, 18)),
            Math.Max(1, inner.Height - 8));
        DrawFormattedText(
            context,
            item.DisplayTitle,
            textRect,
            Math.Clamp(14 * _zoom, 7, 22),
            ParseBrush(item.Model.Style.Foreground, Brushes.White),
            FontWeights.SemiBold,
            TextAlignment.Left,
            maxLines: 2);
        var secondaryRect = new Rect(
            textRect.X,
            textRect.Y + Math.Min(textRect.Height * 0.55, 42 * _zoom),
            textRect.Width,
            Math.Max(1, textRect.Height * 0.4));
        DrawFormattedText(
            context,
            item.SecondaryText,
            secondaryRect,
            Math.Clamp(11 * _zoom, 6, 16),
            WithOpacity(ParseBrush(item.Model.Style.Foreground, Brushes.White), 0.68),
            FontWeights.Normal,
            TextAlignment.Left,
            maxLines: 2);
    }

    private void DrawTextCard(
        DrawingContext context,
        BoardItemViewModel item,
        Rect inner,
        TextContent text)
    {
        DrawFormattedText(
            context,
            text.Text,
            inner,
            Math.Clamp(text.FontSize * _zoom, 6, 52),
            ParseBrush(text.Foreground, Brushes.White),
            FontWeights.Normal,
            text.Alignment switch
            {
                TextHorizontalAlignment.Center => TextAlignment.Center,
                TextHorizontalAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left
            });
    }

    private void DrawUrlCard(DrawingContext context, BoardItemViewModel item, Rect inner, UrlContent url)
    {
        var glyphRect = new Rect(inner.X, inner.Y, Math.Min(44 * _zoom, inner.Width * 0.25), inner.Height);
        var cardForeground = ParseBrush(item.Model.Style.Foreground, Brushes.White);
        DrawCenteredGlyph(context, glyphRect, "↗", cardForeground);
        var textRect = new Rect(glyphRect.Right + 8, inner.Y, Math.Max(1, inner.Right - glyphRect.Right - 8), inner.Height);
        DrawFormattedText(
            context,
            item.DisplayTitle,
            textRect,
            Math.Clamp(15 * _zoom, 8, 23),
            cardForeground,
            FontWeights.SemiBold,
            TextAlignment.Left,
            maxLines: 1);
        DrawFormattedText(
            context,
            url.Url,
            new Rect(textRect.X, textRect.Y + Math.Min(32 * _zoom, textRect.Height * 0.5), textRect.Width, textRect.Height * 0.45),
            Math.Clamp(11 * _zoom, 6, 16),
            WithOpacity(cardForeground, 0.68),
            FontWeights.Normal,
            TextAlignment.Left,
            maxLines: 2);
    }

    private void DrawCenteredGlyph(DrawingContext context, Rect rect, string glyph, Brush? foreground = null) =>
        DrawFormattedText(
            context,
            glyph,
            rect,
            Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.46, 10, 56),
            foreground ?? TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.LightGray,
            FontWeights.Normal,
            TextAlignment.Center,
            verticalCenter: true);

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

    private void DrawFormattedText(
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
            return;
        }
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, fontWeight, FontStretches.Normal),
            fontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = rect.Width,
            MaxTextHeight = maxLines > 0 ? Math.Min(rect.Height, fontSize * 1.35 * maxLines) : rect.Height,
            TextAlignment = alignment,
            Trimming = TextTrimming.CharacterEllipsis
        };
        var y = verticalCenter ? rect.Y + Math.Max(0, (rect.Height - formatted.Height) / 2) : rect.Y;
        context.DrawText(formatted, new Point(rect.X, y));
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (Workspace is null)
        {
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
        if (Workspace is null || _textEditor?.IsKeyboardFocusWithin == true)
        {
            return;
        }
        TakeKeyboardFocus();
        _mouseDownScreen = eventArgs.GetPosition(this);
        _mouseDownWorld = ScreenToWorld(_mouseDownScreen);
        if (eventArgs.ClickCount == 2)
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

        var hit = HitTestItem(_mouseDownWorld);
        if (hit is not null && hit.IsSelected && Workspace.SelectedItems.Count == 1 &&
            ResizeHandleRect(ToScreenRect(hit.Bounds)).Contains(_mouseDownScreen) &&
            !Workspace.IsReadOnly)
        {
            _resizeItem = hit;
            _layoutBefore = Workspace.CaptureLayout([hit]);
            Workspace.BeginInteraction();
            _interaction = InteractionMode.Resize;
            CaptureMouse();
            eventArgs.Handled = true;
            return;
        }

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

            if (!Workspace.IsReadOnly && hit.IsSelected)
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
        if (Workspace is null || _interaction == InteractionMode.None)
        {
            return;
        }
        var currentScreen = eventArgs.GetPosition(this);
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
                var deltaX = currentWorld.X - _mouseDownWorld.X;
                var deltaY = currentWorld.Y - _mouseDownWorld.Y;
                foreach (var item in Workspace.Items)
                {
                    if (_layoutBefore.TryGetValue(item.Id, out var before))
                    {
                        item.UpdateBounds(before.Bounds.Translate(deltaX, deltaY));
                    }
                }
                break;
            case InteractionMode.Resize when _layoutBefore is not null && _resizeItem is not null:
                if (_layoutBefore.TryGetValue(_resizeItem.Id, out var initial))
                {
                    var minWidth = _resizeItem.Kind == ItemKind.Frame ? 240 : 80;
                    var minHeight = _resizeItem.Kind == ItemKind.Frame ? 160 : 60;
                    var width = Math.Max(minWidth, initial.Bounds.Width + (currentWorld.X - _mouseDownWorld.X));
                    var height = Math.Max(minHeight, initial.Bounds.Height + (currentWorld.Y - _mouseDownWorld.Y));
                    if (_resizeItem.Kind == ItemKind.Image &&
                        (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                    {
                        var ratio = initial.Bounds.Width / Math.Max(1, initial.Bounds.Height);
                        if (Math.Abs(width - initial.Bounds.Width) >= Math.Abs(height - initial.Bounds.Height))
                        {
                            height = width / ratio;
                        }
                        else
                        {
                            width = height * ratio;
                        }
                    }
                    _resizeItem.UpdateBounds(new(
                        initial.Bounds.X,
                        initial.Bounds.Y,
                        width,
                        height));
                }
                break;
            case InteractionMode.SelectBox:
                _selectionBox = WorldRect.FromPoints(_mouseDownWorld, currentWorld);
                InvalidateVisual();
                break;
        }
        eventArgs.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (_interaction is InteractionMode.Move or InteractionMode.Resize or InteractionMode.SelectBox)
        {
            CompleteInteraction();
            eventArgs.Handled = true;
        }
    }

    private void OnAnyMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Middle && Workspace is not null)
        {
            TakeKeyboardFocus();
            _mouseDownScreen = eventArgs.GetPosition(this);
            _mouseDownWorld = ScreenToWorld(_mouseDownScreen);
            BeginPan();
            eventArgs.Handled = true;
        }
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

    private void ResetInteraction()
    {
        _interaction = InteractionMode.None;
        _selectionBox = null;
        _layoutBefore = null;
        _resizeItem = null;
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
        var rect = ToScreenRect(_editingItem.Bounds);
        SetLeft(_textEditor, rect.Left);
        SetTop(_textEditor, rect.Top);
        _textEditor.Width = Math.Max(80, rect.Width);
        _textEditor.Height = Math.Max(60, rect.Height);
        if (_editingItem.Model.Content is TextContent text)
        {
            _textEditor.FontSize = Math.Clamp(text.FontSize * _zoom, 10, 42);
        }
    }

    private void OnDragOver(object sender, DragEventArgs eventArgs)
    {
        eventArgs.Effects = eventArgs.Data.GetDataPresent(DataFormats.FileDrop) ||
                             eventArgs.Data.GetDataPresent(DataFormats.UnicodeText)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs eventArgs)
    {
        if (Workspace is null || Workspace.IsReadOnly)
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
        var topLeft = WorldToScreen(new WorldPoint(world.X, world.Y));
        return new Rect(topLeft.X, topLeft.Y, world.Width * _zoom, world.Height * _zoom);
    }

    private static Rect ResizeHandleRect(Rect rect) => new(rect.Right - 7, rect.Bottom - 7, 12, 12);

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
