using OmniRef.App.Services;
using OmniRef.App.ViewModels;
using OmniRef.Core.Interfaces;
using OmniRef.Core.Models;
using OmniRef.Infrastructure.Windows.Diagnostics;
using OmniRef.Infrastructure.Windows.Settings;

namespace OmniRef.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OmniRef.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly TestWorkspaceStore _store = new();
    private readonly PreviewCache _previewCache;
    private readonly MainWindowViewModel _viewModel;

    public MainWindowViewModelTests()
    {
        var settingsStore = new AppSettingsStore(_directory);
        var logger = new RollingFileLogger(settingsStore.LogDirectory);
        _previewCache = new PreviewCache(
            _store,
            new TestThumbnailProvider(),
            logger,
            settingsStore.CacheDirectory);
        var localization = new LocalizationService();
        localization.SetLanguage("en-US");
        _viewModel = new MainWindowViewModel(
            _store,
            new TestPlatformShell(),
            _previewCache,
            logger,
            settingsStore,
            localization);
    }

    [Fact]
    public async Task CloseSelectedWorkspace_SelectsTabToTheRightBeforeSelectorProcessesRemoval()
    {
        var first = await _viewModel.CreateNewAsync(includeWelcomeContent: false);
        var middle = await _viewModel.CreateNewAsync(includeWelcomeContent: false);
        var last = await _viewModel.CreateNewAsync(includeWelcomeContent: false);
        _viewModel.SelectedWorkspace = middle;
        _viewModel.Workspaces.CollectionChanged += (_, _) =>
        {
            if (_viewModel.SelectedWorkspace is { } selected &&
                !_viewModel.Workspaces.Contains(selected))
            {
                _viewModel.SelectedWorkspace = null;
            }
        };

        var closed = await _viewModel.CloseAsync(middle, force: false);

        Assert.True(closed);
        Assert.Same(last, _viewModel.SelectedWorkspace);
        Assert.Equal([first, last], _viewModel.Workspaces);
    }

    [Fact]
    public async Task CloseLastSelectedWorkspace_SelectsTabToTheLeft()
    {
        var first = await _viewModel.CreateNewAsync(includeWelcomeContent: false);
        var last = await _viewModel.CreateNewAsync(includeWelcomeContent: false);

        var closed = await _viewModel.CloseAsync(last, force: false);

        Assert.True(closed);
        Assert.Same(first, _viewModel.SelectedWorkspace);
    }

    [Fact]
    public async Task CloseOnlyWorkspace_ClearsSelection()
    {
        var workspace = await _viewModel.CreateNewAsync(includeWelcomeContent: false);

        var closed = await _viewModel.CloseAsync(workspace, force: false);

        Assert.True(closed);
        Assert.Null(_viewModel.SelectedWorkspace);
        Assert.False(_viewModel.HasWorkspace);
        Assert.Empty(_viewModel.Workspaces);
    }

    [Fact]
    public async Task CloseBackgroundWorkspace_KeepsSelectedTab()
    {
        var background = await _viewModel.CreateNewAsync(includeWelcomeContent: false);
        var selected = await _viewModel.CreateNewAsync(includeWelcomeContent: false);

        var closed = await _viewModel.CloseAsync(background, force: false);

        Assert.True(closed);
        Assert.Same(selected, _viewModel.SelectedWorkspace);
    }

    [Fact]
    public async Task SaveAs_UsesFileNameForTabTitle()
    {
        var workspace = await _viewModel.CreateNewAsync(includeWelcomeContent: false);
        var destinationPath = Path.Combine(_directory, "Reference board.omniref");

        await workspace.SaveAsAsync(destinationPath);

        Assert.Equal("Reference board", workspace.DisplayTitle);
        Assert.NotNull(_store.SavedAsDocument);
    }

    [Fact]
    public async Task Open_UsesCurrentFileNameForTabTitle()
    {
        _store.RestoredDocument = new WorkspaceDocument();

        var workspace = await _viewModel.OpenAsync(Path.Combine(_directory, "Renamed board.omniref"));

        Assert.NotNull(workspace);
        Assert.Equal("Renamed board", workspace.DisplayTitle);
    }

    [Fact]
    public async Task MissingBackingFile_StopsAutomaticSaveAndKeepsWorkspaceOpen()
    {
        var workspace = await _viewModel.CreateNewAsync(includeWelcomeContent: false);
        var savesBeforeDeletion = _store.SaveCount;
        _store.LastLease!.IsCurrent = false;

        workspace.AddText("Still in memory", new WorldPoint(10, 20));
        await workspace.FlushAsync();

        Assert.True(workspace.IsBackingFileMissing);
        Assert.True(workspace.IsDirty);
        Assert.Equal(WorkspaceSaveState.Failed, workspace.SaveState);
        Assert.NotNull(workspace.SaveError);
        Assert.Equal(savesBeforeDeletion, _store.SaveCount);
        Assert.Contains(workspace, _viewModel.Workspaces);
    }

    [Fact]
    public async Task RestoredBackingFile_ReconnectsBeforeSavingMemoryChanges()
    {
        var workspace = await _viewModel.CreateNewAsync(includeWelcomeContent: false);
        _store.LastLease!.IsCurrent = false;
        workspace.AddText("Unsaved", new WorldPoint(10, 20));
        await workspace.FlushAsync();
        _store.RestoredDocument = workspace.Document.DeepClone();
        var savesBeforeRetry = _store.SaveCount;

        var reconnected = await workspace.TryReconnectBackingFileAsync();
        await workspace.FlushAsync();

        Assert.True(reconnected);
        Assert.False(workspace.IsBackingFileMissing);
        Assert.Equal(WorkspaceSaveState.Saved, workspace.SaveState);
        Assert.Equal(savesBeforeRetry + 1, _store.SaveCount);
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        _previewCache.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class TestWorkspaceStore : IWorkspaceStore
    {
        public WorkspaceDocument? SavedAsDocument { get; private set; }
        public TestWorkspaceFileLease? LastLease { get; private set; }
        public int SaveCount { get; private set; }
        public WorkspaceDocument? RestoredDocument { get; set; }

        public IWorkspaceFileLease AcquireFileLease(string path)
        {
            LastLease = new TestWorkspaceFileLease(Path.GetFullPath(path));
            return LastLease;
        }

        public Task<WorkspaceOpenResult> OpenAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceOpenResult(
                RestoredDocument?.DeepClone() ?? throw new FileNotFoundException(),
                WorkspaceOpenMode.ReadWrite));

        public Task SaveAsync(
            string path,
            WorkspaceDocument document,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task SaveAsAsync(
            string sourcePath,
            string destinationPath,
            WorkspaceDocument document,
            CancellationToken cancellationToken = default)
        {
            SavedAsDocument = document.DeepClone();
            return Task.CompletedTask;
        }

        public Task<EmbeddedAssetInfo> ImportEmbeddedAssetAsync(
            string workspacePath,
            string sourcePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EmbeddedAssetInfo> ImportEmbeddedAssetAsync(
            string workspacePath,
            Stream source,
            string fileName,
            string? mediaType,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ExportEmbeddedAssetAsync(
            string workspacePath,
            Guid assetId,
            string destinationPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]> ReadEmbeddedAssetAsync(
            string workspacePath,
            Guid assetId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CompactAsync(
            string workspacePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestWorkspaceFileLease(string path) : IWorkspaceFileLease
    {
        public string Path { get; } = path;
        public bool IsCurrent { get; set; } = true;
        public void Dispose() => IsCurrent = false;
    }

    private sealed class TestThumbnailProvider : IThumbnailProvider
    {
        public Task<ThumbnailData?> GetThumbnailAsync(
            string path,
            int requestedPixels,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ThumbnailData?>(null);
    }

    private sealed class TestPlatformShell : IPlatformShell
    {
        public bool OpenPath(string path) => true;
        public bool RevealPath(string path) => true;
        public bool OpenUrl(string url) => true;
    }
}
