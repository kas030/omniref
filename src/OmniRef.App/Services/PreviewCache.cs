using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OmniRef.Core.Interfaces;
using OmniRef.Core.Models;
using OmniRef.Core.Services;
using OmniRef.Infrastructure.Windows.Diagnostics;

namespace OmniRef.App.Services;

public sealed class PreviewCache : IDisposable
{
    private const long MaximumMemoryBytes = 96L * 1024 * 1024;
    private const long MaximumDiskBytes = 512L * 1024 * 1024;

    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = [];
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _pending = new(StringComparer.Ordinal);
    private readonly IWorkspaceStore _workspaceStore;
    private readonly IThumbnailProvider _thumbnailProvider;
    private readonly RollingFileLogger _logger;
    private readonly string _diskDirectory;
    private long _memoryBytes;
    private bool _disposed;

    public PreviewCache(
        IWorkspaceStore workspaceStore,
        IThumbnailProvider thumbnailProvider,
        RollingFileLogger logger,
        string diskDirectory)
    {
        _workspaceStore = workspaceStore;
        _thumbnailProvider = thumbnailProvider;
        _logger = logger;
        _diskDirectory = Path.Combine(diskDirectory, "Thumbnails");
        _ = Task.Run(PruneDiskCache);
    }

    public Task<ImageSource?> GetAsync(
        string workspacePath,
        BoardItem item,
        int requestedPixels,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bucket = PixelBucket(requestedPixels);
        var key = BuildKey(workspacePath, item, bucket);
        if (key is null)
        {
            return Task.FromResult<ImageSource?>(null);
        }

        lock (_sync)
        {
            if (_memory.TryGetValue(key, out var entry))
            {
                Touch(entry);
                return Task.FromResult<ImageSource?>(entry.Image);
            }
        }

        return _pending.GetOrAdd(
            key,
            _ => LoadAndCacheAsync(key, workspacePath, item.DeepClone(), bucket, cancellationToken));
    }

