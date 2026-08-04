namespace OmniRef.Core.Models;

public sealed record WorkspaceCompactionInfo(
    long FileSize,
    long EstimatedReclaimableBytes,
    int UnreferencedAssetCount);

public sealed record WorkspaceCompactionResult(
    long SizeBefore,
    long SizeAfter,
    int RemovedAssetCount)
{
    public long ReclaimedBytes => Math.Max(0, SizeBefore - SizeAfter);
}
