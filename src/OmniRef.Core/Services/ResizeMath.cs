using OmniRef.Core.Models;

namespace OmniRef.Core.Services;

public static class ResizeMath
{
    public static WorldSize ConstrainToAspectRatio(
        WorldSize initialSize,
        WorldSize requestedSize,
        WorldSize minimumSize)
    {
        ValidateSize(initialSize, nameof(initialSize), requirePositive: true);
        ValidateSize(requestedSize, nameof(requestedSize), requirePositive: false);
        ValidateSize(minimumSize, nameof(minimumSize), requirePositive: true);

        var lengthSquared =
            (initialSize.Width * initialSize.Width) +
            (initialSize.Height * initialSize.Height);
        var scale =
            ((requestedSize.Width * initialSize.Width) +
             (requestedSize.Height * initialSize.Height)) /
            lengthSquared;
        var minimumScale = Math.Max(
            minimumSize.Width / initialSize.Width,
            minimumSize.Height / initialSize.Height);
        scale = Math.Max(scale, minimumScale);

        return new WorldSize(
            initialSize.Width * scale,
            initialSize.Height * scale);
    }

    private static void ValidateSize(WorldSize size, string parameterName, bool requirePositive)
    {
        if (!double.IsFinite(size.Width) ||
            !double.IsFinite(size.Height) ||
            (requirePositive && (size.Width <= 0 || size.Height <= 0)))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