    public void TrimAggressively()
    {
        lock (_sync)
        {
            _memory.Clear();
            _lru.Clear();
            _memoryBytes = 0;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        TrimAggressively();
        _pending.Clear();
    }

    private async Task<ImageSource?> LoadAndCacheAsync(
        string key,
        string workspacePath,
        BoardItem item,
        int bucket,
        CancellationToken cancellationToken)
    {
        try
        {
            var diskPath = DiskPath(key);
            ImageSource? image;
            if (File.Exists(diskPath))
            {
                image = await Task.Run(
                        () => DecodeFile(diskPath, bucket),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                image = await LoadPreviewImageAsync(
                        workspacePath,
                        item,
                        bucket,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (image is not null)
                {
                    TryWriteDiskCache(diskPath, image);
                }
            }

            if (image is BitmapSource bitmap)
            {
                AddMemory(key, image, Math.Max(1, bitmap.PixelWidth) * (long)Math.Max(1, bitmap.PixelHeight) * 4);
            }

            return image;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                System.Runtime.InteropServices.COMException or KeyNotFoundException)
        {
            _logger.Warning($"Preview failed for item {item.Id}: {exception.Message}");
            return null;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private async Task<ImageSource?> LoadPreviewImageAsync(
        string workspacePath,
        BoardItem item,
        int bucket,
        CancellationToken cancellationToken)
    {
        var source = item.Content switch
        {
            ImageContent image => image.Source,
            FileContent file => file.Source,
            FolderContent folder => folder.Source,
            _ => null
        };
        if (source is null)
        {
            return null;
        }

        if (source.Mode == AssetMode.EmbeddedCopy && source.EmbeddedAssetId.HasValue)
        {
            Directory.CreateDirectory(_diskDirectory);
            var extension = SafeExtension(source.OriginalFileName);
            var temporaryPath = Path.Combine(
                _diskDirectory,
                $".embedded-{Guid.NewGuid():N}{extension}");
            try
            {
                await _workspaceStore.ExportEmbeddedAssetAsync(
                        workspacePath,
                        source.EmbeddedAssetId.Value,
                        temporaryPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                return item.Kind == ItemKind.Image
                    ? await Task.Run(
                            () => DecodeFile(temporaryPath, bucket),
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await LoadShellThumbnailAsync(temporaryPath, bucket, cancellationToken)
                        .ConfigureAwait(false);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        var resolved = PathResolver.Resolve(workspacePath, source);
        if (resolved is null)
        {
            return null;
        }

        if (item.Kind == ItemKind.Image)
        {
            return await Task.Run(
                    () => DecodeFile(resolved, bucket),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await LoadShellThumbnailAsync(resolved, bucket, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImageSource?> LoadShellThumbnailAsync(
        string path,
        int bucket,
        CancellationToken cancellationToken)
    {
        var thumbnail = await _thumbnailProvider.GetThumbnailAsync(
                path,
                bucket,
                cancellationToken)
            .ConfigureAwait(false);
        return thumbnail is null
            ? null
            : await Task.Run(
                    () => DecodeBytes(thumbnail.PngBytes, bucket),
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private static BitmapSource? DecodeFile(string path, int requestedPixels)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan);
        return DecodeStream(stream, requestedPixels);
    }

    private static BitmapSource? DecodeBytes(byte[] bytes, int requestedPixels)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return DecodeStream(stream, requestedPixels);
    }

    private static BitmapSource? DecodeStream(Stream stream, int requestedPixels)
    {
        if (!stream.CanSeek)
        {
            throw new NotSupportedException("Preview image streams must be seekable.");
        }

        var initialPosition = stream.Position;
        var (width, height) = ReadPixelSize(stream);
        stream.Position = initialPosition;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream;
        if (Math.Max(width, height) > requestedPixels)
        {
            if (width >= height)
            {
                image.DecodePixelWidth = requestedPixels;
            }
            else
            {
                image.DecodePixelHeight = requestedPixels;
            }
        }
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static (int Width, int Height) ReadPixelSize(Stream stream)
    {
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
        {
            throw new NotSupportedException("The image does not contain a decodable frame.");
        }
        return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
    }

    private void AddMemory(string key, ImageSource image, long estimatedBytes)
    {
        lock (_sync)
        {
            if (_memory.Remove(key, out var previous))
            {
                _memoryBytes -= previous.EstimatedBytes;
                _lru.Remove(previous.Node);
            }

            var node = _lru.AddFirst(key);
            _memory[key] = new(image, estimatedBytes, node);
            _memoryBytes += estimatedBytes;

            while (_memoryBytes > MaximumMemoryBytes && _lru.Last is { } oldest)
            {
                if (_memory.Remove(oldest.Value, out var removed))
                {
                    _memoryBytes -= removed.EstimatedBytes;
                }
                _lru.RemoveLast();
            }
        }
    }

    private void Touch(CacheEntry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private string? BuildKey(string workspacePath, BoardItem item, int bucket)
    {
        var source = item.Content switch
        {
            ImageContent image => image.Source,
            FileContent file => file.Source,
            FolderContent folder => folder.Source,
            _ => null
        };
        if (source is null)
        {
            return null;
        }

        if (source.Mode == AssetMode.EmbeddedCopy && source.EmbeddedAssetId.HasValue)
        {
            return $"embedded|{Path.GetFullPath(workspacePath)}|{source.EmbeddedAssetId:D}|{bucket}";
        }

        var resolved = PathResolver.Resolve(workspacePath, source);
        if (resolved is null)
        {
            return $"missing|{source.AbsolutePath}|{bucket}";
        }

        DateTime modified;
        try
        {
            modified = File.Exists(resolved)
                ? File.GetLastWriteTimeUtc(resolved)
                : Directory.GetLastWriteTimeUtc(resolved);
        }
        catch (UnauthorizedAccessException)
        {
            modified = DateTime.MinValue;
        }

        return $"{item.Kind}|{resolved}|{modified.Ticks}|{bucket}";
    }

    private string DiskPath(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_diskDirectory, Convert.ToHexString(hash) + ".png");
    }

    private static int PixelBucket(int requestedPixels)
    {
        var clamped = Math.Clamp(requestedPixels, 64, 2048);
        var bucket = 64;
        while (bucket < clamped)
        {
            bucket *= 2;
        }
        return bucket;
    }

    private void TryWriteDiskCache(string path, ImageSource image)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (image is not BitmapSource bitmap)
            {
                return;
            }

            Directory.CreateDirectory(_diskDirectory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warning($"Could not write thumbnail cache: {exception.Message}");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private void PruneDiskCache()
    {
        try
        {
            if (!Directory.Exists(_diskDirectory))
            {
                return;
            }

            var files = new DirectoryInfo(_diskDirectory)
                .EnumerateFiles("*.png")
                .OrderByDescending(file => file.LastAccessTimeUtc)
                .ToList();
            var total = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (total <= MaximumDiskBytes)
                {
                    break;
                }

                total -= file.Length;
                file.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warning($"Could not prune thumbnail cache: {exception.Message}");
        }
    }

    private sealed record CacheEntry(
        ImageSource Image,
        long EstimatedBytes,
        LinkedListNode<string> Node);

    private static string SafeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length is > 0 and <= 16 &&
               extension.All(character => !Path.GetInvalidFileNameChars().Contains(character))
            ? extension
            : ".tmp";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
