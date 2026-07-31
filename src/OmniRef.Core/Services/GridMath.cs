using OmniRef.Core.Models;

namespace OmniRef.Core.Services;

public static class GridMath
{
    public const double SnapStep = 8;
    public const double MajorStep = 32;
    public const double TargetScreenStep = 32;

    public static double GetVisualStep(double zoom)
    {
        if (!double.IsFinite(zoom) || zoom <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom));
        }

        var exponent = Math.Round(Math.Log2(TargetScreenStep / (MajorStep * zoom)));
        return MajorStep * Math.Pow(2, exponent);
    }

    public static double Snap(double value, double step = SnapStep)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (!double.IsFinite(step) || step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step));
        }

        return Math.Round(value / step, MidpointRounding.AwayFromZero) * step;
    }

    public static WorldPoint SnapTranslation(
        WorldRect bounds,
        double deltaX,
        double deltaY,
        double step = SnapStep)
    {
        var snappedLeft = Snap(bounds.Left + deltaX, step);
        var snappedTop = Snap(bounds.Top + deltaY, step);
        return new WorldPoint(snappedLeft - bounds.Left, snappedTop - bounds.Top);
    }
}
