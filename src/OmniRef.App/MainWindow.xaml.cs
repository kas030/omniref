using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OmniRef.App.Controls;
using OmniRef.App.Services;
using OmniRef.App.ViewModels;
using OmniRef.Core.Interfaces;
using OmniRef.Core.Models;
using OmniRef.Core.Services;
using OmniRef.Infrastructure.Windows.Diagnostics;
using OmniRef.Infrastructure.Windows.Settings;
using OmniRef.Infrastructure.Windows.Shell;

namespace OmniRef.App;

public partial class MainWindow : Window
{
    private const string ClipboardFormat = "OmniRef.Items.v1";
    private const int GetMinMaxInfoMessage = 0x0024;
    private const double WorkspaceTabPreferredWidth = 180;
    private const double WorkspaceTabMinimumWidth = 104;
    private const double WorkspaceTabHorizontalMargin = 4;
    private const double WorkspaceTabReorderEdgeInsetRatio = 0.25;
    private const int WorkspaceTabReorderAnimationMilliseconds = 100;
    private const int WorkspaceTabSettleAnimationMilliseconds = 80;

    public static readonly DependencyProperty ShowCanvasGridProperty = DependencyProperty.Register(
        nameof(ShowCanvasGrid),
        typeof(bool),
        typeof(MainWindow),
        new PropertyMetadata(false));

    public static readonly DependencyProperty SnapToGridProperty = DependencyProperty.Register(
        nameof(SnapToGrid),
        typeof(bool),
        typeof(MainWindow),
        new PropertyMetadata(false));

    public static readonly DependencyProperty CurrentThemeProperty = DependencyProperty.Register(
        nameof(CurrentTheme),
        typeof(AppTheme),
        typeof(MainWindow),
        new PropertyMetadata(AppTheme.System));

    public static readonly DependencyProperty WorkspaceTabWidthProperty = DependencyProperty.Register(
        nameof(WorkspaceTabWidth),
        typeof(double),
        typeof(MainWindow),
        new PropertyMetadata(WorkspaceTabPreferredWidth));

    private readonly MainWindowViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly AppSettingsStore _settingsStore;
    private readonly IWorkspaceStore _workspaceStore;
    private readonly IHotkeyService _hotkeyService;
    private readonly PreviewCache _previewCache;
    private readonly ThemeManager _themeManager;
    private readonly IClipboardImporter _clipboardImporter;
    private readonly RollingFileLogger _logger;
    private readonly JsonSerializerOptions _clipboardJsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private WindowsTrayIcon? _trayIcon;
    private HwndSource? _windowSource;
    private bool _allowClose;
    private bool _loaded;
    private int _themeTransitionVersion;
    private Point _titleBarDragStart;
    private bool _titleBarDragPending;
    private Point _workspaceTabDragStart;
    private WorkspaceViewModel? _workspaceTabDragItem;
    private double _workspaceTabDragPointerOffsetX;
    private bool _workspaceTabDragActive;
    private bool _workspaceTabOrderChanged;
    private bool _workspaceTabsOverflow;

