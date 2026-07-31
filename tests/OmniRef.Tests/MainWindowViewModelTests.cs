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
    public async Task CloseBackgroundWorkspace_KeepsSelectedTab()
    {
        var background = await _viewModel.CreateNewAsync(includeWelcomeContent: false);
        var selected = await _viewModel.CreateNewAsync(includeWelcomeContent: false);

        var closed = await _viewModel.CloseAsync(background, force: false);

        Assert.True(closed);
        Assert.Same(selected, _viewModel.SelectedWorkspace);
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
        public Task<WorkspaceOpenResult> OpenAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveAsync(
            string path,
            WorkspaceDocument document,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveAsAsync(
            string sourcePath,
            string destinationPath,
            WorkspaceDocument document,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
