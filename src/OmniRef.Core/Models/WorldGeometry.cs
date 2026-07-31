namespace OmniRef.Core.Models;

public readonly record struct WorldPoint(double X, double Y)
{
    public static WorldPoint operator +(WorldPoint point, WorldPoint delta) =>
        new(point.X + delta.X, point.Y + delta.Y);

    public static WorldPoint operator -(WorldPoint point, WorldPoint delta) =>
        new(point.X - delta.X, point.Y - delta.Y);
}

public readonly record struct WorldSize(double Width, double Height);

public readonly record struct WorldRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public WorldPoint Center => new(X + (Width / 2), Y + (Height / 2));

    public bool Contains(WorldPoint point) =>
        point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    public bool Intersects(WorldRect other) =>
        Left <= other.Right && Right >= other.Left && Top <= other.Bottom && Bottom >= other.Top;

    public WorldRect Translate(double x, double y) => new(X + x, Y + y, Width, Height);

    public WorldRect Inflate(double amount) =>
        new(X - amount, Y - amount, Width + (amount * 2), Height + (amount * 2));

    public static WorldRect FromPoints(WorldPoint first, WorldPoint second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        return new(left, top, Math.Abs(second.X - first.X), Math.Abs(second.Y - first.Y));
    }

    public static WorldRect Union(IEnumerable<WorldRect> rectangles)
    {
        using var enumerator = rectangles.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            throw new ArgumentException("At least one rectangle is required.", nameof(rectangles));
        }

        var left = enumerator.Current.Left;
        var top = enumerator.Current.Top;
        var right = enumerator.Current.Right;
        var bottom = enumerator.Current.Bottom;
        while (enumerator.MoveNext())
        {
            left = Math.Min(left, enumerator.Current.Left);
            top = Math.Min(top, enumerator.Current.Top);
            right = Math.Max(right, enumerator.Current.Right);
            bottom = Math.Max(bottom, enumerator.Current.Bottom);
        }
        return new(left, top, right - left, bottom - top);
    }
}