    public MainWindow(
        MainWindowViewModel viewModel,
        AppSettings settings,
        AppSettingsStore settingsStore,
        IWorkspaceStore workspaceStore,
        IHotkeyService hotkeyService,
        PreviewCache previewCache,
        ThemeManager themeManager,
        IClipboardImporter clipboardImporter,
        RollingFileLogger logger)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        _settingsStore = settingsStore;
        _workspaceStore = workspaceStore;
        _hotkeyService = hotkeyService;
        _previewCache = previewCache;
        _themeManager = themeManager;
        _clipboardImporter = clipboardImporter;
        _logger = logger;
        DataContext = viewModel;
        WorkspaceTabs.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(WorkspaceTabs_ScrollChanged));
        _viewModel.Workspaces.CollectionChanged += Workspaces_CollectionChanged;

        RestoreWindowState();
        Topmost = settings.AlwaysOnTop;
        ShowCanvasGrid = settings.ShowCanvasGrid;
        SnapToGrid = settings.SnapToGrid;
        CurrentTheme = settings.Theme;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Activated += OnActivated;
        StateChanged += OnWindowStateChanged;
        Application.Current.SessionEnding += OnSessionEnding;
    }

    public double WorkspaceTabWidth
    {
        get => (double)GetValue(WorkspaceTabWidthProperty);
        private set => SetValue(WorkspaceTabWidthProperty, value);
    }

    public async Task OpenActivationArgumentsAsync(IReadOnlyList<string> arguments)
    {
        ShowAndActivate();
        foreach (var path in arguments.Where(path =>
                     path.EndsWith(".omniref", StringComparison.OrdinalIgnoreCase) && File.Exists(path)))
        {
            try
            {
                await _viewModel.OpenAsync(path);
            }
            catch (InvalidOperationException exception)
            {
                ShowError(exception.Message);
            }
        }
        SaveSettings(cleanExit: false);
    }

    public async Task RequestExitAsync()
    {
        if (_allowClose)
        {
            return;
        }

        var flushFailed = false;
        try
        {
            await _viewModel.FlushAllAsync();
            flushFailed = _viewModel.Workspaces.Any(
                workspace => workspace.SaveState == WorkspaceSaveState.Failed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Error("Could not flush workspaces during exit.", exception);
            flushFailed = true;
        }

        if (flushFailed)
        {
            var result = MessageBox.Show(
                this,
                _viewModel.Localization["ConfirmCloseDirty"],
                "OmniRef",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _allowClose = true;
        try
        {
            SaveSettings(cleanExit: !flushFailed);
        }
        finally
        {
            CleanupShellIntegration();
            Close();
            Application.Current.Shutdown();
        }
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            WindowsWindowAnimation.TryShow(handle);
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            EnableSystemWindowTransitions();
            SystemCommands.RestoreWindow(this);
        }
        Activate();
        Topmost = _settings.AlwaysOnTop;
        Focus();
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        var handle = new WindowInteropHelper(this).Handle;
        _hotkeyService.Pressed += OnHotkeyPressed;
        var registered = _hotkeyService.Register(
            handle,
            new HotkeyGesture(
                Control: true,
                Alt: true,
                Shift: false,
                Windows: false,
                KeyInterop.VirtualKeyFromKey(Key.Space)));
        if (!registered)
        {
            ShowError(_viewModel.Localization["HotkeyConflict"]);
        }
        CreateTrayIcon(handle);
        UpdateWorkspaceTabLayout();
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => FindCanvas()?.Focus()));
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }
        if (_settings.CloseToTray)
        {
            eventArgs.Cancel = true;
            HideToTray();
            return;
        }
        eventArgs.Cancel = true;
        _ = RequestExitAsync();
    }

    private void OnActivated(object? sender, EventArgs eventArgs) => _viewModel.RefreshReferences();

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message == GetMinMaxInfoMessage)
        {
            handled = WindowsWindowBounds.TryApplyWindowBounds(
                windowHandle,
                longParameter,
                MinWidth,
                MinHeight);
        }

        return IntPtr.Zero;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (TryBeginWorkspaceTabDragFromTitleBar(eventArgs))
        {
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.ClickCount == 2)
        {
            EndPendingTitleBarDrag(sender as UIElement);
            MaximizeRestoreWindow_Click(sender, eventArgs);
            eventArgs.Handled = true;
            return;
        }

        if (sender is UIElement titleBar)
        {
            _titleBarDragStart = eventArgs.GetPosition(this);
            _titleBarDragPending = true;
            titleBar.CaptureMouse();
            eventArgs.Handled = true;
        }
    }

    private bool TryBeginWorkspaceTabDragFromTitleBar(MouseButtonEventArgs eventArgs)
    {
        if (WindowState != WindowState.Maximized)
        {
            return false;
        }

        var position = eventArgs.GetPosition(WorkspaceTabs);
        if (FindWorkspaceTabAtX(WorkspaceTabs, position.X) is { } tab)
        {
            BeginWorkspaceTabDrag(
                WorkspaceTabs,
                tab.Workspace,
                position,
                tab.PointerOffsetX);
            WorkspaceTabs.CaptureMouse();
            return true;
        }

        return false;
    }

    private void TitleBar_MouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (!_titleBarDragPending)
        {
            return;
        }

        if (eventArgs.LeftButton != MouseButtonState.Pressed)
        {
            EndPendingTitleBarDrag(sender as UIElement);
            return;
        }

        var position = eventArgs.GetPosition(this);
        var delta = position - _titleBarDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _titleBarDragPending = false;
        if (sender is UIElement titleBar && titleBar.IsMouseCaptured)
        {
            titleBar.ReleaseMouseCapture();
        }

        if (WindowState == WindowState.Maximized)
        {
            RestoreWindowForDrag(position);
        }

        DragMove();
        eventArgs.Handled = true;
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (_titleBarDragPending && eventArgs.ChangedButton == MouseButton.Left)
        {
            EndPendingTitleBarDrag(sender as UIElement);
            eventArgs.Handled = true;
        }
    }

    private void TitleBar_LostMouseCapture(object sender, MouseEventArgs eventArgs) =>
        _titleBarDragPending = false;

    private void EndPendingTitleBarDrag(UIElement? titleBar)
    {
        _titleBarDragPending = false;
        if (titleBar?.IsMouseCaptured == true)
        {
            titleBar.ReleaseMouseCapture();
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs eventArgs)
    {
        if (WindowState != WindowState.Minimized)
        {
            var handle = new WindowInteropHelper(this).Handle;
            WindowsWindowAnimation.DisableSystemWindowTransitions(handle);
            if (WindowState == WindowState.Maximized)
            {
                WindowsWindowBounds.TryFitWindowToWorkArea(handle);
            }
        }

        SaveSettings(cleanExit: false);
    }

    private void RestoreWindowForDrag(Point mousePosition)
    {
        var screenPosition = PointToScreen(mousePosition);
        var dpi = VisualTreeHelper.GetDpi(this);
        var horizontalRatio = ActualWidth > 0
            ? Math.Clamp(mousePosition.X / ActualWidth, 0, 1)
            : 0.5;
        var restoredWidth = RestoreBounds.Width;

        WindowState = WindowState.Normal;
        Left = (screenPosition.X / dpi.DpiScaleX) - (restoredWidth * horizontalRatio);
        Top = (screenPosition.Y / dpi.DpiScaleY) - mousePosition.Y;
    }

    private void WorkspaceTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is not ListBox tabs || eventArgs.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var source = eventArgs.OriginalSource as DependencyObject;
        var item = FindVisualAncestor<ListBoxItem>(source);
        if (FindVisualAncestor<Button>(source) is not null)
        {
            return;
        }

        var position = eventArgs.GetPosition(tabs);
        var tab = item?.DataContext is WorkspaceViewModel workspace
            ? (Workspace: workspace, PointerOffsetX: eventArgs.GetPosition(item).X)
            : FindWorkspaceTabAtX(tabs, position.X);
        if (tab is null)
        {
            return;
        }

        BeginWorkspaceTabDrag(
            tabs,
            tab.Value.Workspace,
            position,
            tab.Value.PointerOffsetX);
        eventArgs.Handled = true;
    }

    private (WorkspaceViewModel Workspace, double PointerOffsetX)? FindWorkspaceTabAtX(
        ListBox tabs,
        double pointerX)
    {
        var hitSlop = WorkspaceTabHorizontalMargin / 2;
        foreach (var workspace in _viewModel.Workspaces)
        {
            if (tabs.ItemContainerGenerator.ContainerFromItem(workspace) is not ListBoxItem item)
            {
                continue;
            }

            var itemLeft = item.TranslatePoint(default, tabs).X;
            if (pointerX >= itemLeft - hitSlop &&
                pointerX <= itemLeft + item.ActualWidth + hitSlop)
            {
                return (
                    workspace,
                    Math.Clamp(pointerX - itemLeft, 0, item.ActualWidth));
            }
        }

        return null;
    }

    private void BeginWorkspaceTabDrag(
        ListBox tabs,
        WorkspaceViewModel workspace,
        Point dragStart,
        double pointerOffsetX)
    {
        _workspaceTabDragStart = dragStart;
        _workspaceTabDragItem = workspace;
        _workspaceTabDragPointerOffsetX = pointerOffsetX;
        _workspaceTabDragActive = false;
        _workspaceTabOrderChanged = false;
        _viewModel.SelectedWorkspace = workspace;
        tabs.SelectedItem = workspace;
    }

    private void Workspaces_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) =>
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateWorkspaceTabLayout));

    private void WorkspaceTabsHost_SizeChanged(object sender, SizeChangedEventArgs eventArgs) =>
        UpdateWorkspaceTabLayout();

    private void WorkspaceTabsScrollLeft_Click(object sender, RoutedEventArgs eventArgs) =>
        ScrollWorkspaceTabsBy(-(WorkspaceTabWidth + WorkspaceTabHorizontalMargin));

    private void WorkspaceTabsScrollRight_Click(object sender, RoutedEventArgs eventArgs) =>
        ScrollWorkspaceTabsBy(WorkspaceTabWidth + WorkspaceTabHorizontalMargin);

    private void WorkspaceTabs_PreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        var scrollViewer = FindVisualDescendant<ScrollViewer>(WorkspaceTabs);
        if (scrollViewer is null || scrollViewer.ScrollableWidth <= 0 || eventArgs.Delta == 0)
        {
            return;
        }

        ScrollWorkspaceTabsBy(eventArgs.Delta * -0.4);
        eventArgs.Handled = true;
    }

    private void WorkspaceTabs_ScrollChanged(object sender, ScrollChangedEventArgs eventArgs) =>
        UpdateWorkspaceTabScrollButtons();

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (_workspaceTabDragItem is { } draggedWorkspace)
        {
            if (!ReferenceEquals(WorkspaceTabs.SelectedItem, draggedWorkspace))
            {
                WorkspaceTabs.SelectedItem = draggedWorkspace;
            }

            return;
        }

        if (WorkspaceTabs.SelectedItem is not WorkspaceViewModel workspace)
        {
            return;
        }

        _viewModel.SelectedWorkspace = workspace;
        WorkspaceTabs.ScrollIntoView(workspace);
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(UpdateWorkspaceTabScrollButtons));
    }

    private void UpdateWorkspaceTabLayout()
    {
        var workspaceCount = _viewModel.Workspaces.Count;
        var availableWidth = WorkspaceTabsHost.ActualWidth;
        if (workspaceCount == 0 || availableWidth <= 0)
        {
            _workspaceTabsOverflow = false;
            WorkspaceTabsScrollLeftButton.Visibility = Visibility.Collapsed;
            WorkspaceTabsScrollRightButton.Visibility = Visibility.Collapsed;
            return;
        }

        var availableTabWidth = (availableWidth / workspaceCount) - WorkspaceTabHorizontalMargin;
        if (availableTabWidth >= WorkspaceTabPreferredWidth)
        {
            _workspaceTabsOverflow = false;
            WorkspaceTabWidth = WorkspaceTabPreferredWidth;
            HideWorkspaceTabScrollButtons();
            return;
        }

        if (availableTabWidth >= WorkspaceTabMinimumWidth)
        {
            _workspaceTabsOverflow = false;
            WorkspaceTabWidth = Math.Floor(availableTabWidth);
            HideWorkspaceTabScrollButtons();
            return;
        }

        _workspaceTabsOverflow = true;
        WorkspaceTabWidth = WorkspaceTabMinimumWidth;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(UpdateWorkspaceTabScrollButtons));
    }

    private void HideWorkspaceTabScrollButtons()
    {
        WorkspaceTabsScrollLeftButton.Visibility = Visibility.Collapsed;
        WorkspaceTabsScrollRightButton.Visibility = Visibility.Collapsed;
        FindVisualDescendant<ScrollViewer>(WorkspaceTabs)?.ScrollToLeftEnd();
    }

    private void ScrollWorkspaceTabsBy(double offset)
    {
        var scrollViewer = FindVisualDescendant<ScrollViewer>(WorkspaceTabs);
        if (scrollViewer is null)
        {
            return;
        }

        scrollViewer.ScrollToHorizontalOffset(
            Math.Clamp(scrollViewer.HorizontalOffset + offset, 0, scrollViewer.ScrollableWidth));
    }

    private void UpdateWorkspaceTabScrollButtons()
    {
        if (!_workspaceTabsOverflow)
        {
            WorkspaceTabsScrollLeftButton.Visibility = Visibility.Collapsed;
            WorkspaceTabsScrollRightButton.Visibility = Visibility.Collapsed;
            return;
        }

        var scrollViewer = FindVisualDescendant<ScrollViewer>(WorkspaceTabs);
        if (scrollViewer is null || scrollViewer.ScrollableWidth <= 0.5)
        {
            WorkspaceTabsScrollLeftButton.Visibility = Visibility.Collapsed;
            WorkspaceTabsScrollRightButton.Visibility = Visibility.Collapsed;
            return;
        }

        WorkspaceTabsScrollLeftButton.Visibility = scrollViewer.HorizontalOffset > 0.5
            ? Visibility.Visible
            : Visibility.Collapsed;
        WorkspaceTabsScrollRightButton.Visibility =
            scrollViewer.HorizontalOffset < scrollViewer.ScrollableWidth - 0.5
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void WorkspaceTabs_PreviewMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (sender is not ListBox tabs || _workspaceTabDragItem is null)
        {
            return;
        }

        if (eventArgs.LeftButton != MouseButtonState.Pressed)
        {
            EndWorkspaceTabDrag(tabs);
            return;
        }

        var position = eventArgs.GetPosition(tabs);
        if (!_workspaceTabDragActive)
        {
            var delta = position - _workspaceTabDragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _workspaceTabDragActive = true;
            tabs.CaptureMouse();
            if (tabs.ItemContainerGenerator.ContainerFromItem(_workspaceTabDragItem) is ListBoxItem item)
            {
                Panel.SetZIndex(item, 1000);
            }
        }

        UpdateDraggedWorkspaceTabPosition(tabs, position.X);
        ReorderWorkspaceTab(tabs, position.X);
        eventArgs.Handled = true;
    }

    private void WorkspaceTabs_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is ListBox tabs && _workspaceTabDragItem is not null)
        {
            EndWorkspaceTabDrag(tabs);
            eventArgs.Handled = true;
        }
    }

    private void WorkspaceTabs_LostMouseCapture(object sender, MouseEventArgs eventArgs)
    {
        if (sender is ListBox tabs && _workspaceTabDragItem is not null)
        {
            EndWorkspaceTabDrag(tabs, releaseCapture: false);
        }
    }

    private void ReorderWorkspaceTab(ListBox tabs, double pointerX)
    {
        if (_workspaceTabDragItem is not { } draggedWorkspace)
        {
            return;
        }

        var currentIndex = _viewModel.Workspaces.IndexOf(draggedWorkspace);
        var targetIndex = currentIndex;
        if (currentIndex < 0)
        {
            return;
        }

        var draggedCenterX = pointerX;
        if (tabs.ItemContainerGenerator.ContainerFromItem(draggedWorkspace) is ListBoxItem draggedItem)
        {
            draggedCenterX = GetClampedDraggedWorkspaceTabLeft(tabs, draggedItem, pointerX) +
                (draggedItem.ActualWidth / 2);
        }

        if (currentIndex < _viewModel.Workspaces.Count - 1)
        {
            for (var index = currentIndex + 1; index < _viewModel.Workspaces.Count; index++)
            {
                if (tabs.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item &&
                    draggedCenterX > GetWorkspaceTabLayoutX(item, tabs) +
                        (item.ActualWidth * WorkspaceTabReorderEdgeInsetRatio))
                {
                    targetIndex = index;
                }
            }
        }

        if (targetIndex == currentIndex && currentIndex > 0)
        {
            for (var index = currentIndex - 1; index >= 0; index--)
            {
                if (tabs.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item &&
                    draggedCenterX < GetWorkspaceTabLayoutX(item, tabs) +
                        (item.ActualWidth * (1 - WorkspaceTabReorderEdgeInsetRatio)))
                {
                    targetIndex = index;
                }
            }
        }

        if (targetIndex == currentIndex)
        {
            return;
        }

        var previousPositions = CaptureWorkspaceTabPositions(tabs);
        _viewModel.Workspaces.Move(currentIndex, targetIndex);
        tabs.UpdateLayout();
        AnimateWorkspaceTabReorder(tabs, previousPositions, draggedWorkspace);
        UpdateDraggedWorkspaceTabPosition(tabs, pointerX);
        _workspaceTabOrderChanged = true;
    }

    private void UpdateDraggedWorkspaceTabPosition(ListBox tabs, double pointerX)
    {
        if (_workspaceTabDragItem is null ||
            tabs.ItemContainerGenerator.ContainerFromItem(_workspaceTabDragItem) is not ListBoxItem item)
        {
            return;
        }

        var transform = item.RenderTransform as TranslateTransform ?? new TranslateTransform();
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        item.RenderTransform = transform;
        var layoutX = GetWorkspaceTabLayoutX(item, tabs);
        var draggedLeft = GetClampedDraggedWorkspaceTabLeft(tabs, item, pointerX);
        transform.X = draggedLeft - layoutX;
    }

    private static double GetWorkspaceTabLayoutX(ListBoxItem item, ListBox tabs)
    {
        // Reorder against stable layout slots, not positions that are still moving through an animation.
        var renderedX = item.TranslatePoint(default, tabs).X;
        return item.RenderTransform is TranslateTransform transform
            ? renderedX - transform.X
            : renderedX;
    }

    private double GetClampedDraggedWorkspaceTabLeft(ListBox tabs, ListBoxItem item, double pointerX)
    {
        var maximumLeft = Math.Max(0, tabs.ActualWidth - item.ActualWidth);
        return Math.Clamp(pointerX - _workspaceTabDragPointerOffsetX, 0, maximumLeft);
    }

    private Dictionary<WorkspaceViewModel, double> CaptureWorkspaceTabPositions(ListBox tabs)
    {
        var positions = new Dictionary<WorkspaceViewModel, double>();
        foreach (var workspace in _viewModel.Workspaces)
        {
            if (tabs.ItemContainerGenerator.ContainerFromItem(workspace) is ListBoxItem item)
            {
                positions[workspace] = item.TranslatePoint(default, tabs).X;
            }
        }

        return positions;
    }

    private static void AnimateWorkspaceTabReorder(
        ListBox tabs,
        IReadOnlyDictionary<WorkspaceViewModel, double> previousPositions,
        WorkspaceViewModel draggedWorkspace)
    {
        foreach (var workspace in previousPositions.Keys)
        {
            if (ReferenceEquals(workspace, draggedWorkspace))
            {
                continue;
            }

            if (tabs.ItemContainerGenerator.ContainerFromItem(workspace) is not ListBoxItem item)
            {
                continue;
            }

            var transform = item.RenderTransform as TranslateTransform ?? new TranslateTransform();
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
            item.RenderTransform = transform;
        }

        foreach (var (workspace, previousX) in previousPositions)
        {
            if (ReferenceEquals(workspace, draggedWorkspace))
            {
                continue;
            }

            if (tabs.ItemContainerGenerator.ContainerFromItem(workspace) is not ListBoxItem item ||
                item.RenderTransform is not TranslateTransform transform)
            {
                continue;
            }

            var currentX = item.TranslatePoint(default, tabs).X;
            var offset = previousX - currentX;
            if (Math.Abs(offset) < 0.5)
            {
                continue;
            }

            var animation = new DoubleAnimation(
                offset,
                0,
                TimeSpan.FromMilliseconds(WorkspaceTabReorderAnimationMilliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void EndWorkspaceTabDrag(ListBox tabs, bool releaseCapture = true)
    {
        var orderChanged = _workspaceTabOrderChanged;
        var draggedWorkspace = _workspaceTabDragItem;
        _workspaceTabDragItem = null;
        _workspaceTabDragActive = false;
        _workspaceTabOrderChanged = false;

        if (releaseCapture && tabs.IsMouseCaptured)
        {
            tabs.ReleaseMouseCapture();
        }

        if (draggedWorkspace is not null &&
            tabs.ItemContainerGenerator.ContainerFromItem(draggedWorkspace) is ListBoxItem item)
        {
            AnimateDraggedWorkspaceTabIntoPlace(item);
        }

        if (orderChanged)
        {
            SaveSettings(cleanExit: false);
        }
    }

    private static void AnimateDraggedWorkspaceTabIntoPlace(ListBoxItem item)
    {
        if (item.RenderTransform is not TranslateTransform transform)
        {
            Panel.SetZIndex(item, 0);
            return;
        }

        transform.BeginAnimation(TranslateTransform.XProperty, null);
        var offset = transform.X;
        transform.X = 0;
        if (Math.Abs(offset) < 0.5)
        {
            Panel.SetZIndex(item, 0);
            return;
        }

        var animation = new DoubleAnimation(
            offset,
            0,
            TimeSpan.FromMilliseconds(WorkspaceTabSettleAnimationMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => Panel.SetZIndex(item, 0);
        transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs eventArgs)
    {
        EnableSystemWindowTransitions();
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs eventArgs)
    {
        EnableSystemWindowTransitions();
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void EnableSystemWindowTransitions() =>
        WindowsWindowAnimation.EnableSystemWindowTransitions(
            new WindowInteropHelper(this).Handle);

    private void CloseWindow_Click(object sender, RoutedEventArgs eventArgs) =>
        SystemCommands.CloseWindow(this);

    private void ToolbarScrollLeft_Click(object sender, RoutedEventArgs eventArgs) =>
        ScrollToolbarBy(-72);

    private void ToolbarScrollRight_Click(object sender, RoutedEventArgs eventArgs) =>
        ScrollToolbarBy(72);

    private void ToolbarScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (ToolbarScrollViewer.ScrollableWidth <= 0 || eventArgs.Delta == 0)
        {
            return;
        }

        ScrollToolbarBy(eventArgs.Delta * -0.4);
        eventArgs.Handled = true;
    }

    private void ToolbarScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs eventArgs) =>
        UpdateToolbarScrollButtons();

    private void ScrollToolbarBy(double offset)
    {
        ToolbarScrollViewer.ScrollToHorizontalOffset(
            Math.Clamp(
                ToolbarScrollViewer.HorizontalOffset + offset,
                0,
                ToolbarScrollViewer.ScrollableWidth));
    }

    private void UpdateToolbarScrollButtons()
    {
        ToolbarScrollLeftButton.Visibility = ToolbarScrollViewer.HorizontalOffset > 0.5
            ? Visibility.Visible
            : Visibility.Collapsed;
        ToolbarScrollRightButton.Visibility =
            ToolbarScrollViewer.HorizontalOffset < ToolbarScrollViewer.ScrollableWidth - 0.5
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs eventArgs)
    {
        _allowClose = true;
        var cleanExit = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var flush = _viewModel.FlushAllAsync(timeout.Token);
            var frame = new DispatcherFrame();
            _ = flush.ContinueWith(
                _ => Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            flush.GetAwaiter().GetResult();
            cleanExit = _viewModel.Workspaces.All(
                workspace => workspace.SaveState != WorkspaceSaveState.Failed);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.Error("Could not flush workspaces while Windows was ending the session.", exception);
        }
        finally
        {
            SaveSettings(cleanExit);
            CleanupShellIntegration();
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs eventArgs)
    {
        Dispatcher.Invoke(
            () =>
            {
                if (IsVisible && IsActive)
                {
                    HideToTray();
                }
                else
                {
                    ShowAndActivate();
                }
            });
    }

    private void CreateTrayIcon(IntPtr windowHandle)
    {
        _trayIcon = new WindowsTrayIcon(
            windowHandle,
            "OmniRef",
            _viewModel.Localization["Show"],
            _viewModel.Localization["Hide"],
            _viewModel.Localization["Exit"]);
        _trayIcon.ShowRequested += (_, _) => Dispatcher.Invoke(ShowAndActivate);
        _trayIcon.HideRequested += (_, _) => Dispatcher.Invoke(HideToTray);
        _trayIcon.ExitRequested += (_, _) => Dispatcher.InvokeAsync(RequestExitAsync);
    }

    private void RebuildTrayMenu()
    {
        if (_trayIcon is null)
        {
            return;
        }
        _trayIcon.UpdateLabels(
            _viewModel.Localization["Show"],
            _viewModel.Localization["Hide"],
            _viewModel.Localization["Exit"]);
    }

    private void HideToTray()
    {
        SaveSettings(cleanExit: false);
        if (IsVisible)
        {
            var handle = new WindowInteropHelper(this).Handle;
            WindowsWindowAnimation.TryHide(handle);
        }
        Hide();
        _previewCache.TrimAggressively();
    }

    private void CleanupShellIntegration()
    {
        Application.Current.SessionEnding -= OnSessionEnding;
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _hotkeyService.Unregister();
    }

    private async void New_Click(object sender, RoutedEventArgs eventArgs) =>
        await _viewModel.CreateNewAsync(includeWelcomeContent: false);

    private async void CreateWorkspace_Click(object sender, RoutedEventArgs eventArgs) =>
        await _viewModel.CreateNewAsync(includeWelcomeContent: false);

    private async void Open_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = _viewModel.Localization["OpenDialogTitle"],
            Filter = "OmniRef workspace (*.omniref)|*.omniref|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        foreach (var path in dialog.FileNames)
        {
            try
            {
                await _viewModel.OpenAsync(path);
            }
            catch (InvalidOperationException exception)
            {
                ShowError(exception.Message);
            }
        }
        SaveSettings(cleanExit: false);
    }

    private async void Save_Click(object sender, RoutedEventArgs eventArgs)
    {
        var workspace = _viewModel.SelectedWorkspace;
        if (workspace is null)
        {
            return;
        }
        if (workspace.IsRecovery)
        {
            await SaveWorkspaceAsAsync(workspace);
        }
        else
        {
            await workspace.FlushAsync();
        }
        SaveSettings(cleanExit: false);
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel.SelectedWorkspace is { } workspace)
        {
            await SaveWorkspaceAsAsync(workspace);
        }
    }

    private async Task SaveWorkspaceAsAsync(WorkspaceViewModel workspace)
    {
        var dialog = new SaveFileDialog
        {
            Title = _viewModel.Localization["SaveDialogTitle"],
            Filter = "OmniRef workspace (*.omniref)|*.omniref",
            AddExtension = true,
            DefaultExt = ".omniref",
            FileName = workspace.IsRecovery ? workspace.Document.Title : Path.GetFileName(workspace.Path),
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            await workspace.SaveAsAsync(dialog.FileName);
            SaveSettings(cleanExit: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.Error("Save As failed.", exception);
            ShowError(exception.Message);
        }
    }

    private void AddFiles_Click(object sender, RoutedEventArgs eventArgs)
    {
        var workspace = _viewModel.SelectedWorkspace;
        if (workspace is null || workspace.IsReadOnly)
        {
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = _viewModel.Localization["FilesDialogTitle"],
            Filter = "All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            workspace.AddPaths(dialog.FileNames, CurrentCanvasCenter());
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs eventArgs)
    {
        var workspace = _viewModel.SelectedWorkspace;
        if (workspace is null || workspace.IsReadOnly)
        {
            return;
        }
        var dialog = new OpenFolderDialog
        {
            Title = _viewModel.Localization["FolderDialogTitle"],
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            workspace.AddPaths(dialog.FolderNames, CurrentCanvasCenter());
        }
    }

    private void AddText_Click(object sender, RoutedEventArgs eventArgs)
    {
        var workspace = _viewModel.SelectedWorkspace;
        var canvas = FindCanvas();
        if (workspace is null || workspace.IsReadOnly || canvas is null)
        {
            return;
        }
        var item = workspace.AddText(string.Empty, canvas.ViewportCenter);
        canvas.BeginTextEdit(item);
    }

    private void AddFrame_Click(object sender, RoutedEventArgs eventArgs)
    {
        var workspace = _viewModel.SelectedWorkspace;
        if (workspace is null || workspace.IsReadOnly)
        {
            return;
        }
        workspace.AddFrame(_viewModel.Localization["FrameDefault"], CurrentCanvasCenter());
    }

    private void Undo_Click(object sender, RoutedEventArgs eventArgs) => _viewModel.SelectedWorkspace?.Undo();
    private void Redo_Click(object sender, RoutedEventArgs eventArgs) => _viewModel.SelectedWorkspace?.Redo();
    private void Delete_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel.SelectedWorkspace is { IsReadOnly: false } workspace)
        {
            workspace.RemoveSelected();
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs eventArgs) =>
        PasteFromClipboard(CurrentCanvasCenter());

    public bool ShowCanvasGrid
    {
        get => (bool)GetValue(ShowCanvasGridProperty);
        set => SetValue(ShowCanvasGridProperty, value);
    }

    public bool SnapToGrid
    {
        get => (bool)GetValue(SnapToGridProperty);
        set => SetValue(SnapToGridProperty, value);
    }

    public AppTheme CurrentTheme
    {
        get => (AppTheme)GetValue(CurrentThemeProperty);
        set => SetValue(CurrentThemeProperty, value);
    }

    private void ToggleGrid_Click(object sender, RoutedEventArgs eventArgs)
    {
        _settings.ShowCanvasGrid = ShowCanvasGrid;
        SaveSettings(cleanExit: false);
    }

    private void ToggleGridSnap_Click(object sender, RoutedEventArgs eventArgs)
    {
        _settings.SnapToGrid = SnapToGrid;
        SaveSettings(cleanExit: false);
    }

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs eventArgs)
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        SaveSettings(cleanExit: false);
    }

    private void Theme_Click(object sender, RoutedEventArgs eventArgs)
    {
        var previousEffectiveTheme = _themeManager.EffectiveTheme;
        var previousThemeFrame = CaptureThemeTransitionFrame();
        CurrentTheme = _themeManager.Cycle();
        _settings.Theme = CurrentTheme;
        SaveSettings(cleanExit: false);
        FindCanvas()?.InvalidateVisual();
        BeginThemeTransition(
            previousEffectiveTheme == _themeManager.EffectiveTheme ? null : previousThemeFrame);
    }

    private BitmapSource? CaptureThemeTransitionFrame()
    {
        ThemeTransitionOverlay.BeginAnimation(OpacityProperty, null);
        ThemeTransitionOverlay.Opacity = 0;
        ThemeTransitionOverlay.Source = null;

        if (WindowContent.ActualWidth <= 0 ||
            WindowContent.ActualHeight <= 0)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(WindowContent);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(WindowContent.ActualWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(WindowContent.ActualHeight * dpi.DpiScaleY));
        var frame = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        frame.Render(WindowContent);
        frame.Freeze();
        return frame;
    }

    private void BeginThemeTransition(BitmapSource? previousThemeFrame)
    {
        var transitionVersion = ++_themeTransitionVersion;
        if (previousThemeFrame is null)
        {
            return;
        }

        ThemeTransitionOverlay.Source = previousThemeFrame;
        ThemeTransitionOverlay.Opacity = 1;

        var animation = new DoubleAnimation(
            fromValue: 1,
            toValue: 0,
            duration: TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            if (transitionVersion != _themeTransitionVersion)
            {
                return;
            }

            ThemeTransitionOverlay.BeginAnimation(OpacityProperty, null);
            ThemeTransitionOverlay.Opacity = 0;
            ThemeTransitionOverlay.Source = null;
        };
        ThemeTransitionOverlay.BeginAnimation(OpacityProperty, animation);
    }

    private void Language_Click(object sender, RoutedEventArgs eventArgs)
    {
        _viewModel.Localization.Toggle();
        _settings.Language = _viewModel.Localization.ConfiguredLanguage;
        RebuildTrayMenu();
        SaveSettings(cleanExit: false);
        FindCanvas()?.InvalidateVisual();
    }

    private async void Exit_Click(object sender, RoutedEventArgs eventArgs) =>
        await RequestExitAsync();

    private void Arrange_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button button || _viewModel.SelectedWorkspace is not { IsReadOnly: false } workspace)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button };
        AddMenuItem(menu, "AlignLeft", () => workspace.AlignSelected(AlignmentKind.Left));
        AddMenuItem(menu, "AlignCenter", () => workspace.AlignSelected(AlignmentKind.HorizontalCenter));
        AddMenuItem(menu, "AlignRight", () => workspace.AlignSelected(AlignmentKind.Right));
        AddMenuItem(menu, "AlignTop", () => workspace.AlignSelected(AlignmentKind.Top));
        AddMenuItem(menu, "AlignMiddle", () => workspace.AlignSelected(AlignmentKind.VerticalCenter));
        AddMenuItem(menu, "AlignBottom", () => workspace.AlignSelected(AlignmentKind.Bottom));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "DistributeHorizontal", () => workspace.DistributeSelected(horizontally: true));
        AddMenuItem(menu, "DistributeVertical", () => workspace.DistributeSelected(horizontally: false));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "BringToFront", () => workspace.MoveSelectionLayer(LayerMove.BringToFront));
        AddMenuItem(menu, "BringForward", () => workspace.MoveSelectionLayer(LayerMove.BringForward));
        AddMenuItem(menu, "SendBackward", () => workspace.MoveSelectionLayer(LayerMove.SendBackward));
        AddMenuItem(menu, "SendToBack", () => workspace.MoveSelectionLayer(LayerMove.SendToBack));
        menu.IsOpen = true;
    }

    private void TextAlignment_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel.SelectedWorkspace is { IsReadOnly: false } &&
            sender is Button { Tag: string alignment } &&
            Enum.TryParse<TextHorizontalAlignment>(alignment, out var parsed))
        {
            _viewModel.SelectedWorkspace?.SelectedItem?.SetTextAlignment(parsed);
        }
    }

    private void AddMenuItem(ContextMenu menu, string localizationKey, Action action)
    {
        var item = new MenuItem { Header = _viewModel.Localization[localizationKey] };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: WorkspaceViewModel workspace })
        {
            return;
        }
        var closed = await _viewModel.CloseAsync(workspace, force: false);
        if (!closed)
        {
            var result = MessageBox.Show(
                this,
                _viewModel.Localization["ConfirmCloseDirty"],
                "OmniRef",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                await _viewModel.CloseAsync(workspace, force: true);
            }
        }
        SaveSettings(cleanExit: false);
        eventArgs.Handled = true;
    }

    private void SearchResult_Click(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is ListBox { SelectedItem: BoardItemViewModel item })
        {
            _viewModel.SelectedWorkspace?.Focus(item);
        }
    }

    private async void Embed_Click(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await (_viewModel.SelectedWorkspace?.EmbedSelectedAsync() ?? Task.FromResult(false));
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs eventArgs)
    {
        var workspace = _viewModel.SelectedWorkspace;
        var source = workspace?.SelectedItem is { } item ? SourceOf(item.Model.Content) : null;
        if (workspace is null || source?.EmbeddedAssetId is null)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = _viewModel.Localization["ExportDialogTitle"],
            FileName = source.OriginalFileName,
            Filter = "All files (*.*)|*.*",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await workspace.ExportSelectedAsync(dialog.FileName);
        }
    }

    private void Relink_Click(object sender, RoutedEventArgs eventArgs)
    {
        var workspace = _viewModel.SelectedWorkspace;
        var item = workspace?.SelectedItem;
        if (workspace is null || item is null || item.Model.Content is not (ImageContent or FileContent or FolderContent))
        {
            return;
        }
        if (item.Kind == ItemKind.Folder)
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = _viewModel.Localization["RelinkDialogTitle"],
                Multiselect = false
            };
            if (folderDialog.ShowDialog(this) == true)
            {
                workspace.RelinkSelected(folderDialog.FolderName);
            }
        }
        else
        {
            var fileDialog = new OpenFileDialog
            {
                Title = _viewModel.Localization["RelinkDialogTitle"],
                Filter = "All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (fileDialog.ShowDialog(this) == true)
            {
                workspace.RelinkSelected(fileDialog.FileName);
            }
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs eventArgs) =>
        _viewModel.SelectedWorkspace?.RevealSelected();

    private void ResetZoom_Click(object sender, RoutedEventArgs eventArgs) => FindCanvas()?.ResetViewport();

    private async void Compact_Click(object sender, RoutedEventArgs eventArgs)
    {
        var workspace = _viewModel.SelectedWorkspace;
        if (workspace is null || workspace.IsReadOnly)
        {
            return;
        }
        await workspace.FlushAsync();
        await _workspaceStore.CompactAsync(workspace.Path);
    }

    private async void RetrySave_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel.SelectedWorkspace is { } workspace)
        {
            await workspace.FlushAsync();
        }
    }

    private async void RetrySaveAs_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel.SelectedWorkspace is { } workspace)
        {
            await SaveWorkspaceAsAsync(workspace);
        }
    }

    private void Canvas_PasteRequested(object sender, CanvasPasteEventArgs eventArgs) =>
        PasteFromClipboard(eventArgs.Position);

    private void Canvas_CopyRequested(object sender, EventArgs eventArgs) => CopySelectionToClipboard();

    private void Canvas_ExternalDrop(object sender, CanvasDropEventArgs eventArgs)
    {
        var workspace = _viewModel.SelectedWorkspace;
        if (workspace is null)
        {
            return;
        }
        if (eventArgs.Files.Count > 0)
        {
            workspace.AddPaths(eventArgs.Files, eventArgs.Position);
            return;
        }
        var text = eventArgs.Text?.Trim();
        if (text is null or "")
        {
            return;
        }
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            workspace.AddUrl(uri.AbsoluteUri, eventArgs.Position);
        }
        else
        {
            workspace.AddText(text, eventArgs.Position);
        }
    }

    private void CopySelectionToClipboard()
    {
        var selected = _viewModel.SelectedWorkspace?.SelectedItems;
        if (selected is null || selected.Count == 0)
        {
            return;
        }
        try
        {
            var models = selected.Select(item => item.Model.DeepClone()).ToList();
            var data = new DataObject();
            data.SetData(ClipboardFormat, JsonSerializer.Serialize(models, _clipboardJsonOptions));
            if (models.Count == 1)
            {
                var text = models[0].Content switch
                {
                    TextContent content => content.Text,
                    UrlContent content => content.Url,
                    ImageContent content => content.Source.AbsolutePath,
                    FileContent content => content.Source.AbsolutePath,
                    FolderContent content => content.Source.AbsolutePath,
                    _ => models[0].Title
                };
                if (!string.IsNullOrWhiteSpace(text))
                {
                    data.SetText(text);
                }
            }

            var paths = models
                .Select(model => SourceOf(model.Content))
                .Where(source => source?.Mode == AssetMode.ExternalReference && source.AbsolutePath is not null)
                .Select(source => source!.AbsolutePath!)
                .Where(PathResolver.Exists)
                .ToList();
            if (paths.Count == models.Count)
            {
                var collection = new StringCollection();
                collection.AddRange(paths.ToArray());
                data.SetFileDropList(collection);
            }
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or JsonException)
        {
            _logger.Warning($"Clipboard copy failed: {exception.Message}");
        }
    }

    private async void PasteFromClipboard(WorldPoint position)
    {
        var workspace = _viewModel.SelectedWorkspace;
        if (workspace is null || workspace.IsReadOnly)
        {
            return;
        }
        try
        {
            if (Clipboard.ContainsData(ClipboardFormat) &&
                Clipboard.GetData(ClipboardFormat) is string json)
            {
                var items = JsonSerializer.Deserialize<List<BoardItem>>(json, _clipboardJsonOptions);
                if (items is { Count: > 0 })
                {
                    workspace.AddClonedItems(items, position);
                    return;
                }
            }

            IReadOnlyList<string> paths = Clipboard.ContainsFileDropList()
                ? Clipboard.GetFileDropList().Cast<string>().ToList()
                : [];
            byte[]? pngBytes = null;
            if (paths.Count == 0 && Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource bitmap)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                pngBytes = stream.ToArray();
            }
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            var result = _clipboardImporter.Classify(new ClipboardSnapshot(paths, pngBytes, text));
            switch (result.Kind)
            {
                case ClipboardImportKind.Files:
                    workspace.AddPaths(result.FilePaths, position);
                    break;
                case ClipboardImportKind.Image when result.PngBytes is not null:
                    await workspace.AddEmbeddedImageAsync(result.PngBytes, position);
                    break;
                case ClipboardImportKind.Url when result.Text is not null:
                    workspace.AddUrl(result.Text, position);
                    break;
                case ClipboardImportKind.Text when result.Text is not null:
                    workspace.AddText(result.Text, position);
                    break;
            }
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or IOException or JsonException)
        {
            _logger.Warning($"Clipboard paste failed: {exception.Message}");
        }
    }

    private void RestoreWindowState()
    {
        if (_settings.WindowLeft is { } left &&
            _settings.WindowTop is { } top &&
            double.IsFinite(left) &&
            double.IsFinite(top))
        {
            Left = left;
            Top = top;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        if (_settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveSettings(bool cleanExit)
    {
        try
        {
            var bounds = RestoreBounds;
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            _settings.LastExitClean = cleanExit;
            _settings.OpenWorkspacePaths = _viewModel.Workspaces.Select(workspace => workspace.Path).ToList();
            _settings.ActiveWorkspaceIndex = Math.Max(0, _viewModel.Workspaces.IndexOf(_viewModel.SelectedWorkspace!));
            foreach (var path in _viewModel.Workspaces
                         .Where(workspace => !workspace.IsRecovery)
                         .Select(workspace => workspace.Path)
                         .Reverse())
            {
                _settings.RecentWorkspacePaths.RemoveAll(
                    existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
                _settings.RecentWorkspacePaths.Insert(0, path);
            }
            _settings.RecentWorkspacePaths = _settings.RecentWorkspacePaths.Take(20).ToList();
            _settingsStore.Save(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warning($"Could not save application settings: {exception.Message}");
        }
    }

    private WorldPoint CurrentCanvasCenter() => FindCanvas()?.ViewportCenter ?? new WorldPoint(0, 0);

    private InfiniteCanvas? FindCanvas() => FindVisualChild<InfiniteCanvas>(CanvasHost);

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result)
            {
                return result;
            }
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }
        return null;
    }

    private static SourceDescriptor? SourceOf(ItemContent content) => content switch
    {
        ImageContent image => image.Source,
        FileContent file => file.Source,
        FolderContent folder => folder.Source,
        _ => null
    };

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "OmniRef", MessageBoxButton.OK, MessageBoxImage.Warning);
}
