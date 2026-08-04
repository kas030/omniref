using OmniRef.App.Services;
using OmniRef.App.ViewModels;
using OmniRef.Core.Interfaces;
using OmniRef.Core.Models;
using OmniRef.Core.Services;
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
    private readonly TestPlatformShell _shell = new();
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
            _shell,
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
    public async Task Open_WithEmptySearchQueryShowsAllItems()
    {
        _store.RestoredDocument = new WorkspaceDocument
        {
            Items =
            [
                BoardItemFactory.Text("First", new WorldPoint(0, 0), 1),
                BoardItemFactory.Text("Second", new WorldPoint(20, 20), 2)
            ]
        };

        var workspace = await _viewModel.OpenAsync(Path.Combine(_directory, "References.omniref"));

        Assert.NotNull(workspace);
        Assert.Empty(workspace.SearchQuery);
        Assert.Equal(2, workspace.SearchResults.Count);
        Assert.All(workspace.Items, item => Assert.Contains(item, workspace.SearchResults));
        Assert.Equal([2, 1], workspace.SearchResults.Select(item => item.Model.ZIndex));
    }

    [Fact]
    public async Task SearchSortMode_LastAccessedUtcOrdersNewestFirst()
    {
        var oldest = SearchItem("Oldest", 30, DateTimeOffset.UtcNow.AddDays(-3));
        var newest = SearchItem("Newest", 10, DateTimeOffset.UtcNow.AddDays(-1));
        var middle = SearchItem("Middle", 20, DateTimeOffset.UtcNow.AddDays(-2));
        _store.RestoredDocument = new WorkspaceDocument { Items = [oldest, newest, middle] };

        var workspace = await _viewModel.OpenAsync(Path.Combine(_directory, "Recent.omniref"));

        Assert.NotNull(workspace);
        workspace.SearchSortMode = SearchSortMode.LastAccessedUtc;

        Assert.Equal(
            ["Newest", "Middle", "Oldest"],
            workspace.SearchResults.Select(item => item.Model.Title));
    }

    [Fact]
    public async Task SearchSortMode_RelevanceOrdersScoresAndKeepsLayerOrderForTies()
    {
        var fuzzy = SearchItem("Gogle", 20, DateTimeOffset.UtcNow);
        var exact = SearchItem("Google", 10, DateTimeOffset.UtcNow);
        _store.RestoredDocument = new WorkspaceDocument { Items = [fuzzy, exact] };

        var workspace = await _viewModel.OpenAsync(Path.Combine(_directory, "Relevance.omniref"));

        Assert.NotNull(workspace);
        workspace.SearchQuery = "google";
        workspace.SearchSortMode = SearchSortMode.Relevance;

        Assert.Equal(
            ["Google", "Gogle"],
            workspace.SearchResults.Select(item => item.Model.Title));

        workspace.SearchQuery = string.Empty;

        Assert.Equal(["Gogle", "Google"], workspace.SearchResults.Select(item => item.Model.Title));
    }

    [Fact]
    public async Task SearchSortMode_LastAccessedUtcReordersAfterSuccessfulOpen()
    {
        var older = BoardItemFactory.Url("https://older.example.com", new WorldPoint(0, 0), 10);
        older.LastAccessedUtc = DateTimeOffset.UtcNow.AddDays(-2);
        var newer = BoardItemFactory.Url("https://newer.example.com", new WorldPoint(20, 20), 20);
        newer.LastAccessedUtc = DateTimeOffset.UtcNow.AddDays(-1);
        _store.RestoredDocument = new WorkspaceDocument { Items = [older, newer] };

        var workspace = await _viewModel.OpenAsync(Path.Combine(_directory, "Access sort.omniref"));

        Assert.NotNull(workspace);
        workspace.SearchSortMode = SearchSortMode.LastAccessedUtc;
        Assert.Equal([newer.Id, older.Id], workspace.SearchResults.Select(item => item.Id));

        await workspace.OpenItemAsync(workspace.Items.Single(item => item.Id == older.Id));

        Assert.Equal([older.Id, newer.Id], workspace.SearchResults.Select(item => item.Id));
    }

    [Fact]
    public async Task OpenItem_RecordsAccessOnlyWhenShellOpenSucceeds()
    {
        var originalAccess = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var model = BoardItemFactory.Url("https://example.com", new WorldPoint(0, 0), 0);
        model.LastAccessedUtc = originalAccess;
        _store.RestoredDocument = new WorkspaceDocument { Items = [model] };
        var workspace = await _viewModel.OpenAsync(Path.Combine(_directory, "Access.omniref"));
        Assert.NotNull(workspace);
        var item = Assert.Single(workspace.Items);

        _shell.OpenSucceeds = false;
        await workspace.OpenItemAsync(item);
        Assert.Equal(originalAccess, item.Model.LastAccessedUtc);
        Assert.False(workspace.IsDirty);

        _shell.OpenSucceeds = true;
        await workspace.OpenItemAsync(item);
        Assert.True(item.Model.LastAccessedUtc > originalAccess);
        Assert.True(workspace.IsDirty);
    }

    [Fact]
    public async Task AddFirstItem_StartsZIndexAtZero()
    {
        var workspace = await _viewModel.CreateNewAsync(includeWelcomeContent: false);

        var item = workspace.AddText("First", new WorldPoint(0, 0));

        Assert.Equal(0, item.Model.ZIndex);
    }

    [Fact]
    public async Task RepeatedCreation_OffsetsCardsAndFrames()
    {
        var workspace = await _viewModel.CreateNewAsync(includeWelcomeContent: false);

        var firstCard = workspace.AddText("First", new WorldPoint(100, 200));
        var secondCard = workspace.AddText("Second", new WorldPoint(100, 200));
        var thirdCard = workspace.AddText("Third", new WorldPoint(100, 200));
        var firstFrame = workspace.AddFrame("First group", new WorldPoint(500, 600));
        var secondFrame = workspace.AddFrame("Second group", new WorldPoint(500, 600));

        Assert.Equal(new WorldPoint(100, 200), new WorldPoint(firstCard.Bounds.X, firstCard.Bounds.Y));
        Assert.Equal(firstCard.Bounds.Translate(28, 28), secondCard.Bounds);
        Assert.Equal(secondCard.Bounds.Translate(28, 28), thirdCard.Bounds);
        Assert.Equal(new WorldPoint(500, 600), new WorldPoint(firstFrame.Bounds.X, firstFrame.Bounds.Y));
        Assert.Equal(firstFrame.Bounds.Translate(28, 28), secondFrame.Bounds);
    }

    [Fact]
    public async Task SendAlreadyBottomItemToBack_DoesNotNormalizeOrMarkDirty()
    {
        _store.RestoredDocument = new WorkspaceDocument
        {
            Items =
            [
                BoardItemFactory.Text("Bottom", new WorldPoint(0, 0), 10),
                BoardItemFactory.Text("Top", new WorldPoint(20, 20), 20)
            ]
        };
        var workspace = await _viewModel.OpenAsync(Path.Combine(_directory, "Sparse layers.omniref"));
        Assert.NotNull(workspace);
        var bottom = workspace.Items.Single(item => item.SecondaryText == "Bottom");
        workspace.SelectOnly(bottom);

        workspace.MoveSelectionLayer(LayerMove.SendToBack);

        Assert.Equal(10, bottom.Model.ZIndex);
        Assert.Equal(20, workspace.Items.Single(item => item.SecondaryText == "Top").Model.ZIndex);
        Assert.False(workspace.IsDirty);
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public async Task SendItemToBack_NormalizesChangedLayerOrder()
    {
        _store.RestoredDocument = new WorkspaceDocument
        {
            Items =
            [
                BoardItemFactory.Text("Bottom", new WorldPoint(0, 0), 10),
                BoardItemFactory.Text("Middle", new WorldPoint(20, 20), 20),
                BoardItemFactory.Text("Top", new WorldPoint(40, 40), 30)
            ]
        };
        var workspace = await _viewModel.OpenAsync(Path.Combine(_directory, "Layer change.omniref"));
        Assert.NotNull(workspace);
        workspace.SelectOnly(workspace.Items.Single(item => item.SecondaryText == "Top"));

        workspace.MoveSelectionLayer(LayerMove.SendToBack);

        var ordered = workspace.Items.OrderBy(item => item.Model.ZIndex).ToList();
        Assert.Equal(["Top", "Bottom", "Middle"], ordered.Select(item => item.SecondaryText));
        Assert.Equal([0, 1, 2], ordered.Select(item => item.Model.ZIndex));
        Assert.True(workspace.IsDirty);
        Assert.True(workspace.CanUndo);
    }

    [Fact]
    public async Task AddItem_WhenZIndexIsExhausted_NormalizesBeforeAppending()
    {
        _store.RestoredDocument = new WorkspaceDocument
        {
            Items =
            [
                BoardItemFactory.Text("Bottom", new WorldPoint(0, 0), 10),
                BoardItemFactory.Text("Top", new WorldPoint(20, 20), int.MaxValue)
            ]
        };
        var workspace = await _viewModel.OpenAsync(Path.Combine(_directory, "Exhausted layers.omniref"));
        Assert.NotNull(workspace);

        workspace.AddText("New", new WorldPoint(40, 40));

        var ordered = workspace.Items.OrderBy(item => item.Model.ZIndex).ToList();
        Assert.Equal(["Bottom", "Top", "New"], ordered.Select(item => item.SecondaryText));
        Assert.Equal([0, 1, 2], ordered.Select(item => item.Model.ZIndex));
    }

    [Fact]
    public async Task UnembedAndRelink_UsesSelectedFileAndSupportsUndoRedo()
    {
        var embeddedAssetId = Guid.NewGuid();
        _store.RestoredDocument = new WorkspaceDocument
        {
            Items =
            [
                new BoardItem
                {
                    Kind = ItemKind.File,
                    Title = "Embedded reference",
                    Content = new FileContent(new SourceDescriptor(
                        @"C:\old\reference.bin",
                        null,
                        AssetMode.EmbeddedCopy,
                        embeddedAssetId,
                        "reference.bin",
                        128,
                        null), ".bin")
                }
            ]
        };
        var workspacePath = Path.Combine(_directory, "References.omniref");
        var sourcePath = Path.Combine(_directory, "linked.bin");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(sourcePath, new byte[2048]);
        var workspace = await _viewModel.OpenAsync(workspacePath);
        Assert.NotNull(workspace);
        var item = Assert.Single(workspace.Items);
        workspace.SelectOnly(item);

        var relinked = workspace.UnembedAndRelinkSelected(sourcePath);

        Assert.True(relinked);
        var external = Assert.IsType<FileContent>(item.Model.Content).Source;
        Assert.Equal(AssetMode.ExternalReference, external.Mode);
        Assert.Null(external.EmbeddedAssetId);
        Assert.Equal(Path.GetFullPath(sourcePath), external.AbsolutePath);
        Assert.Equal("linked.bin", external.RelativePath);
        Assert.Equal("linked.bin", external.OriginalFileName);
        Assert.Equal(2048, external.Size);
        Assert.True(workspace.CanUndo);

        workspace.Undo();

        var restored = Assert.IsType<FileContent>(item.Model.Content).Source;
        Assert.Equal(AssetMode.EmbeddedCopy, restored.Mode);
        Assert.Equal(embeddedAssetId, restored.EmbeddedAssetId);

        workspace.Redo();

        var redone = Assert.IsType<FileContent>(item.Model.Content).Source;
        Assert.Equal(AssetMode.ExternalReference, redone.Mode);
        Assert.Null(redone.EmbeddedAssetId);
    }

    [Fact]
    public async Task Compact_ProtectsDeletedEmbeddedAssetNeededByUndo()
    {
        var embeddedAssetId = Guid.NewGuid();
        _store.RestoredDocument = new WorkspaceDocument
        {
            Items =
            [
                new BoardItem
                {
                    Kind = ItemKind.File,
                    Title = "Embedded reference",
                    Content = new FileContent(new SourceDescriptor(
                        null,
                        null,
                        AssetMode.EmbeddedCopy,
                        embeddedAssetId,
                        "reference.bin",
                        128,
                        null), ".bin")
                }
            ]
        };
        var workspace = await _viewModel.OpenAsync(Path.Combine(_directory, "Undo.omniref"));
        Assert.NotNull(workspace);
        workspace.SelectOnly(Assert.Single(workspace.Items));
        workspace.RemoveSelected();
        Assert.Empty(workspace.Items);

        await workspace.CompactAsync();

        Assert.Contains(embeddedAssetId, _store.LastProtectedAssetIds);
        Assert.True(workspace.CanUndo);

        workspace.Undo();

        var restored = Assert.Single(workspace.Items);
        var source = Assert.IsType<FileContent>(restored.Model.Content).Source;
        Assert.Equal(embeddedAssetId, source.EmbeddedAssetId);
        Assert.Equal(AssetMode.EmbeddedCopy, source.Mode);
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

    [Fact]
    public async Task ViewportChange_UsesMetadataOnlySave()
    {
        var workspace = await _viewModel.CreateNewAsync(includeWelcomeContent: false);
        var fullSaves = _store.SaveCount;

        workspace.SetViewport(new WorldPoint(120, -45), 1.75, interactionComplete: true);
        await workspace.FlushAsync();

        Assert.Equal(fullSaves, _store.SaveCount);
        Assert.Equal(1, _store.ViewportSaveCount);
    }

    [Fact]
    public async Task ContentChange_WithViewportChange_UsesFullSave()
    {
        var workspace = await _viewModel.CreateNewAsync(includeWelcomeContent: false);
        var fullSaves = _store.SaveCount;

        workspace.SetViewport(new WorldPoint(120, -45), 1.75, interactionComplete: true);
        workspace.AddText("Changed", new WorldPoint(10, 20));
        await workspace.FlushAsync();

        Assert.Equal(fullSaves + 1, _store.SaveCount);
        Assert.Equal(0, _store.ViewportSaveCount);
    }

    [Fact]
    public async Task Compact_ShowsPersistentDetailedNotificationWhileStorageTextRemainsSizeOnly()
    {
        _store.CompactionResult = new WorkspaceCompactionResult(
            1024L * 1024 * 1024,
            512L * 1024 * 1024,
            3);
        var workspace = await _viewModel.CreateNewAsync(includeWelcomeContent: false);

        await workspace.CompactAsync();

        Assert.True(workspace.IsCompactionNotificationVisible);
        Assert.Equal("Workspace compacted", workspace.CompactionNotificationTitle);
        Assert.Contains("1 GB → 512 MB", workspace.CompactionNotificationMessage);
        Assert.Contains("Unused embedded assets removed: 3", workspace.CompactionNotificationMessage);
        Assert.Equal("512 MB", workspace.StorageSizeText);

        _store.CompactionResult = new WorkspaceCompactionResult(
            512L * 1024 * 1024,
            256L * 1024 * 1024,
            0);
        await workspace.CompactAsync();

        Assert.DoesNotContain("unused embedded", workspace.CompactionNotificationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("256 MB", workspace.StorageSizeText);

        workspace.DismissCompactionNotification();

        Assert.False(workspace.IsCompactionNotificationVisible);
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

    private static BoardItem SearchItem(string title, int zIndex, DateTimeOffset lastAccessedUtc) => new()
    {
        Title = title,
        Kind = ItemKind.Text,
        Content = new TextContent(title),
        ZIndex = zIndex,
        LastAccessedUtc = lastAccessedUtc
    };

    private sealed class TestWorkspaceStore : IWorkspaceStore
    {
        public WorkspaceDocument? SavedAsDocument { get; private set; }
        public TestWorkspaceFileLease? LastLease { get; private set; }
        public int SaveCount { get; private set; }
        public int ViewportSaveCount { get; private set; }
        public IReadOnlyCollection<Guid> LastProtectedAssetIds { get; private set; } = [];
        public WorkspaceDocument? RestoredDocument { get; set; }
        public WorkspaceCompactionResult CompactionResult { get; set; } = new(0, 0, 0);

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

        public Task SaveViewportAsync(
            string path,
            WorldPoint origin,
            double zoom,
            DateTimeOffset lastAccessedUtc,
            CancellationToken cancellationToken = default)
        {
            ViewportSaveCount++;
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

        public Task<WorkspaceCompactionInfo> AnalyzeCompactionAsync(
            string workspacePath,
            CancellationToken cancellationToken = default,
            IReadOnlyCollection<Guid>? protectedAssetIds = null)
        {
            LastProtectedAssetIds = protectedAssetIds?.ToList() ?? [];
            return Task.FromResult(new WorkspaceCompactionInfo(0, 0, 0));
        }

        public Task<WorkspaceCompactionResult> CompactAsync(
            string workspacePath,
            CancellationToken cancellationToken = default,
            IReadOnlyCollection<Guid>? protectedAssetIds = null)
        {
            LastProtectedAssetIds = protectedAssetIds?.ToList() ?? [];
            return Task.FromResult(CompactionResult);
        }
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
        public bool OpenSucceeds { get; set; } = true;

        public bool OpenPath(string path) => OpenSucceeds;
        public bool RevealPath(string path) => true;
        public bool OpenUrl(string url) => OpenSucceeds;
    }
}
