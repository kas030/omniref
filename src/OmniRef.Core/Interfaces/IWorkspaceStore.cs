using OmniRef.Core.Models;

namespace OmniRef.Core.Interfaces;

public interface IWorkspaceStore
{
    IWorkspaceFileLease AcquireFileLease(string path);
    Task<WorkspaceOpenResult> OpenAsync(string path, CancellationToken cancellationToken = default);
    Task SaveAsync(string path, WorkspaceDocument document, CancellationToken cancellationToken = default);
    Task SaveViewportAsync(
        string path,
        WorldPoint origin,
        double zoom,
        DateTimeOffset lastAccessedUtc,
        CancellationToken cancellationToken = default);
    Task SaveAsAsync(string sourcePath, string destinationPath, WorkspaceDocument document, CancellationToken cancellationToken = default);
    Task<EmbeddedAssetInfo> ImportEmbeddedAssetAsync(
        string workspacePath,
        string sourcePath,
        CancellationToken cancellationToken = default);
    Task<EmbeddedAssetInfo> ImportEmbeddedAssetAsync(
        string workspacePath,
        Stream source,
        string fileName,
        string? mediaType,
        CancellationToken cancellationToken = default);
    Task ExportEmbeddedAssetAsync(
        string workspacePath,
        Guid assetId,
        string destinationPath,
        CancellationToken cancellationToken = default);
    Task<byte[]> ReadEmbeddedAssetAsync(
        string workspacePath,
        Guid assetId,
        CancellationToken cancellationToken = default);
    Task<WorkspaceCompactionInfo> AnalyzeCompactionAsync(
        string workspacePath,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? protectedAssetIds = null);
    Task<WorkspaceCompactionResult> CompactAsync(
        string workspacePath,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? protectedAssetIds = null);
}

public interface IWorkspaceFileLease : IDisposable
{
    string Path { get; }
    bool IsCurrent { get; }
}
