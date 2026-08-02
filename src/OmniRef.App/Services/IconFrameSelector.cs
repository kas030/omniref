using System.Windows.Media.Imaging;

namespace OmniRef.App.Services;

public static class IconFrameSelector
{
    public static BitmapFrame SelectBestFrame(
        IReadOnlyList<BitmapFrame> frames,
        int targetPixelWidth)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one icon frame is required.", nameof(frames));
        }

        var bestWidth = SelectBestWidth(
            frames.Select(frame => frame.PixelWidth),
            targetPixelWidth);
        return frames.First(frame => frame.PixelWidth == bestWidth);
    }

    public static int SelectBestWidth(
        IEnumerable<int> availableWidths,
        int targetPixelWidth)
    {
        ArgumentNullException.ThrowIfNull(availableWidths);
        if (targetPixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetPixelWidth),
                "The target width must be positive.");
        }

        var found = false;
        var bestWidth = 0;
        var bestDistance = long.MaxValue;
        foreach (var width in availableWidths)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(availableWidths),
                    "Icon frame widths must be positive.");
            }

            var distance = Math.Abs((long)width - targetPixelWidth);
            if (distance < bestDistance ||
                (distance == bestDistance && width > bestWidth))
            {
                found = true;
                bestWidth = width;
                bestDistance = distance;
            }
        }

        if (!found)
        {
            throw new ArgumentException(
                "At least one icon frame width is required.",
                nameof(availableWidths));
        }

        return bestWidth;
    }
}
