using OmniRef.Core.Models;

namespace OmniRef.Core.Services;

public static class ViewportMath
{
    public const double MinimumZoom = 0.1;
    public const double MaximumZoom = 8;

    public static WorldPoint ScreenToWorld(WorldPoint screen, WorldPoint origin, double zoom) =>
        new((screen.X / zoom) + origin.X, (screen.Y / zoom) + origin.Y);

    public static WorldPoint WorldToScreen(WorldPoint world, WorldPoint origin, double zoom) =>
        new((world.X - origin.X) * zoom, (world.Y - origin.Y) * zoom);

    public static double ZoomForMinimumScreenExtent(
        double currentZoom,
        double worldExtent,
        double minimumScreenExtent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(worldExtent, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minimumScreenExtent, 0);

        return Math.Clamp(
            Math.Max(currentZoom, minimumScreenExtent / worldExtent),
            MinimumZoom,
            MaximumZoom);
    }

    public static (WorldPoint Origin, double Zoom) ZoomAt(
        WorldPoint screenAnchor,
        WorldPoint origin,
        double currentZoom,
        double requestedZoom)
    {
        var nextZoom = Math.Clamp(requestedZoom, MinimumZoom, MaximumZoom);
        var worldAnchor = ScreenToWorld(screenAnchor, origin, currentZoom);
        var nextOrigin = new WorldPoint(
            worldAnchor.X - (screenAnchor.X / nextZoom),
            worldAnchor.Y - (screenAnchor.Y / nextZoom));
        return (nextOrigin, nextZoom);
    }
}
