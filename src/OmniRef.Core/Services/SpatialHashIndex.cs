using OmniRef.Core.Models;

namespace OmniRef.Core.Services;

public sealed class SpatialHashIndex<T> where T : notnull
{
    private readonly double _cellSize;
    private readonly Dictionary<(int X, int Y), HashSet<T>> _cells = [];
    private readonly Dictionary<T, WorldRect> _bounds = [];

    public SpatialHashIndex(double cellSize = 512)
    {
        if (cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        }

        _cellSize = cellSize;
    }

    public int Count => _bounds.Count;

    public void AddOrUpdate(T item, WorldRect bounds)
    {
        Remove(item);
        _bounds[item] = bounds;
        foreach (var cell in EnumerateCells(bounds))
        {
            if (!_cells.TryGetValue(cell, out var items))
            {
                items = [];
                _cells[cell] = items;
            }

            items.Add(item);
        }
    }

    public bool Remove(T item)
    {
        if (!_bounds.Remove(item, out var previousBounds))
        {
            return false;
        }

        foreach (var cell in EnumerateCells(previousBounds))
        {
            if (_cells.TryGetValue(cell, out var items))
            {
                items.Remove(item);
                if (items.Count == 0)
                {
                    _cells.Remove(cell);
                }
            }
        }

        return true;
    }

    public IReadOnlyList<T> Query(WorldRect area)
    {
        var candidates = new HashSet<T>();
        foreach (var cell in EnumerateCells(area))
        {
            if (_cells.TryGetValue(cell, out var items))
            {
                candidates.UnionWith(items);
            }
        }

        return candidates
            .Where(item => _bounds[item].Intersects(area))
            .ToList();
    }

    public void Clear()
    {
        _cells.Clear();
        _bounds.Clear();
    }

    private IEnumerable<(int X, int Y)> EnumerateCells(WorldRect bounds)
    {
        var minX = CellCoordinate(bounds.Left);
        var maxX = CellCoordinate(bounds.Right);
        var minY = CellCoordinate(bounds.Top);
        var maxY = CellCoordinate(bounds.Bottom);

        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                yield return (x, y);
            }
        }
    }

    private int CellCoordinate(double value) => (int)Math.Floor(value / _cellSize);
}
