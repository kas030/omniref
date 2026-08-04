using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using OmniRef.App.Services;
using OmniRef.Core.Interfaces;
using OmniRef.Core.Models;
using OmniRef.Core.Services;
using OmniRef.Infrastructure.Windows.Diagnostics;
using OmniRef.Infrastructure.Windows.Settings;

namespace OmniRef.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceStore _store;
    private readonly IPlatformShell _shell;
    private readonly PreviewCache _previewCache;
    private readonly RollingFileLogger _logger;
    private readonly AppSettingsStore _settingsStore;
    private WorkspaceViewModel? _selectedWorkspace;
    private bool _sidebarVisible = true;
    private bool _disposed;

    public MainWindowViewModel(
        IWorkspaceStore store,
        IPlatformShell shell,
        PreviewCache previewCache,
        RollingFileLogger logger,
        AppSettingsStore settingsStore,
        LocalizationService localization)
    {
        _store = store;
        _shell = shell;
        _previewCache = previewCache;
        _logger = logger;
        _settingsStore = settingsStore;
        Localization = localization;
    }

    public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = [];
    public LocalizationService Localization { get; }

    public WorkspaceViewModel? SelectedWorkspace
    {
        get => _selectedWorkspace;
        set
        {
            if (ReferenceEquals(_selectedWorkspace, value))
            {
                return;
            }
            _selectedWorkspace?.ReleasePreviews();
            if (SetProperty(ref _selectedWorkspace, value))
            {
                OnPropertyChanged(nameof(HasWorkspace));
            }
        }
    }

    public bool HasWorkspace => SelectedWorkspace is not null;

    public bool SidebarVisible
    {
        get => _sidebarVisible;
        set => SetProperty(ref _sidebarVisible, value);
    }

    public async Task<WorkspaceViewModel> CreateNewAsync(
        bool includeWelcomeContent,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_settingsStore.RecoveryDirectory);
        var path = System.IO.Path.Combine(
            _settingsStore.RecoveryDirectory,
            $"{Guid.NewGuid():N}.omniref");
        var document = new WorkspaceDocument
        {
            ViewportOrigin = new WorldPoint(-120, -90),
            Zoom = 1
        };
        if (includeWelcomeContent)
        {
            document.Items.Add(BoardItemFactory.Frame(
                "OmniRef",
                new WorldPoint(0, 0),
                0));
            document.Items.Add(BoardItemFactory.Text(
                Localization.IsChinese
                    ? "欢迎使用 OmniRef\n\n拖入文件、文件夹或图片；按 T 新建文本，按 F 新建分组框。使用空格拖动平移，滚轮围绕光标缩放。"
                    : "Welcome to OmniRef\n\nDrop files, folders or images here. Press T for text and F for a frame. Space-drag to pan; use the wheel to zoom around the pointer.",
                new WorldPoint(40, 70),
                1));
            document.Items.Add(BoardItemFactory.Url(
                "https://github.com/",
                new WorldPoint(360, 190),
                2));
        }

        await _store.SaveAsync(path, document, cancellationToken).ConfigureAwait(true);
        var workspace = CreateWorkspace(
            document,
            path,
            isRecovery: true,
            WorkspaceOpenMode.ReadWrite,
            _store.AcquireFileLease(path));
        await workspace.RefreshStorageInfoAsync(cancellationToken).ConfigureAwait(true);
        Workspaces.Add(workspace);
        SelectedWorkspace = workspace;
        return workspace;
    }

    public async Task<WorkspaceViewModel?> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var existing = Workspaces.FirstOrDefault(
            workspace => string.Equals(workspace.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedWorkspace = existing;
            return existing;
        }

        try
        {
            var fileLease = _store.AcquireFileLease(fullPath);
            try
            {
                var result = await _store.OpenAsync(fullPath, cancellationToken).ConfigureAwait(true);
                var isRecovery = fullPath.StartsWith(
                    System.IO.Path.GetFullPath(_settingsStore.RecoveryDirectory),
                    StringComparison.OrdinalIgnoreCase);
                var workspace = CreateWorkspace(result.Document, fullPath, isRecovery, result.Mode, fileLease);
                await workspace.RefreshStorageInfoAsync(cancellationToken).ConfigureAwait(true);
                Workspaces.Add(workspace);
                SelectedWorkspace = workspace;
                return workspace;
            }
            catch
            {
                fileLease.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                Microsoft.Data.Sqlite.SqliteException or FormatException)
        {
            _logger.Error($"Could not open workspace {fullPath}.", exception);
            throw new InvalidOperationException(Localization["OpenFailed"], exception);
        }
    }

    public async Task<bool> CloseAsync(
        WorkspaceViewModel workspace,
        bool force,
        CancellationToken cancellationToken = default)
    {
        await workspace.FlushAsync(cancellationToken).ConfigureAwait(true);
        if (!force && workspace.SaveState == WorkspaceSaveState.Failed)
        {
            return false;
        }

        var index = Workspaces.IndexOf(workspace);
        if (index < 0)
        {
            return true;
        }

        var wasSelected = ReferenceEquals(SelectedWorkspace, workspace);
        if (wasSelected)
        {
            SelectedWorkspace = Workspaces.Count == 1
                ? null
                : Workspaces[index < Workspaces.Count - 1 ? index + 1 : index - 1];
        }

        Workspaces.Remove(workspace);
        workspace.Dispose();
        return true;
    }

    public async Task FlushAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var workspace in Workspaces)
        {
            await workspace.FlushAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public void RefreshReferences()
    {
        foreach (var workspace in Workspaces)
        {
            workspace.RefreshBackingFileState();
            workspace.RefreshMissingSources();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var workspace in Workspaces)
        {
            workspace.Dispose();
        }
        Workspaces.Clear();
    }

    private WorkspaceViewModel CreateWorkspace(
        WorkspaceDocument document,
        string path,
        bool isRecovery,
        WorkspaceOpenMode mode,
        IWorkspaceFileLease fileLease) =>
        new(
            document,
            path,
            isRecovery,
            mode,
            _store,
            fileLease,
            _shell,
            _previewCache,
            _logger,
            Localization,
            System.IO.Path.Combine(_settingsStore.CacheDirectory, "Open"));
}
