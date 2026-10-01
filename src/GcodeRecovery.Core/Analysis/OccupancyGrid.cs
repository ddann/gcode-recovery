using GcodeRecovery.Core.Gcode;

namespace GcodeRecovery.Core.Analysis;

public readonly record struct RectMm(double X0, double Y0, double X1, double Y1)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
    public double CenterX => (X0 + X1) / 2;
    public double CenterY => (Y0 + Y1) / 2;
    public double Area => Width * Height;
    public double MinSide => Math.Min(Width, Height);

    public RectMm Shrink(double margin) => new(X0 + margin, Y0 + margin, X1 - margin, Y1 - margin);

    public override string ToString() => $"{Width:0.0} × {Height:0.0} mm at ({CenterX:0.0}, {CenterY:0.0})";
}

/// <summary>
/// Raster of "is there extruded material here" for a layer, built by stamping every extrusion path with
/// its line width. Grids with the same geometry can be intersected to find areas solid in several layers.
/// </summary>
public sealed class OccupancyGrid
{
    private readonly bool[] _cells;

    public OccupancyGrid(double originX, double originY, int cols, int rows, double cellSize)
    {
        if (cols <= 0 || rows <= 0) throw new ArgumentOutOfRangeException(nameof(cols), "Grid must not be empty.");
        OriginX = originX;
        OriginY = originY;
        Cols = cols;
        Rows = rows;
        CellSize = cellSize;
        _cells = new bool[cols * rows];
    }

    public double OriginX { get; }
    public double OriginY { get; }
    public int Cols { get; }
    public int Rows { get; }
    public double CellSize { get; }

    public bool this[int col, int row]
    {
        get => _cells[row * Cols + col];
        set => _cells[row * Cols + col] = value;
    }

    public static OccupancyGrid ForBounds(double minX, double minY, double maxX, double maxY, double cellSize, double margin = 2)
    {
        var x0 = minX - margin;
        var y0 = minY - margin;
        var cols = Math.Max(1, (int)Math.Ceiling((maxX - minX + 2 * margin) / cellSize));
        var rows = Math.Max(1, (int)Math.Ceiling((maxY - minY + 2 * margin) / cellSize));
        return new OccupancyGrid(x0, y0, cols, rows, cellSize);
    }

    public OccupancyGrid CloneEmpty() => new(OriginX, OriginY, Cols, Rows, CellSize);

    public void Fill(bool value) => Array.Fill(_cells, value);

    /// <summary>Marks every cell whose centre lies within half the line width of the path.</summary>
    public void Stamp(Segment s)
    {
        var half = s.Width / 2.0 + CellSize * 0.25;
        var minC = Math.Max(0, (int)Math.Floor((Math.Min(s.X1, s.X2) - half - OriginX) / CellSize));
        var maxC = Math.Min(Cols - 1, (int)Math.Floor((Math.Max(s.X1, s.X2) + half - OriginX) / CellSize));
        var minR = Math.Max(0, (int)Math.Floor((Math.Min(s.Y1, s.Y2) - half - OriginY) / CellSize));
        var maxR = Math.Min(Rows - 1, (int)Math.Floor((Math.Max(s.Y1, s.Y2) + half - OriginY) / CellSize));
        var dx = s.X2 - s.X1;
        var dy = s.Y2 - s.Y1;
        var len2 = dx * dx + dy * dy;
        var half2 = half * half;
        for (var r = minR; r <= maxR; r++)
        {
            var py = OriginY + (r + 0.5) * CellSize;
            for (var c = minC; c <= maxC; c++)
            {
                var px = OriginX + (c + 0.5) * CellSize;
                var t = len2 < 1e-12 ? 0 : Math.Clamp(((px - s.X1) * dx + (py - s.Y1) * dy) / len2, 0, 1);
                var ex = s.X1 + t * dx - px;
                var ey = s.Y1 + t * dy - py;
                if (ex * ex + ey * ey <= half2) _cells[r * Cols + c] = true;
            }
        }
    }

    public void IntersectWith(OccupancyGrid other)
    {
        if (other.Cols != Cols || other.Rows != Rows) throw new ArgumentException("Grid geometry differs.", nameof(other));
        for (var i = 0; i < _cells.Length; i++) _cells[i] &= other._cells[i];
    }

    public int FilledCount() => _cells.Count(c => c);

    public RectMm CellsToRect(int col, int row, int cols, int rows) =>
        new(OriginX + col * CellSize, OriginY + row * CellSize, OriginX + (col + cols) * CellSize, OriginY + (row + rows) * CellSize);

    /// <summary>True if every cell overlapping the square of side <paramref name="size"/> centred on (x, y) is filled.</summary>
    public bool IsSquareFilled(double x, double y, double size)
    {
        var h = size / 2;
        var c0 = (int)Math.Floor((x - h - OriginX) / CellSize);
        var c1 = (int)Math.Floor((x + h - OriginX) / CellSize);
        var r0 = (int)Math.Floor((y - h - OriginY) / CellSize);
        var r1 = (int)Math.Floor((y + h - OriginY) / CellSize);
        if (c0 < 0 || r0 < 0 || c1 >= Cols || r1 >= Rows) return false;
        for (var r = r0; r <= r1; r++)
            for (var c = c0; c <= c1; c++)
                if (!_cells[r * Cols + c]) return false;
        return true;
    }
}
