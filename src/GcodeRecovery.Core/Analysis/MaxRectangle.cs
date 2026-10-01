namespace GcodeRecovery.Core.Analysis;

/// <summary>
/// Largest axis-aligned rectangle of filled cells, using the classic "largest rectangle in a histogram"
/// sweep (O(rows × cols)). Every maximal rectangle is visited, so a minimum side constraint can be applied
/// without losing the optimum: if any rectangle satisfies it, the maximal rectangle containing it does too.
/// This keeps the result from degenerating into a long thin wall that a nozzle could slip off.
/// </summary>
public static class MaxRectangle
{
    public readonly record struct CellRect(int Col, int Row, int Cols, int Rows)
    {
        public int Area => Cols * Rows;
    }

    public static CellRect? Find(OccupancyGrid grid, int minSideCells)
    {
        minSideCells = Math.Max(1, minSideCells);
        var heights = new int[grid.Cols];
        var stack = new int[grid.Cols + 1];
        CellRect? best = null;

        for (var row = 0; row < grid.Rows; row++)
        {
            for (var c = 0; c < grid.Cols; c++)
                heights[c] = grid[c, row] ? heights[c] + 1 : 0;

            var top = 0;
            for (var c = 0; c <= grid.Cols; c++)
            {
                var h = c == grid.Cols ? 0 : heights[c];
                while (top > 0 && heights[stack[top - 1]] >= h)
                {
                    var height = heights[stack[--top]];
                    var left = top == 0 ? 0 : stack[top - 1] + 1;
                    var width = c - left;
                    if (height >= minSideCells && width >= minSideCells)
                    {
                        var candidate = new CellRect(left, row - height + 1, width, height);
                        if (best is null || candidate.Area > best.Value.Area ||
                            (candidate.Area == best.Value.Area && Math.Min(width, height) > Math.Min(best.Value.Cols, best.Value.Rows)))
                            best = candidate;
                    }
                }
                stack[top++] = c;
            }
        }
        return best;
    }
}
