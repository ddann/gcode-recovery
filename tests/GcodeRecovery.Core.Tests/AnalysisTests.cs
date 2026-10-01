using GcodeRecovery.Core.Analysis;
using GcodeRecovery.Core.Gcode;

namespace GcodeRecovery.Core.Tests;

public class MaxRectangleTests
{
    [Fact]
    public void Finds_largest_rectangle_respecting_min_side()
    {
        var grid = new OccupancyGrid(0, 0, 20, 20, 1);
        // Long thin strip (1 × 20) and a 6 × 5 block.
        for (var c = 0; c < 20; c++) grid[c, 0] = true;
        for (var r = 10; r < 15; r++)
            for (var c = 5; c < 11; c++)
                grid[c, r] = true;

        var any = MaxRectangle.Find(grid, 1)!.Value;
        Assert.Equal(30, any.Area); // block (30) beats strip (20)

        var square = MaxRectangle.Find(grid, 5)!.Value;
        Assert.Equal((5, 10, 6, 5), (square.Col, square.Row, square.Cols, square.Rows));

        Assert.Null(MaxRectangle.Find(grid, 7));
    }

    [Fact]
    public void Stamped_raster_lines_merge_into_solid_area()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 3));
        var grid = OccupancyGrid.ForBounds(95, 95, 125, 125, 0.2);
        foreach (var s in model.Layers[2].Segments) grid.Stamp(s);
        Assert.True(grid.IsSquareFilled(110, 110, 15));
        Assert.False(grid.IsSquareFilled(97, 97, 2));
    }
}

public class FlatAreaFinderTests
{
    [Fact]
    public void Touch_rectangle_lies_inside_the_solid_block()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 50));
        var result = FlatAreaFinder.Analyze(model, 6.1);

        Assert.Equal(6.0, model.Layers[result.NearestLayerIndex].Z, 6);
        Assert.NotNull(result.TouchRect);
        var r = result.TouchRect!.Value;
        Assert.True(r.MinSide >= 3);
        Assert.InRange(r.X0, 99.5, 120.5);
        Assert.InRange(r.X1, 99.5, 120.5);
        Assert.InRange(r.CenterX, 105, 115);
        Assert.InRange(r.CenterY, 105, 115);
        Assert.All(result.Candidates, c => Assert.InRange(c.Layer.Z, 5.4 - 1e-6, 6.6 + 1e-6));
        Assert.True(FlatAreaFinder.IsTouchPointSafe(result, r.CenterX, r.CenterY));
        Assert.False(FlatAreaFinder.IsTouchPointSafe(result, 130, 130));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Only_area_solid_in_every_candidate_layer_is_used()
    {
        // Upper layers are a smaller block: the touch point must lie in the overlap.
        var lower = SyntheticGcode.SolidBlock(layers: 30, x0: 100, y0: 100, size: 40);
        var model = LayerParser.Parse(lower);
        var layers = model.Layers.ToList();
        // Clip the raster lines of layers 26..30 at X = 118 to emulate the top narrowing.
        foreach (var l in layers.Where(l => l.Index >= 25))
        {
            var clipped = l.Segments.Select(s => s with { X1 = Math.Min(s.X1, 118f), X2 = Math.Min(s.X2, 118f) }).ToList();
            l.Segments.Clear();
            l.Segments.AddRange(clipped);
        }

        var result = FlatAreaFinder.Analyze(model, 5.2, new AnalysisSettings { SearchRangeMm = 0.6 });
        Assert.NotNull(result.TouchRect);
        Assert.True(result.TouchRect!.Value.X1 <= 118.5);
        Assert.Contains(result.Candidates, c => c.LargestRect!.Value.X1 > 130); // lower layers alone are wider
    }

    [Fact]
    public void Warns_when_no_area_is_large_enough()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 20, size: 2));
        var result = FlatAreaFinder.Analyze(model, 2.0, new AnalysisSettings { MinTouchSizeMm = 5 });
        Assert.Null(result.TouchRect);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Warns_when_height_is_above_part()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 20));
        var result = FlatAreaFinder.Analyze(model, 9.0);
        Assert.Contains(result.Warnings, w => w.Contains("above the finished part"));
    }
}
