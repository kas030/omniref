using System.IO;
using System.Windows;
using System.Windows.Threading;
using OmniRef.App.Services;
using OmniRef.App.ViewModels;
using OmniRef.Core.Services;
using OmniRef.Infrastructure.Windows.Diagnostics;
using OmniRef.Infrastructure.Windows.Persistence;
using OmniRef.Infrastructure.Windows.Settings;
using OmniRef.Infrastructure.Windows.Shell;
using OmniRef.Infrastructure.Windows.SingleInstance;

namespace OmniRef.App;

public partial class App : Application
{
    private AppSettingsStore? _settingsStore;
    private AppSettings? _settings;
    private RollingFileLogger? _logger;
    private SqliteWorkspaceStore? _workspaceStore;
    private ShellThumbnailProvider? _thumbnailProvider;
    private PreviewCache? _previewCache;
    private WindowsHotkeyService? _hotkeyService;
    private ThemeManager? _themeManager;
    private SingleInstanceCoordinator? _singleInstance;
    private MainWindowViewModel? _viewModel;
    private MainWindow? _window;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        if (eventArgs.Args.Length >= 2 &&
            eventArgs.Args[0].Equals("--create-sample", StringComparison.OrdinalIgnoreCase))
        {
            var localization = new LocalizationService();
            localization.SetLanguage("auto");
            using var store = new SqliteWorkspaceStore();
            await CreateSampleAsync(store, eventArgs.Args[1], localization);
            Shutdown(0);
            return;
        }
        try
        {
            await InitializeAsync(eventArgs.Args);
        }
        catch (Exception exception)
        {
            _logger?.Error("Application startup failed.", exception);
            MessageBox.Show(
                exception.ToString(),
                "OmniRef startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        _viewModel?.Dispose();
        _previewCache?.Dispose();
        _thumbnailProvider?.Dispose();
        _hotkeyService?.Dispose();
        _workspaceStore?.Dispose();
        _themeManager?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(eventArgs);
    }

    private async Task InitializeAsync(IReadOnlyList<string> arguments)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("OMNIREF_DATA_DIR");
        _settingsStore = new AppSettingsStore(
            string.IsNullOrWhiteSpace(configuredRoot) ? null : configuredRoot);
        _settings = _settingsStore.Load();
        _logger = new RollingFileLogger(_settingsStore.LogDirectory);
        _workspaceStore = new SqliteWorkspaceStore();
        _thumbnailProvider = new ShellThumbnailProvider();
        var shell = new WindowsPlatformShell();
        _previewCache = new PreviewCache(
            _workspaceStore,
            _thumbnailProvider,
            _logger,
            _settingsStore.CacheDirectory);
        _hotkeyService = new WindowsHotkeyService();
        _themeManager = new ThemeManager();
        var localization = new LocalizationService();
        localization.SetLanguage(_settings.Language);
        _themeManager.Apply(_settings.Theme);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimary)
        {
            await SingleInstanceCoordinator.SendActivationAsync(arguments);
            Shutdown(0);
            return;
        }

        var previousExitWasClean = _settings.LastExitClean;
        _settings.LastExitClean = false;
        _settingsStore.Save(_settings);

        _viewModel = new MainWindowViewModel(
            _workspaceStore,
            shell,
            _previewCache,
            _logger,
            _settingsStore,
            localization);
        _window = new MainWindow(
            _viewModel,
            _settings,
            _settingsStore,
            _workspaceStore,
            _hotkeyService,
            _previewCache,
            _themeManager,
            new DefaultClipboardImporter(),
            _logger);

        _singleInstance.ActivationReceived += (_, activationArguments) =>
            Dispatcher.InvokeAsync(() => _window.OpenActivationArgumentsAsync(activationArguments));
        _singleInstance.StartListening();

        var startupPaths = arguments
            .Where(path => path.EndsWith(".omniref", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            .Concat(_settings.OpenWorkspacePaths.Where(File.Exists))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var path in startupPaths)
        {
            try
            {
                await _viewModel.OpenAsync(path);
            }
            catch (InvalidOperationException exception)
            {
                _logger.Error($"Could not restore workspace {path}.", exception);
            }
        }

        if (_viewModel.Workspaces.Count == 0)
        {
            await _viewModel.CreateNewAsync(includeWelcomeContent: true);
        }
        else
        {
            _viewModel.SelectedWorkspace = _viewModel.Workspaces[
                Math.Clamp(_settings.ActiveWorkspaceIndex, 0, _viewModel.Workspaces.Count - 1)];
        }

        _window.Show();
        if (!previousExitWasClean)
        {
            MessageBox.Show(
                _window,
                localization["RecoveryNotice"],
                "OmniRef",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private static async Task CreateSampleAsync(
        SqliteWorkspaceStore workspaceStore,
        string path,
        LocalizationService localization)
    {
        var document = new Core.Models.WorkspaceDocument
        {
            ViewportOrigin = new Core.Models.WorldPoint(-120, -90),
            Zoom = 1
        };
        document.Items.Add(Core.Services.BoardItemFactory.Frame(
            "OmniRef",
            new Core.Models.WorldPoint(0, 0),
            0));
        document.Items.Add(Core.Services.BoardItemFactory.Text(
            localization.IsChinese
                ? "欢迎使用 OmniRef\n\n拖放文件、文件夹和图片到画布；双击打开，按 T 添加文本。"
                : "Welcome to OmniRef\n\nDrop files, folders and images onto the canvas. Double-click to open; press T to add text.",
            new Core.Models.WorldPoint(40, 70),
            1));
        await workspaceStore.SaveAsync(Path.GetFullPath(path), document);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        _logger?.Error("Unhandled UI exception.", eventArgs.Exception);
        MessageBox.Show(
            eventArgs.Exception.Message,
            "OmniRef",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        eventArgs.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        if (eventArgs.ExceptionObject is Exception exception)
        {
            _logger?.Error("Unhandled application exception.", exception);
        }
    }
}
