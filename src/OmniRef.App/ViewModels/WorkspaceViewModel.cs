using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OmniRef.App.Services;
using OmniRef.Core.Interfaces;
using OmniRef.Core.Models;
using OmniRef.Core.Services;
using OmniRef.Infrastructure.Windows.Diagnostics;

namespace OmniRef.App.ViewModels;

public enum WorkspaceSaveState
{
    Saved,
    Unsaved,
    Saving,
    Failed,
    ReadOnly
}

public enum CompactionNotificationState
{
    None,
    Running,
    Completed,
    Failed
}

public sealed record ItemLayoutState(WorldRect Bounds, Guid? ParentFrameId);

public enum AlignmentKind
{
    Left,
    HorizontalCenter,
    Right,
    Top,
    VerticalCenter,
    Bottom
}

public enum LayerMove
{
    BringToFront,
    BringForward,
    SendBackward,
    SendToBack
}

public enum SearchSortMode
{
    Layer,
    LastAccessedUtc,
    Relevance
}

public sealed class WorkspaceViewModel : ObservableObject, IDisposable
{
    private const int InitialImagePreviewPixels = 512;
    private const long CompactionRecommendationMinimumBytes = 32L * 1024 * 1024;
    private const double CompactionRecommendationMinimumRatio = 0.10;
    private const double RepeatedCreationOffset = 28;
    private static readonly WorldSize ImportedImageCardMaximum = new(300, 220);
    private static readonly WorldSize PastedImageCardMaximum = new(320, 240);

    private readonly IWorkspaceStore _store;
    private IWorkspaceFileLease _fileLease;
    private readonly IPlatformShell _shell;
    private readonly PreviewCache _previewCache;
    private readonly RollingFileLogger _logger;
    private readonly LocalizationService _localization;
    private readonly string _openCacheDirectory;
    private readonly DispatcherTimer _saveTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly UndoHistory _history = new(100);
    private string _path;
    private string _searchQuery = string.Empty;
    private SearchSortMode _searchSortMode = SearchSortMode.Layer;
    private WorkspaceSaveState _saveState;
    private string? _saveError;
    private int _changeVersion;
    private int _savedVersion;
    private bool _requiresFullSave;
    private int _interactionDepth;
    private long _workspaceFileSize;
    private long _estimatedReclaimableBytes;
    private int _unreferencedAssetCount;
    private bool _isCompacting;
    private WorkspaceCompactionResult? _lastCompactionResult;
    private CompactionNotificationState _compactionNotificationState;
    private bool _isCompactionNotificationVisible;
    private string? _compactionFailureDetail;
    private bool _disposed;

    public WorkspaceViewModel(
        WorkspaceDocument document,
        string path,
        bool isRecovery,
        WorkspaceOpenMode openMode,
        IWorkspaceStore store,
        IWorkspaceFileLease fileLease,
        IPlatformShell shell,
        PreviewCache previewCache,
        RollingFileLogger logger,
        LocalizationService localization,
        string openCacheDirectory)
    {
        Document = document;
        _path = System.IO.Path.GetFullPath(path);
        IsRecovery = isRecovery;
        OpenMode = openMode;
        _store = store;
        _fileLease = fileLease;
        _shell = shell;
        _previewCache = previewCache;
        _logger = logger;
        _localization = localization;
        _openCacheDirectory = openCacheDirectory;
        _saveState = openMode == WorkspaceOpenMode.ReadOnly
            ? WorkspaceSaveState.ReadOnly
            : WorkspaceSaveState.Saved;
        _workspaceFileSize = TryGetWorkspaceFileSize();

        foreach (var item in document.Items.OrderBy(item => item.ZIndex))
        {
            AddViewModel(item);
        }
        UpdateSearch();

        _saveTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            OnSaveTimer,
            Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();
        _localization.PropertyChanged += OnLocalizationChanged;
        RefreshMissingSources();
    }

    public WorkspaceDocument Document { get; }
    public ObservableCollection<BoardItemViewModel> Items { get; } = [];
    public ObservableCollection<BoardItemViewModel> SearchResults { get; } = [];
    public bool IsRecovery { get; private set; }
    public WorkspaceOpenMode OpenMode { get; }
    public bool IsReadOnly => OpenMode == WorkspaceOpenMode.ReadOnly;
    public bool IsBackingFileMissing { get; private set; }
    public string Path => _path;
    public bool IsDirty => _changeVersion != _savedVersion;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public string ZoomPercentText => $"{Document.Zoom * 100:0}%";
    public bool IsCompacting
    {
        get => _isCompacting;
        private set
        {
            if (SetProperty(ref _isCompacting, value))
            {
                OnPropertyChanged(nameof(CanCompact));
                OnPropertyChanged(nameof(StorageToolTip));
                OnPropertyChanged(nameof(CompactionButtonToolTip));
            }
        }
    }

    public bool CanCompact => !IsReadOnly && !IsCompacting && !IsBackingFileMissing;

    public bool IsCompactionRecommended =>
        _estimatedReclaimableBytes >= CompactionRecommendationMinimumBytes &&
        _workspaceFileSize > 0 &&
        (double)_estimatedReclaimableBytes / _workspaceFileSize >=
        CompactionRecommendationMinimumRatio;

