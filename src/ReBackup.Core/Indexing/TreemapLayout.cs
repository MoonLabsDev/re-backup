namespace ReBackup.Core.Indexing;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
}

public readonly record struct TreemapTile<T>(T Item, TreemapRect Rect);

/// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk).</summary>
public static class TreemapLayout
{
    public static IReadOnlyList<TreemapTile<T>> Squarify<T>(IEnumerable<T> items, Func<T, double> weight,
        TreemapRect bounds)
    {
        var tiles = new List<TreemapTile<T>>();
        var weighted = items
            .Select(item => (Item: item, Weight: weight(item)))
            .Where(x => x.Weight > 0)
            .OrderByDescending(x => x.Weight)
            .ToList();
        if (weighted.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return tiles;

        var scale = bounds.Area / weighted.Sum(x => x.Weight);
        var row = new List<(T Item, double Area)>();
        var free = bounds;
        foreach (var (item, itemWeight) in weighted)
        {
            var area = itemWeight * scale;
            var side = Math.Min(free.Width, free.Height);
            if (row.Count > 0 && WorstRatio(row, area, side) > WorstRatio(row, 0, side))
            {
                free = PlaceRow(row, free, tiles);
                row.Clear();
            }
            row.Add((item, area));
        }
        PlaceRow(row, free, tiles);
        return tiles;
    }

    /// <summary>Worst aspect ratio in the row if <paramref name="extraArea"/> were added (0 = the row as it is).</summary>
    private static double WorstRatio<T>(List<(T Item, double Area)> row, double extraArea, double side)
    {
        var sum = extraArea;
        var max = extraArea;
        var min = extraArea > 0 ? extraArea : double.MaxValue;
        foreach (var (_, area) in row)
        {
            sum += area;
            max = Math.Max(max, area);
            min = Math.Min(min, area);
        }
        var side2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(side2 * max / sum2, sum2 / (side2 * min));
    }

    /// <summary>Places the row along the shorter side of the free rectangle and returns what is left.</summary>
    private static TreemapRect PlaceRow<T>(List<(T Item, double Area)> row, TreemapRect free,
        List<TreemapTile<T>> tiles)
    {
        var sum = row.Sum(r => r.Area);
        if (free.Width >= free.Height)
        {
            var width = free.Height > 0 ? Math.Min(sum / free.Height, free.Width) : 0;
            var y = free.Y;
            foreach (var (item, area) in row)
            {
                var height = width > 0 ? area / width : 0;
                tiles.Add(new TreemapTile<T>(item, new TreemapRect(free.X, y, width, height)));
                y += height;
            }
            return new TreemapRect(free.X + width, free.Y, Math.Max(0, free.Width - width), free.Height);
        }
        else
        {
            var height = free.Width > 0 ? Math.Min(sum / free.Width, free.Height) : 0;
            var x = free.X;
            foreach (var (item, area) in row)
            {
                var width = height > 0 ? area / height : 0;
                tiles.Add(new TreemapTile<T>(item, new TreemapRect(x, free.Y, width, height)));
                x += width;
            }
            return new TreemapRect(free.X, free.Y + height, free.Width, Math.Max(0, free.Height - height));
        }
    }
}
