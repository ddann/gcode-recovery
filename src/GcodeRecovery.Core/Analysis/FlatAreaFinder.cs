using GcodeRecovery.Core.Gcode;

namespace GcodeRecovery.Core.Analysis;

public sealed record AnalysisSettings
{
    /// <summary>Layers whose top lies within ± this distance of the measured height are candidates.</summary>
    public double SearchRangeMm { get; init; } = 0.4;

    /// <summary>Raster resolution.</summary>
    public double CellSizeMm { get; init; } = 0.2;

    /// <summary>Smallest acceptable side of the touch-down rectangle (after the edge margin is removed).</summary>
    public double MinTouchSizeMm { get; init; } = 3.0;

    /// <summary>Distance kept between the touch rectangle and the edge of the solid region.</summary>
    public double EdgeMarginMm { get; init; } = 0.3;
}

public sealed record LayerFlatArea(LayerInfo Layer, RectMm? LargestRect);

public sealed class FlatAreaResult
{
    public required double MeasuredHeight { get; init; }
    public required int NearestLayerIndex { get; init; }
    public required IReadOnlyList<LayerFlatArea> Candidates { get; init; }

    /// <summary>Material present in every candidate layer — wherever the print actually stopped, it is solid here.</summary>
    public required OccupancyGrid Intersection { get; init; }

    /// <summary>Best touch-down rectangle inside <see cref="Intersection"/>, already shrunk by the edge margin.</summary>
    public RectMm? TouchRect { get; init; }

    /// <summary>The candidate layer with the largest flat area on its own (informational).</summary>
    public LayerFlatArea? LargestSingleLayer { get; init; }

    public required AnalysisSettings Settings { get; init; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Finds where the nozzle can safely touch down on the partially printed object.
/// <para>
/// The caliper measurement is only accurate to a layer or two, so the exact layer the print stopped on is
/// unknown. A touch point is therefore only accepted where material exists in <em>every</em> layer of the
/// candidate range: whichever of those layers is the real top, the nozzle lands on solid extrusion of it.
/// </para>
/// </summary>
public static class FlatAreaFinder
{
    public static FlatAreaResult Analyze(GcodeModel model, double measuredHeightMm, AnalysisSettings? settings = null)
    {
        settings ??= new AnalysisSettings();
        if (model.Layers.Count == 0) throw new InvalidOperationException("No printable layers were found in the G-code.");
        if (measuredHeightMm <= 0) throw new ArgumentOutOfRangeException(nameof(measuredHeightMm), "Height must be positive.");

        var nearest = model.NearestLayerIndex(measuredHeightMm);
        var candidates = model.Layers
            .Where(l => Math.Abs(l.Z - measuredHeightMm) <= settings.SearchRangeMm || Math.Abs(l.Index - nearest) <= 1)
            .Where(l => l.Segments.Count > 0)
            .OrderBy(l => l.Index)
            .ToList();
        if (candidates.Count == 0) throw new InvalidOperationException("No layers with extrusion near the measured height.");

        var bounds = Bounds(candidates);
        var template = OccupancyGrid.ForBounds(bounds.X0, bounds.Y0, bounds.X1, bounds.Y1, settings.CellSizeMm);
        var minCells = (int)Math.Ceiling((settings.MinTouchSizeMm + 2 * settings.EdgeMarginMm) / settings.CellSizeMm);

        OccupancyGrid? intersection = null;
        var perLayer = new List<LayerFlatArea>();
        foreach (var layer in candidates)
        {
            var grid = template.CloneEmpty();
            foreach (var s in layer.Segments) grid.Stamp(s);
            var rect = MaxRectangle.Find(grid, minCells);
            perLayer.Add(new LayerFlatArea(layer, rect is { } r ? ToMm(grid, r, settings.EdgeMarginMm) : null));
            if (intersection is null) intersection = grid;
            else intersection.IntersectWith(grid);
        }

        var touch = MaxRectangle.Find(intersection!, minCells);
        var result = new FlatAreaResult
        {
            MeasuredHeight = measuredHeightMm,
            NearestLayerIndex = nearest,
            Candidates = perLayer,
            Intersection = intersection!,
            TouchRect = touch is { } t ? ToMm(intersection!, t, settings.EdgeMarginMm) : null,
            LargestSingleLayer = perLayer.Where(p => p.LargestRect is not null).OrderByDescending(p => p.LargestRect!.Value.Area).FirstOrDefault(),
            Settings = settings,
        };

        var diff = Math.Abs(model.Layers[nearest].Z - measuredHeightMm);
        if (measuredHeightMm > model.MaxZ + settings.SearchRangeMm)
            result.Warnings.Add($"Measured height {measuredHeightMm:0.##} mm is above the finished part height ({model.MaxZ:0.##} mm). Check the measurement.");
        else if (diff > settings.SearchRangeMm)
            result.Warnings.Add($"The closest layer is {diff:0.##} mm away from the measured height. Check the measurement.");
        if (result.TouchRect is null)
            result.Warnings.Add($"No area of at least {settings.MinTouchSizeMm:0.#} × {settings.MinTouchSizeMm:0.#} mm is solid in all {candidates.Count} candidate layers. " +
                                "Narrow the search range, lower the minimum touch size, or click a solid spot in the preview.");
        return result;
    }

    /// <summary>Checks a user-chosen touch point against the candidate-layer intersection.</summary>
    public static bool IsTouchPointSafe(FlatAreaResult result, double x, double y) =>
        result.Intersection.IsSquareFilled(x, y, result.Settings.MinTouchSizeMm);

    private static RectMm ToMm(OccupancyGrid grid, MaxRectangle.CellRect r, double margin) =>
        grid.CellsToRect(r.Col, r.Row, r.Cols, r.Rows).Shrink(margin);

    private static RectMm Bounds(IEnumerable<LayerInfo> layers)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var s in layers.SelectMany(l => l.Segments))
        {
            minX = Math.Min(minX, Math.Min(s.X1, s.X2));
            minY = Math.Min(minY, Math.Min(s.Y1, s.Y2));
            maxX = Math.Max(maxX, Math.Max(s.X1, s.X2));
            maxY = Math.Max(maxY, Math.Max(s.Y1, s.Y2));
        }
        return new RectMm(minX, minY, maxX, maxY);
    }
}