    public string StorageSummaryText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        _localization["WorkspaceStorageSummary"],
        FormatFileSize(_workspaceFileSize),
        _workspaceFileSize <= 0
            ? 0
            : Math.Clamp(
                (double)_estimatedReclaimableBytes / _workspaceFileSize * 100,
                0,
                100));

    public string StorageToolTip => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        _localization["WorkspaceSize"],
        FormatFileSize(_workspaceFileSize));

    public bool IsCompactionNotificationVisible
    {
        get => _isCompactionNotificationVisible;
        private set => SetProperty(ref _isCompactionNotificationVisible, value);
    }

    public string CompactionNotificationTitle => _compactionNotificationState switch
    {
        CompactionNotificationState.Running => _localization["CompactionRunningTitle"],
        CompactionNotificationState.Completed => _localization["CompactionCompleteTitle"],
        CompactionNotificationState.Failed => _localization["CompactionFailedTitle"],
        _ => _localization["Compact"]
    };

    public string CompactionNotificationMessage
    {
        get
        {
            if (_compactionNotificationState == CompactionNotificationState.Running)
            {
                return _localization["CompactionRunningMessage"];
            }
            if (_compactionNotificationState == CompactionNotificationState.Completed &&
                _lastCompactionResult is { } result)
            {
                var reclaimedPercent = result.SizeBefore > 0
                    ? (double)result.ReclaimedBytes / result.SizeBefore * 100
                    : 0;
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    result.RemovedAssetCount > 0
                        ? _localization["CompactionCompleteMessageWithAssets"]
                        : _localization["CompactionCompleteMessage"],
                    FormatFileSize(result.SizeBefore),
                    FormatFileSize(result.SizeAfter),
                    FormatFileSize(result.ReclaimedBytes),
                    reclaimedPercent,
                    result.RemovedAssetCount);
            }
            if (_compactionNotificationState == CompactionNotificationState.Failed)
            {
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    _localization["CompactionFailedMessage"],
                    _compactionFailureDetail ?? _localization["WorkspaceError"]);
            }
            return string.Empty;
        }
    }

    public string CompactionButtonToolTip => IsCompactionRecommended
        ? string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _localization["CompactRecommended"],
            FormatFileSize(_estimatedReclaimableBytes),
            _unreferencedAssetCount)
        : _localization["Compact"];

    public string DisplayTitle
    {
        get
        {
            var title = IsRecovery
                ? _localization["Untitled"]
                : System.IO.Path.GetFileNameWithoutExtension(_path);
            return IsDirty ? $"{title} •" : title;
        }
    }

    public WorkspaceSaveState SaveState
    {
        get => _saveState;
        private set
        {
            if (SetProperty(ref _saveState, value))
            {
                OnPropertyChanged(nameof(SaveStatusText));
            }
        }
    }

    public string SaveStatusText => SaveState switch
    {
        WorkspaceSaveState.Saved => _localization["Saved"],
        WorkspaceSaveState.Saving => _localization["Saving"],
        WorkspaceSaveState.Unsaved => _localization["Unsaved"],
        WorkspaceSaveState.Failed => _localization["SaveFailed"],
        WorkspaceSaveState.ReadOnly => _localization["ReadOnly"],
        _ => SaveState.ToString()
    };

    public string? SaveError
    {
        get => _saveError;
        private set => SetProperty(ref _saveError, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                UpdateSearch();
            }
        }
    }

    public SearchSortMode SearchSortMode
    {
        get => _searchSortMode;
        set
        {
            if (SetProperty(ref _searchSortMode, value))
            {
                UpdateSearch();
            }
        }
    }

    public BoardItemViewModel? SelectedItem =>
        Items.Where(item => item.IsSelected).OrderByDescending(item => item.Model.ZIndex).FirstOrDefault();

    public IReadOnlyList<BoardItemViewModel> SelectedItems =>
        Items.Where(item => item.IsSelected).ToList();

    public bool CanAlignSelection => !IsReadOnly && TopLevelSelection().Count >= 2;
    public bool CanDistributeSelection => !IsReadOnly && TopLevelSelection().Count >= 3;
    public bool CanMoveSelectionLayer => !IsReadOnly && SelectedItems.Count > 0;

    public event EventHandler? ItemsChanged;
    public event EventHandler? VisualInvalidated;
    public event EventHandler<BoardItemViewModel>? FocusItemRequested;
    public event EventHandler? SelectionChanged;

    private int NextZIndex(int itemCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemCount);
        if (Items.Count == 0)
        {
            return 0;
        }

        var maximum = Items.Max(item => item.Model.ZIndex);
        if ((long)maximum + itemCount <= int.MaxValue)
        {
            return maximum + 1;
        }

        if ((long)Items.Count + itemCount - 1 > int.MaxValue)
        {
            throw new InvalidOperationException("The workspace contains too many items to assign a layer.");
        }

        NormalizeZIndices();
        return Items.Count;
    }

    private WorldPoint FindAvailableCreationPosition(
        WorldPoint requestedPosition,
        IEnumerable<WorldPoint>? relativePositions = null,
        IEnumerable<WorldRect>? additionalOccupiedBounds = null)
    {
        var offsets = relativePositions?.ToList() ?? [new WorldPoint(0, 0)];
        if (offsets.Count == 0)
        {
            offsets.Add(new WorldPoint(0, 0));
        }

        var occupiedBounds = Items
            .Select(item => item.Bounds)
            .Concat(additionalOccupiedBounds ?? Enumerable.Empty<WorldRect>())
            .ToList();
        var candidate = requestedPosition;
        while (offsets.Any(offset => occupiedBounds.Any(bounds =>
                   AreClose(bounds.X, candidate.X + offset.X) &&
                   AreClose(bounds.Y, candidate.Y + offset.Y))))
        {
            candidate = new WorldPoint(
                candidate.X + RepeatedCreationOffset,
                candidate.Y + RepeatedCreationOffset);
        }

        return candidate;
    }

    public async Task AddPathsAsync(
        IEnumerable<string> paths,
        WorldPoint start,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        var pathsToAdd = paths
            .Where(PathResolver.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pathsToAdd.Count == 0)
        {
            return;
        }

        var models = new List<BoardItem>();
        var additionalOccupiedBounds = new List<WorldRect>();
        var index = 0;
        foreach (var path in pathsToAdd)
        {
            var column = index % 4;
            var row = index / 4;
            var requestedPosition = new WorldPoint(
                start.X + (column * 28),
                start.Y + (row * 28));
            var creationPosition = FindAvailableCreationPosition(
                requestedPosition,
                additionalOccupiedBounds: additionalOccupiedBounds);
            models.Add(BoardItemFactory.FromPath(
                _path,
                path,
                creationPosition,
                0));
            additionalOccupiedBounds.Add(models[^1].Bounds);
            index++;
        }

        var previews = await Task.WhenAll(
                models.Select(model => model.Kind == ItemKind.Image
                    ? _previewCache.GetAsync(
                        _path,
                        model,
                        InitialImagePreviewPixels,
                        cancellationToken)
                    : Task.FromResult<ImageSource?>(null)))
            .ConfigureAwait(true);
        for (var modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            ApplyImageAspectRatio(
                models[modelIndex],
                previews[modelIndex],
                ImportedImageCardMaximum);
        }

        var nextZIndex = NextZIndex(models.Count);
        for (var modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            models[modelIndex].ZIndex = nextZIndex + modelIndex;
        }
        AddModelsWithUndo(models, "Add files");

        for (var modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            if (previews[modelIndex] is { } preview)
            {
                Items.First(item => item.Id == models[modelIndex].Id).Preview = preview;
            }
        }
    }

    public BoardItemViewModel AddText(string text, WorldPoint position)
    {
        EnsureWritable();
        var creationPosition = FindAvailableCreationPosition(position);
        var model = BoardItemFactory.Text(text, creationPosition, NextZIndex());
        AddModelsWithUndo([model], "Add text");
        return Items.First(item => item.Id == model.Id);
    }

    public BoardItemViewModel AddUrl(string url, WorldPoint position)
    {
        EnsureWritable();
        var creationPosition = FindAvailableCreationPosition(position);
        var model = BoardItemFactory.Url(url, creationPosition, NextZIndex());
        AddModelsWithUndo([model], "Add URL");
        return Items.First(item => item.Id == model.Id);
    }

    public BoardItemViewModel AddFrame(string title, WorldPoint position)
    {
        EnsureWritable();
        var creationPosition = FindAvailableCreationPosition(position);
        var model = BoardItemFactory.Frame(title, creationPosition, NextZIndex());
        AddModelsWithUndo([model], "Add frame");
        return Items.First(item => item.Id == model.Id);
    }

    public async Task<BoardItemViewModel> AddEmbeddedImageAsync(
        byte[] pngBytes,
        WorldPoint position,
        CancellationToken cancellationToken = default,
        WorldSize? imageSize = null)
    {
        EnsureWritable();
        await FlushAsync(cancellationToken).ConfigureAwait(true);
        await using var stream = new MemoryStream(pngBytes, writable: false);
        var asset = await _store.ImportEmbeddedAssetAsync(
                _path,
                stream,
                $"Clipboard-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png",
                "image/png",
                cancellationToken)
            .ConfigureAwait(true);
        RefreshWorkspaceFileSize();
        var source = new SourceDescriptor(
            null,
            null,
            AssetMode.EmbeddedCopy,
            asset.Id,
            asset.FileName,
            asset.Length,
            DateTimeOffset.UtcNow);
        var boundsSize = GetInitialImageSize(imageSize, PastedImageCardMaximum);
        var creationPosition = FindAvailableCreationPosition(position);
        var model = new BoardItem
        {
            Kind = ItemKind.Image,
            Title = "Clipboard image",
            Bounds = new(
                creationPosition.X,
                creationPosition.Y,
                boundsSize.Width,
                boundsSize.Height),
            ZIndex = NextZIndex(),
            Content = new ImageContent(source)
        };
        AddModelsWithUndo([model], "Paste image");
        return Items.First(item => item.Id == model.Id);
    }

    public void AddClonedItems(IEnumerable<BoardItem> sourceItems, WorldPoint position)
    {
        EnsureWritable();
        var originals = sourceItems.Select(item => item.DeepClone()).ToList();
        if (originals.Count == 0)
        {
            return;
        }

        var minX = originals.Min(item => item.Bounds.X);
        var minY = originals.Min(item => item.Bounds.Y);
        var relativePositions = originals
            .Select(item => new WorldPoint(item.Bounds.X - minX, item.Bounds.Y - minY))
            .ToList();
        var creationPosition = FindAvailableCreationPosition(position, relativePositions);
        var idMap = originals.ToDictionary(item => item.Id, _ => Guid.NewGuid());
        var nextZ = NextZIndex(originals.Count);
        for (var index = 0; index < originals.Count; index++)
        {
            var item = originals[index];
            var oldId = item.Id;
            item.Id = idMap[oldId];
            item.Bounds = item.Bounds.Translate(position.X - minX, position.Y - minY);
            item.ZIndex = nextZ + index;
            item.ParentFrameId = item.ParentFrameId.HasValue &&
                                 idMap.TryGetValue(item.ParentFrameId.Value, out var parentId)
                ? parentId
                : null;
            item.CreatedUtc = DateTimeOffset.UtcNow;
            item.LastAccessedUtc = item.CreatedUtc;
        }

        AddModelsWithUndo(originals, "Paste items");
    }

    public void RemoveSelected()
    {
        EnsureWritable();
        var selected = SelectedItems.ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var deleted = selected
            .Select(item => (Index: Items.IndexOf(item), Model: item.Model.DeepClone()))
            .ToList();
        var protectedAssetIds = EmbeddedAssetIdsOf(deleted.Select(entry => entry.Model));
        var selectedIds = selected.Select(item => item.Id).ToHashSet();
        var affectedChildren = Items
            .Where(item => item.ParentFrameId.HasValue && selectedIds.Contains(item.ParentFrameId.Value))
            .ToDictionary(item => item.Id, item => item.ParentFrameId);

        void Execute()
        {
            foreach (var item in Items.Where(item => selectedIds.Contains(item.Id)).ToList())
            {
                RemoveViewModel(item);
            }
            foreach (var child in Items.Where(item => affectedChildren.ContainsKey(item.Id)))
            {
                child.UpdateParentFrame(null);
            }
            MarkDirty();
            RaiseCollectionState();
        }

        void Undo()
        {
            foreach (var deletedItem in deleted.OrderBy(entry => entry.Index))
            {
                InsertViewModel(Math.Min(deletedItem.Index, Items.Count), deletedItem.Model.DeepClone());
            }
            foreach (var child in Items.Where(item => affectedChildren.ContainsKey(item.Id)))
            {
                child.UpdateParentFrame(affectedChildren[child.Id]);
            }
            MarkDirty();
            RaiseCollectionState();
        }

        _history.Execute(new DelegateUndoableCommand(
            "Delete items",
            Execute,
            Undo,
            protectedAssetIds));
        RaiseHistoryState();
    }

    public void Undo()
    {
        if (IsReadOnly)
        {
            return;
        }
        _history.Undo();
        MarkDirty();
        RaiseCollectionState();
        RaiseHistoryState();
    }

    public void Redo()
    {
        if (IsReadOnly)
        {
            return;
        }
        _history.Redo();
        MarkDirty();
        RaiseCollectionState();
        RaiseHistoryState();
    }

    public void SelectOnly(BoardItemViewModel? item)
    {
        foreach (var candidate in Items)
        {
            candidate.IsSelected = ReferenceEquals(candidate, item);
        }
        RaiseSelectionChanged();
    }

    public void ToggleSelection(BoardItemViewModel item)
    {
        item.IsSelected = !item.IsSelected;
        RaiseSelectionChanged();
    }

    public void SelectAll()
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }
        RaiseSelectionChanged();
    }

    public void SelectInside(WorldRect bounds, bool additive)
    {
        if (!additive)
        {
            foreach (var item in Items)
            {
                item.IsSelected = false;
            }
        }
        foreach (var item in Items.Where(item => item.Bounds.Intersects(bounds)))
        {
            item.IsSelected = true;
        }
        RaiseSelectionChanged();
    }

    public IReadOnlyList<BoardItemViewModel> GetMovableSelection()
    {
        var selected = SelectedItems.ToList();
        var selectedFrames = selected
            .Where(item => item.Kind == ItemKind.Frame)
            .Select(item => item.Id)
            .ToHashSet();
        foreach (var child in Items.Where(item =>
                     item.ParentFrameId.HasValue &&
                     selectedFrames.Contains(item.ParentFrameId.Value) &&
                     !item.IsSelected))
        {
            selected.Add(child);
        }
        return selected;
    }

    public Dictionary<Guid, ItemLayoutState> CaptureLayout(IEnumerable<BoardItemViewModel> items) =>
        items.DistinctBy(item => item.Id).ToDictionary(
            item => item.Id,
            item => new ItemLayoutState(item.Bounds, item.ParentFrameId));

    public void BeginInteraction()
    {
        _interactionDepth++;
        _saveTimer.Stop();
    }

    public void EndInteraction(
        IReadOnlyDictionary<Guid, ItemLayoutState> before,
        string description,
        bool assignFrames)
    {
        if (assignFrames)
        {
            AssignFramesForSelected();
        }
        var after = before.Keys
            .Select(id => Items.FirstOrDefault(item => item.Id == id))
            .Where(item => item is not null)
            .Cast<BoardItemViewModel>()
            .ToDictionary(
                item => item.Id,
                item => new ItemLayoutState(item.Bounds, item.ParentFrameId));
        if (!LayoutsEqual(before, after))
        {
            _history.PushExecuted(
                new DelegateUndoableCommand(
                    description,
                    () => ApplyLayout(after),
                    () => ApplyLayout(before)));
            RaiseHistoryState();
            MarkDirty();
        }

        _interactionDepth = Math.Max(0, _interactionDepth - 1);
        if (_interactionDepth == 0 && IsDirty)
        {
            RestartSaveTimer();
        }
    }

    public void CancelInteraction(IReadOnlyDictionary<Guid, ItemLayoutState> before)
    {
        ApplyLayout(before);
        _interactionDepth = Math.Max(0, _interactionDepth - 1);
        if (_interactionDepth == 0 && IsDirty)
        {
            RestartSaveTimer();
        }
    }

    public void SetViewport(WorldPoint origin, double zoom, bool interactionComplete)
    {
        if (Document.ViewportOrigin == origin && Math.Abs(Document.Zoom - zoom) < 0.0001)
        {
            return;
        }
        Document.ViewportOrigin = origin;
        Document.Zoom = zoom;
        OnPropertyChanged(nameof(ZoomPercentText));
        MarkDirty(requiresFullSave: false);
        if (interactionComplete && _interactionDepth == 0)
        {
            RestartSaveTimer();
        }
    }

    public async Task EnsurePreviewAsync(
        BoardItemViewModel item,
        int requestedPixels,
        CancellationToken cancellationToken = default)
    {
        if (item.Preview is not null || item.PreviewLoading || item.IsMissing)
        {
            return;
        }

        item.PreviewLoading = true;
        try
        {
            item.Preview = await _previewCache.GetAsync(
                    _path,
                    item.Model,
                    requestedPixels,
                    cancellationToken)
                .ConfigureAwait(true);
            ApplyInitialImageAspectRatio(item, item.Preview);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            item.PreviewLoading = false;
            VisualInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    private static WorldSize GetInitialImageSize(WorldSize? imageSize, WorldSize maximumSize)
    {
        return imageSize is { } sourceSize &&
               double.IsFinite(sourceSize.Width) &&
               double.IsFinite(sourceSize.Height) &&
               sourceSize.Width > 0 &&
               sourceSize.Height > 0
            ? ResizeMath.FitWithin(sourceSize, maximumSize)
            : maximumSize;
    }

    private static void ApplyImageAspectRatio(
        BoardItem model,
        ImageSource? preview,
        WorldSize maximumSize)
    {
        if (model.Kind != ItemKind.Image ||
            !TryGetImageSize(preview, out var imageSize))
        {
            return;
        }

        var boundsSize = ResizeMath.FitWithin(imageSize, maximumSize);
        model.Bounds = new(
            model.Bounds.X,
            model.Bounds.Y,
            boundsSize.Width,
            boundsSize.Height);
    }

    private void ApplyInitialImageAspectRatio(
        BoardItemViewModel item,
        ImageSource? preview)
    {
        if (IsReadOnly ||
            item.Kind != ItemKind.Image ||
            !TryGetInitialImageMaximum(item.Bounds, out var maximumSize) ||
            !TryGetImageSize(preview, out var imageSize))
        {
            return;
        }

        var boundsSize = ResizeMath.FitWithin(imageSize, maximumSize);
        if (AreClose(item.Bounds.Width, boundsSize.Width) &&
            AreClose(item.Bounds.Height, boundsSize.Height))
        {
            return;
        }

        item.UpdateBounds(new(
            item.Bounds.X,
            item.Bounds.Y,
            boundsSize.Width,
            boundsSize.Height));
    }

    private static bool TryGetImageSize(ImageSource? preview, out WorldSize imageSize)
    {
        if (preview is not null &&
            double.IsFinite(preview.Width) &&
            double.IsFinite(preview.Height) &&
            preview.Width > 0 &&
            preview.Height > 0)
        {
            imageSize = new WorldSize(preview.Width, preview.Height);
            return true;
        }

        imageSize = default;
        return false;
    }

    private static bool TryGetInitialImageMaximum(
        WorldRect bounds,
        out WorldSize maximumSize)
    {
        if (AreClose(bounds.Width, PastedImageCardMaximum.Width) &&
            AreClose(bounds.Height, PastedImageCardMaximum.Height))
        {
            maximumSize = PastedImageCardMaximum;
            return true;
        }

        if (AreClose(bounds.Width, ImportedImageCardMaximum.Width) &&
            AreClose(bounds.Height, ImportedImageCardMaximum.Height))
        {
            maximumSize = ImportedImageCardMaximum;
            return true;
        }

        maximumSize = default;
        return false;
    }

    private static bool AreClose(double first, double second) =>
        Math.Abs(first - second) < 0.01;

    public void UpdateTextWithUndo(BoardItemViewModel item, string text)
    {
        EnsureWritable();
        if (item.Model.Content is not TextContent current || current.Text == text)
        {
            return;
        }

        var previous = current;
        var next = current with { Text = text };
        item.ReplaceContent(next);
        _history.PushExecuted(
            new DelegateUndoableCommand(
                "Edit text",
                () => item.ReplaceContent(next),
                () => item.ReplaceContent(previous)));
        MarkDirty();
        RaiseHistoryState();
        UpdateSearch();
    }

    public void NudgeSelected(double x, double y)
    {
        EnsureWritable();
        var movable = GetMovableSelection();
        if (movable.Count == 0)
        {
            return;
        }

        var before = CaptureLayout(movable);
        foreach (var item in movable)
        {
            item.UpdateBounds(item.Bounds.Translate(x, y));
        }
        EndInteraction(before, "Move items", assignFrames: true);
    }

    public void AlignSelected(AlignmentKind alignment)
    {
        EnsureWritable();
        var anchors = TopLevelSelection();
        if (anchors.Count < 2)
        {
            return;
        }

        var bounds = WorldRect.Union(anchors.Select(item => item.Bounds));
        TransformAnchors(
            anchors,
            alignment switch
            {
                AlignmentKind.Left => item => new WorldPoint(bounds.Left - item.Bounds.Left, 0),
                AlignmentKind.HorizontalCenter => item => new WorldPoint(
                    bounds.Center.X - item.Bounds.Center.X,
                    0),
                AlignmentKind.Right => item => new WorldPoint(bounds.Right - item.Bounds.Right, 0),
                AlignmentKind.Top => item => new WorldPoint(0, bounds.Top - item.Bounds.Top),
                AlignmentKind.VerticalCenter => item => new WorldPoint(
                    0,
                    bounds.Center.Y - item.Bounds.Center.Y),
                AlignmentKind.Bottom => item => new WorldPoint(0, bounds.Bottom - item.Bounds.Bottom),
                _ => throw new ArgumentOutOfRangeException(nameof(alignment))
            },
            $"Align {alignment}");
    }

    public void DistributeSelected(bool horizontally)
    {
        EnsureWritable();
        var anchors = TopLevelSelection()
            .OrderBy(item => horizontally ? item.Bounds.Center.X : item.Bounds.Center.Y)
            .ToList();
        if (anchors.Count < 3)
        {
            return;
        }

        var first = horizontally ? anchors[0].Bounds.Center.X : anchors[0].Bounds.Center.Y;
        var last = horizontally ? anchors[^1].Bounds.Center.X : anchors[^1].Bounds.Center.Y;
        var interval = (last - first) / (anchors.Count - 1);
        var targetPositions = anchors
            .Select((item, index) => (item.Id, Position: first + (interval * index)))
            .ToDictionary(entry => entry.Id, entry => entry.Position);
        TransformAnchors(
            anchors,
            item =>
            {
                var current = horizontally ? item.Bounds.Center.X : item.Bounds.Center.Y;
                var delta = targetPositions[item.Id] - current;
                return horizontally ? new WorldPoint(delta, 0) : new WorldPoint(0, delta);
            },
            horizontally ? "Distribute horizontally" : "Distribute vertically");
    }

    public void MoveSelectionLayer(LayerMove move)
    {
        EnsureWritable();
        var selectedIds = GetMovableSelection().Select(item => item.Id).ToHashSet();
        if (selectedIds.Count == 0)
        {
            return;
        }

        var ordered = Items
            .OrderBy(item => item.Model.ZIndex)
            .ThenBy(item => item.Model.CreatedUtc)
            .ToList();
        var beforeOrder = ordered.Select(item => item.Id).ToList();
        var before = ordered.ToDictionary(item => item.Id, item => item.Model.ZIndex);
        switch (move)
        {
            case LayerMove.BringToFront:
                ordered = ordered
                    .Where(item => !selectedIds.Contains(item.Id))
                    .Concat(ordered.Where(item => selectedIds.Contains(item.Id)))
                    .ToList();
                break;
            case LayerMove.SendToBack:
                ordered = ordered
                    .Where(item => selectedIds.Contains(item.Id))
                    .Concat(ordered.Where(item => !selectedIds.Contains(item.Id)))
                    .ToList();
                break;
            case LayerMove.BringForward:
                for (var index = ordered.Count - 2; index >= 0; index--)
                {
                    if (selectedIds.Contains(ordered[index].Id) &&
                        !selectedIds.Contains(ordered[index + 1].Id))
                    {
                        (ordered[index], ordered[index + 1]) = (ordered[index + 1], ordered[index]);
                    }
                }
                break;
            case LayerMove.SendBackward:
                for (var index = 1; index < ordered.Count; index++)
                {
                    if (selectedIds.Contains(ordered[index].Id) &&
                        !selectedIds.Contains(ordered[index - 1].Id))
                    {
                        (ordered[index], ordered[index - 1]) = (ordered[index - 1], ordered[index]);
                    }
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(move));
        }

        if (beforeOrder.SequenceEqual(ordered.Select(item => item.Id)))
        {
            return;
        }

        var after = ordered
            .Select((item, index) => (item.Id, ZIndex: index))
            .ToDictionary(entry => entry.Id, entry => entry.ZIndex);

        ApplyZIndices(after);
        _history.PushExecuted(
            new DelegateUndoableCommand(
                $"Layer {move}",
                () => ApplyZIndices(after),
                () => ApplyZIndices(before)));
        MarkDirty();
        RaiseHistoryState();
        VisualInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void ReleasePreviews()
    {
        foreach (var item in Items)
        {
            item.Preview = null;
        }
    }

    public async Task OpenItemAsync(BoardItemViewModel item, CancellationToken cancellationToken = default)
    {
        if (item.Model.Content is UrlContent url)
        {
            if (_shell.OpenUrl(url.Url))
            {
                RecordAccess(item);
            }
            return;
        }

        var source = SourceOf(item.Model.Content);
        if (source is null)
        {
            return;
        }

        if (source.Mode == AssetMode.ExternalReference)
        {
            var resolved = PathResolver.Resolve(_path, source);
            item.IsMissing = resolved is null;
            if (resolved is not null && _shell.OpenPath(resolved))
            {
                RecordAccess(item);
            }
            return;
        }

        if (!source.EmbeddedAssetId.HasValue)
        {
            return;
        }

        var directory = System.IO.Path.Combine(_openCacheDirectory, Document.Id.ToString("N"), source.EmbeddedAssetId.Value.ToString("N"));
        Directory.CreateDirectory(directory);
        var temporaryPath = System.IO.Path.Combine(directory, SanitizeFileName(source.OriginalFileName));
        if (File.Exists(temporaryPath))
        {
            File.SetAttributes(temporaryPath, FileAttributes.Normal);
        }
        await _store.ExportEmbeddedAssetAsync(_path, source.EmbeddedAssetId.Value, temporaryPath, cancellationToken)
            .ConfigureAwait(true);
        File.SetAttributes(temporaryPath, FileAttributes.ReadOnly);
        if (_shell.OpenPath(temporaryPath))
        {
            RecordAccess(item);
        }
    }

    public bool RevealSelected()
    {
        var item = SelectedItem;
        var source = item is null ? null : SourceOf(item.Model.Content);
        if (source?.Mode != AssetMode.ExternalReference)
        {
            return false;
        }
        var resolved = PathResolver.Resolve(_path, source);
        return resolved is not null && _shell.RevealPath(resolved);
    }

    public async Task<bool> EmbedSelectedAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        var item = SelectedItem;
        var source = item is null ? null : SourceOf(item.Model.Content);
        if (item is null ||
            source is null ||
            source.Mode != AssetMode.ExternalReference ||
            item.Kind is not (ItemKind.Image or ItemKind.File))
        {
            return false;
        }

        var resolved = PathResolver.Resolve(_path, source);
        if (resolved is null)
        {
            item.IsMissing = true;
            return false;
        }

        if (new FileInfo(resolved).Length >
            OmniRef.Infrastructure.Windows.Persistence.SqliteWorkspaceStore.MaximumEmbeddedAssetBytes)
        {
            throw new InvalidOperationException("Embedded assets cannot exceed 512 MB.");
        }

        await FlushAsync(cancellationToken).ConfigureAwait(true);
        var asset = await _store.ImportEmbeddedAssetAsync(_path, resolved, cancellationToken).ConfigureAwait(true);
        RefreshWorkspaceFileSize();
        var oldContent = item.Model.Content;
        var nextSource = source with
        {
            Mode = AssetMode.EmbeddedCopy,
            EmbeddedAssetId = asset.Id,
            Size = asset.Length,
            ModifiedUtc = DateTimeOffset.UtcNow
        };
        var nextContent = ReplaceSource(oldContent, nextSource);
        item.ReplaceContent(nextContent);
        _history.PushExecuted(
            new DelegateUndoableCommand(
                "Embed asset",
                () => item.ReplaceContent(nextContent),
                () => item.ReplaceContent(oldContent),
                EmbeddedAssetIdsOf(nextContent)));
        MarkDirty();
        RaiseHistoryState();
        return true;
    }

    public async Task<bool> ExportSelectedAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var item = SelectedItem;
        var source = item is null ? null : SourceOf(item.Model.Content);
        if (source?.Mode != AssetMode.EmbeddedCopy || !source.EmbeddedAssetId.HasValue)
        {
            return false;
        }

        await _store.ExportEmbeddedAssetAsync(
                _path,
                source.EmbeddedAssetId.Value,
                destinationPath,
                cancellationToken)
            .ConfigureAwait(true);
        return true;
    }

    public bool UnembedAndRelinkSelected(string sourcePath)
    {
        EnsureWritable();
        var item = SelectedItem;
        var source = item is null ? null : SourceOf(item.Model.Content);
        if (item is null ||
            source?.Mode != AssetMode.EmbeddedCopy ||
            item.Kind is not (ItemKind.Image or ItemKind.File))
        {
            return false;
        }

        var fullPath = System.IO.Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
        {
            return false;
        }

        var info = new FileInfo(fullPath);
        var nextSource = source with
        {
            AbsolutePath = fullPath,
            RelativePath = PathResolver.CreateRelativePath(_path, fullPath),
            Mode = AssetMode.ExternalReference,
            EmbeddedAssetId = null,
            OriginalFileName = info.Name,
            Size = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc
        };
        var oldContent = item.Model.Content;
        var nextContent = ReplaceSource(oldContent, nextSource);
        _history.Execute(
            new DelegateUndoableCommand(
                "Relink embedded asset",
                () => item.ReplaceContent(nextContent),
                () => item.ReplaceContent(oldContent),
                EmbeddedAssetIdsOf(oldContent)));
        item.IsMissing = false;
        MarkDirty();
        RaiseHistoryState();
        return true;
    }

    public void RelinkSelected(string sourcePath)
    {
        EnsureWritable();
        var item = SelectedItem;
        var source = item is null ? null : SourceOf(item.Model.Content);
        if (item is null || source is null || source.Mode != AssetMode.ExternalReference)
        {
            return;
        }

        var fullPath = System.IO.Path.GetFullPath(sourcePath);
        var info = File.Exists(fullPath) ? new FileInfo(fullPath) : null;
        var nextSource = source with
        {
            AbsolutePath = fullPath,
            RelativePath = PathResolver.CreateRelativePath(_path, fullPath),
            OriginalFileName = System.IO.Path.GetFileName(fullPath),
            Size = info?.Length,
            ModifiedUtc = info?.LastWriteTimeUtc
        };
        var oldContent = item.Model.Content;
        var nextContent = ReplaceSource(oldContent, nextSource);
        _history.Execute(
            new DelegateUndoableCommand(
                "Relink source",
                () => item.ReplaceContent(nextContent),
                () => item.ReplaceContent(oldContent)));
        item.IsMissing = false;
        MarkDirty();
        RaiseHistoryState();
    }

    public async Task SaveAsAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (!RefreshBackingFileState() && Items.Any(item =>
                SourceOf(item.Model.Content)?.Mode == AssetMode.EmbeddedCopy))
        {
            SaveError = _localization["WorkspaceFileMissingEmbedded"];
            throw new InvalidOperationException(SaveError);
        }
        var previousPath = _path;
        var wasRecovery = IsRecovery;
        var destinationFullPath = System.IO.Path.GetFullPath(destinationPath);
        UpdateRelativePaths(destinationFullPath);
        var snapshot = BuildSnapshot();
        SaveState = WorkspaceSaveState.Saving;
        await _store.SaveAsAsync(_path, destinationFullPath, snapshot, cancellationToken).ConfigureAwait(true);
        var nextFileLease = _store.AcquireFileLease(destinationFullPath);
        var previousFileLease = _fileLease;
        _fileLease = nextFileLease;
        _path = destinationFullPath;
        IsRecovery = false;
        IsBackingFileMissing = false;
        _savedVersion = _changeVersion;
        _requiresFullSave = false;
        SaveState = WorkspaceSaveState.Saved;
        SaveError = null;
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(IsRecovery));
        OnPropertyChanged(nameof(IsBackingFileMissing));
        OnPropertyChanged(nameof(DisplayTitle));
        await RefreshStorageInfoAsync(cancellationToken).ConfigureAwait(true);
        previousFileLease.Dispose();
        if (wasRecovery && !string.Equals(previousPath, destinationFullPath, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteRecoveryFile(previousPath);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        _saveTimer.Stop();
        if (!RefreshBackingFileState() || !IsDirty || IsReadOnly)
        {
            return;
        }
        await SaveNowAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task RefreshStorageInfoAsync(CancellationToken cancellationToken = default)
    {
        if (IsBackingFileMissing || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var info = await _store.AnalyzeCompactionAsync(
                    _path,
                    cancellationToken,
                    _history.ProtectedAssetIds)
                .ConfigureAwait(true);
            _workspaceFileSize = info.FileSize;
            _estimatedReclaimableBytes = info.EstimatedReclaimableBytes;
            _unreferencedAssetCount = info.UnreferencedAssetCount;
            RaiseStorageStateChanged();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException or FormatException)
        {
            _workspaceFileSize = TryGetWorkspaceFileSize();
            RaiseStorageStateChanged();
            _logger.Error($"Could not analyze workspace storage {_path}.", exception);
        }
    }

    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCompact)
        {
            return;
        }

        SetCompactionNotification(CompactionNotificationState.Running);
        IsCompacting = true;
        try
        {
            await FlushAsync(cancellationToken).ConfigureAwait(true);
            if (IsBackingFileMissing || SaveState == WorkspaceSaveState.Failed)
            {
                _compactionFailureDetail = SaveError ?? _localization["SaveFailed"];
                SetCompactionNotification(CompactionNotificationState.Failed);
                return;
            }

            _lastCompactionResult = await _store.CompactAsync(
                    _path,
                    cancellationToken,
                    _history.ProtectedAssetIds)
                .ConfigureAwait(true);
            _workspaceFileSize = _lastCompactionResult.SizeAfter;
            _estimatedReclaimableBytes = 0;
            _unreferencedAssetCount = 0;
            RaiseStorageStateChanged();
            SetCompactionNotification(CompactionNotificationState.Completed);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                Microsoft.Data.Sqlite.SqliteException)
        {
            _compactionFailureDetail = exception.Message;
            RefreshWorkspaceFileSize();
            SetCompactionNotification(CompactionNotificationState.Failed);
            _logger.Error($"Could not compact workspace {_path}.", exception);
        }
        finally
        {
            IsCompacting = false;
        }
    }

    public void DismissCompactionNotification() => IsCompactionNotificationVisible = false;

    public void Focus(BoardItemViewModel item)
    {
        SelectOnly(item);
        FocusItemRequested?.Invoke(this, item);
    }

    public void RefreshMissingSources()
    {
        foreach (var item in Items)
        {
            var source = SourceOf(item.Model.Content);
            item.IsMissing = source?.Mode == AssetMode.ExternalReference &&
                             PathResolver.Resolve(_path, source) is null;
        }
        VisualInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public bool RefreshBackingFileState()
    {
        if (!IsBackingFileMissing && _fileLease.IsCurrent)
        {
            return true;
        }

        IsBackingFileMissing = true;
        _saveTimer.Stop();
        SaveState = WorkspaceSaveState.Failed;
        SaveError = _localization["WorkspaceFileMissing"];
        OnPropertyChanged(nameof(IsBackingFileMissing));
        RaiseStorageStateChanged();
        return false;
    }

    public async Task<bool> TryReconnectBackingFileAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBackingFileMissing)
        {
            return true;
        }

        try
        {
            var nextFileLease = _store.AcquireFileLease(_path);
            WorkspaceOpenResult opened;
            try
            {
                opened = await _store.OpenAsync(_path, cancellationToken).ConfigureAwait(true);
            }
            catch
            {
                nextFileLease.Dispose();
                throw;
            }
            if (opened.Document.Id != Document.Id || opened.Mode != OpenMode)
            {
                nextFileLease.Dispose();
                SaveError = _localization["WorkspaceFileReplaced"];
                return false;
            }

            var previousFileLease = _fileLease;
            _fileLease = nextFileLease;
            previousFileLease.Dispose();
            IsBackingFileMissing = false;
            SaveError = null;
            SaveState = IsReadOnly
                ? WorkspaceSaveState.ReadOnly
                : IsDirty
                    ? WorkspaceSaveState.Unsaved
                    : WorkspaceSaveState.Saved;
            OnPropertyChanged(nameof(IsBackingFileMissing));
            RaiseStorageStateChanged();
            await RefreshStorageInfoAsync(cancellationToken).ConfigureAwait(true);
            if (IsDirty && !IsReadOnly)
            {
                RestartSaveTimer();
            }
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                Microsoft.Data.Sqlite.SqliteException or FormatException)
        {
            SaveError = _localization["WorkspaceFileMissing"];
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _saveTimer.Stop();
        _lifetime.Cancel();
        foreach (var item in Items)
        {
            Unsubscribe(item);
        }
        _localization.PropertyChanged -= OnLocalizationChanged;
        _saveGate.Dispose();
        _lifetime.Dispose();
        _fileLease.Dispose();
    }

    private void AddModelsWithUndo(IReadOnlyCollection<BoardItem> models, string description)
    {
        if (models.Count == 0)
        {
            return;
        }
        var snapshots = models.Select(model => model.DeepClone()).ToList();
        var ids = snapshots.Select(model => model.Id).ToHashSet();
        var protectedAssetIds = EmbeddedAssetIdsOf(snapshots);

        void Execute()
        {
            foreach (var model in snapshots)
            {
                if (Items.All(item => item.Id != model.Id))
                {
                    AddViewModel(model.DeepClone());
                }
            }
            SelectIds(ids);
            MarkDirty();
            RaiseCollectionState();
        }

        void Undo()
        {
            foreach (var item in Items.Where(item => ids.Contains(item.Id)).ToList())
            {
                RemoveViewModel(item);
            }
            MarkDirty();
            RaiseCollectionState();
        }

        _history.Execute(new DelegateUndoableCommand(
            description,
            Execute,
            Undo,
            protectedAssetIds));
        RaiseHistoryState();
    }

    private void AddViewModel(BoardItem model)
    {
        var viewModel = new BoardItemViewModel(model);
        Subscribe(viewModel);
        Items.Add(viewModel);
    }

    private void InsertViewModel(int index, BoardItem model)
    {
        var viewModel = new BoardItemViewModel(model);
        Subscribe(viewModel);
        Items.Insert(index, viewModel);
    }

    private void RemoveViewModel(BoardItemViewModel item)
    {
        Unsubscribe(item);
        Items.Remove(item);
    }

    private void Subscribe(BoardItemViewModel item)
    {
        item.ModelChanged += OnItemModelChanged;
        item.VisualChanged += OnItemVisualChanged;
    }

    private void Unsubscribe(BoardItemViewModel item)
    {
        item.ModelChanged -= OnItemModelChanged;
        item.VisualChanged -= OnItemVisualChanged;
    }

    private void OnItemModelChanged(object? sender, EventArgs eventArgs)
    {
        MarkDirty();
        UpdateSearch();
        OnPropertyChanged(nameof(DisplayTitle));
    }

    private void OnItemVisualChanged(object? sender, EventArgs eventArgs) =>
        VisualInvalidated?.Invoke(this, EventArgs.Empty);

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        OnPropertyChanged(nameof(SaveStatusText));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(StorageSummaryText));
        OnPropertyChanged(nameof(StorageToolTip));
        OnPropertyChanged(nameof(CompactionButtonToolTip));
        OnPropertyChanged(nameof(CompactionNotificationTitle));
        OnPropertyChanged(nameof(CompactionNotificationMessage));
        if (IsBackingFileMissing)
        {
            SaveError = _localization["WorkspaceFileMissing"];
        }
    }

    private long TryGetWorkspaceFileSize()
    {
        try
        {
            return File.Exists(_path) ? new FileInfo(_path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void RefreshWorkspaceFileSize()
    {
        _workspaceFileSize = TryGetWorkspaceFileSize();
        RaiseStorageStateChanged();
    }

    private void RaiseStorageStateChanged()
    {
        OnPropertyChanged(nameof(StorageSummaryText));
        OnPropertyChanged(nameof(StorageToolTip));
        OnPropertyChanged(nameof(CompactionButtonToolTip));
        OnPropertyChanged(nameof(IsCompactionRecommended));
        OnPropertyChanged(nameof(CanCompact));
    }

    private void SetCompactionNotification(CompactionNotificationState state)
    {
        _compactionNotificationState = state;
        if (state != CompactionNotificationState.Failed)
        {
            _compactionFailureDetail = null;
        }
        IsCompactionNotificationVisible = true;
        OnPropertyChanged(nameof(CompactionNotificationTitle));
        OnPropertyChanged(nameof(CompactionNotificationMessage));
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        var displayValue = (double)value;
        while (displayValue >= 1024 && unitIndex < units.Length - 1)
        {
            displayValue /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value} {units[unitIndex]}"
            : $"{displayValue:0.#} {units[unitIndex]}";
    }

    private void SelectIds(IReadOnlySet<Guid> ids)
    {
        foreach (var item in Items)
        {
            item.IsSelected = ids.Contains(item.Id);
        }
        RaiseSelectionChanged();
    }

    private void ApplyLayout(IReadOnlyDictionary<Guid, ItemLayoutState> layout)
    {
        foreach (var item in Items)
        {
            if (layout.TryGetValue(item.Id, out var state))
            {
                item.UpdateBounds(state.Bounds);
                item.UpdateParentFrame(state.ParentFrameId);
            }
        }
        MarkDirty();
        VisualInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void AssignFramesForSelected()
    {
        var frames = Items
            .Where(item => item.Kind == ItemKind.Frame && !item.IsSelected)
            .OrderByDescending(item => item.Model.ZIndex)
            .ToList();
        foreach (var item in SelectedItems.Where(item => item.Kind != ItemKind.Frame))
        {
            var frame = frames.FirstOrDefault(candidate => candidate.Bounds.Contains(item.Bounds.Center));
            item.UpdateParentFrame(frame?.Id);
        }
    }

    private List<BoardItemViewModel> TopLevelSelection()
    {
        var selected = SelectedItems.ToList();
        var selectedFrames = selected
            .Where(item => item.Kind == ItemKind.Frame)
            .Select(item => item.Id)
            .ToHashSet();
        return selected
            .Where(item => !item.ParentFrameId.HasValue || !selectedFrames.Contains(item.ParentFrameId.Value))
            .ToList();
    }

    private void TransformAnchors(
        IReadOnlyCollection<BoardItemViewModel> anchors,
        Func<BoardItemViewModel, WorldPoint> offsetFor,
        string description)
    {
        var movable = GetMovableSelection();
        var before = CaptureLayout(movable);
        var selectedFrames = anchors
            .Where(item => item.Kind == ItemKind.Frame)
            .Select(item => item.Id)
            .ToHashSet();

        BeginInteraction();
        foreach (var anchor in anchors)
        {
            var offset = offsetFor(anchor);
            if (Math.Abs(offset.X) < 0.0001 && Math.Abs(offset.Y) < 0.0001)
            {
                continue;
            }

            anchor.UpdateBounds(anchor.Bounds.Translate(offset.X, offset.Y));
            if (anchor.Kind == ItemKind.Frame)
            {
                foreach (var child in Items.Where(item =>
                             item.ParentFrameId == anchor.Id &&
                             !selectedFrames.Contains(item.Id)))
                {
                    child.UpdateBounds(child.Bounds.Translate(offset.X, offset.Y));
                }
            }
        }
        EndInteraction(before, description, assignFrames: true);
    }

    private void ApplyZIndices(IReadOnlyDictionary<Guid, int> zIndices)
    {
        foreach (var item in Items)
        {
            if (zIndices.TryGetValue(item.Id, out var zIndex))
            {
                item.UpdateZIndex(zIndex);
            }
        }
        VisualInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void NormalizeZIndices()
    {
        var normalized = Items
            .OrderBy(item => item.Model.ZIndex)
            .ThenBy(item => item.Model.CreatedUtc)
            .Select((item, index) => (item.Id, ZIndex: index))
            .ToDictionary(entry => entry.Id, entry => entry.ZIndex);
        ApplyZIndices(normalized);
    }

    private static bool LayoutsEqual(
        IReadOnlyDictionary<Guid, ItemLayoutState> first,
        IReadOnlyDictionary<Guid, ItemLayoutState> second) =>
        first.Count == second.Count &&
        first.All(pair => second.TryGetValue(pair.Key, out var value) && pair.Value == value);

    private void MarkDirty(bool requiresFullSave = true)
    {
        if (IsReadOnly)
        {
            return;
        }
        _changeVersion++;
        _requiresFullSave |= requiresFullSave;
        SaveState = IsBackingFileMissing
            ? WorkspaceSaveState.Failed
            : WorkspaceSaveState.Unsaved;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DisplayTitle));
        if (_interactionDepth == 0 && !IsBackingFileMissing)
        {
            RestartSaveTimer();
        }
    }

    private void RestartSaveTimer()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void OnSaveTimer(object? sender, EventArgs eventArgs)
    {
        _saveTimer.Stop();
        try
        {
            await SaveNowAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SaveNowAsync(CancellationToken cancellationToken)
    {
        if (IsReadOnly || !IsDirty || !RefreshBackingFileState())
        {
            return;
        }

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!IsDirty)
            {
                return;
            }

            var version = _changeVersion;
            var requiresFullSave = _requiresFullSave;
            if (requiresFullSave)
            {
                _requiresFullSave = false;
            }
            SaveState = WorkspaceSaveState.Saving;
            SaveError = null;
            try
            {
                if (requiresFullSave)
                {
                    var snapshot = BuildSnapshot();
                    await _store.SaveAsync(_path, snapshot, cancellationToken).ConfigureAwait(true);
                }
                else
                {
                    Document.LastAccessedUtc = DateTimeOffset.UtcNow;
                    await _store.SaveViewportAsync(
                            _path,
                            Document.ViewportOrigin,
                            Document.Zoom,
                            Document.LastAccessedUtc,
                            cancellationToken)
                        .ConfigureAwait(true);
                }
                if (_changeVersion == version)
                {
                    _savedVersion = version;
                    SaveState = WorkspaceSaveState.Saved;
                    OnPropertyChanged(nameof(IsDirty));
                    OnPropertyChanged(nameof(DisplayTitle));
                }
                else
                {
                    SaveState = WorkspaceSaveState.Unsaved;
                    RestartSaveTimer();
                }
                if (requiresFullSave)
                {
                    await RefreshStorageInfoAsync(cancellationToken).ConfigureAwait(true);
                }
                else
                {
                    RefreshWorkspaceFileSize();
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or
                    Microsoft.Data.Sqlite.SqliteException)
            {
                _requiresFullSave |= requiresFullSave;
                SaveState = WorkspaceSaveState.Failed;
                SaveError = exception.Message;
                _logger.Error($"Could not save workspace {_path}.", exception);
            }
            catch
            {
                _requiresFullSave |= requiresFullSave;
                throw;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private WorkspaceDocument BuildSnapshot()
    {
        Document.Items = Items.Select(item => item.Model.DeepClone()).ToList();
        Document.LastAccessedUtc = DateTimeOffset.UtcNow;
        return Document.DeepClone();
    }

    private void RecordAccess(BoardItemViewModel item)
    {
        if (!IsReadOnly)
        {
            item.RecordAccess(DateTimeOffset.UtcNow);
        }
    }

    private void UpdateSearch()
    {
        SearchResults.Clear();
        var results = WorkspaceSearch.SearchWithScores(
            Items.Select(item => item.Model),
            SearchQuery);
        IEnumerable<WorkspaceSearchResult> orderedResults = SearchSortMode switch
        {
            SearchSortMode.LastAccessedUtc => results.OrderByDescending(result => result.Item.LastAccessedUtc),
            SearchSortMode.Relevance => results.OrderByDescending(result => result.Score),
            _ => results
        };

        foreach (var result in orderedResults)
        {
            var viewModel = Items.FirstOrDefault(item => item.Id == result.Item.Id);
            if (viewModel is not null)
            {
                SearchResults.Add(viewModel);
            }
        }
    }

    private void RaiseCollectionState()
    {
        UpdateSearch();
        ItemsChanged?.Invoke(this, EventArgs.Empty);
        VisualInvalidated?.Invoke(this, EventArgs.Empty);
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(SelectedItems));
        OnPropertyChanged(nameof(CanAlignSelection));
        OnPropertyChanged(nameof(CanDistributeSelection));
        OnPropertyChanged(nameof(CanMoveSelectionLayer));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        VisualInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseHistoryState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private void UpdateRelativePaths(string destinationPath)
    {
        foreach (var item in Items)
        {
            var source = SourceOf(item.Model.Content);
            if (source?.Mode != AssetMode.ExternalReference || source.AbsolutePath is null)
            {
                continue;
            }
            item.ReplaceContent(ReplaceSource(
                item.Model.Content,
                source with { RelativePath = PathResolver.CreateRelativePath(destinationPath, source.AbsolutePath) }));
        }
    }

    private static SourceDescriptor? SourceOf(ItemContent content) => content switch
    {
        ImageContent image => image.Source,
        FileContent file => file.Source,
        FolderContent folder => folder.Source,
        _ => null
    };

    private static IReadOnlySet<Guid> EmbeddedAssetIdsOf(ItemContent content)
    {
        var assetId = SourceOf(content)?.EmbeddedAssetId;
        return assetId.HasValue ? new HashSet<Guid> { assetId.Value } : new HashSet<Guid>();
    }

    private static IReadOnlySet<Guid> EmbeddedAssetIdsOf(IEnumerable<BoardItem> items) =>
        items.Select(item => SourceOf(item.Content)?.EmbeddedAssetId)
            .Where(assetId => assetId.HasValue)
            .Select(assetId => assetId!.Value)
            .ToHashSet();

    private static ItemContent ReplaceSource(ItemContent content, SourceDescriptor source) => content switch
    {
        ImageContent image => image with { Source = source },
        FileContent file => file with { Source = source },
        FolderContent folder => folder with { Source = source },
        _ => content
    };

    private static string SanitizeFileName(string fileName)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = fileName.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(chars);
    }

    private static void TryDeleteRecoveryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The workspace is read-only.");
        }
    }
}
