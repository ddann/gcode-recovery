using GcodeRecovery.Core.Gcode;

namespace GcodeRecovery.Core.Tests;

public class GcodeLineTests
{
    [Fact]
    public void Parses_classic_parameters_and_comment()
    {
        var line = GcodeLine.Parse("G1 X10.5 Y-2 Z0.2 E.03 F1200 ; perimeter");
        Assert.Equal("G1", line.Command);
        Assert.True(line.TryGet('X', out var x)); Assert.Equal(10.5, x);
        Assert.True(line.TryGet('Y', out var y)); Assert.Equal(-2, y);
        Assert.True(line.TryGet('E', out var e)); Assert.Equal(0.03, e, 6);
        Assert.Equal("perimeter", line.Comment);
        Assert.True(line.IsMove);
    }

    [Theory]
    [InlineData("G01 X1", "G1")]
    [InlineData("G29.1 Z-0.04", "G29.1")]
    [InlineData("T2", "T2")]
    [InlineData("SET_KINEMATIC_POSITION Z=10", "SET_KINEMATIC_POSITION")]
    [InlineData("  ; just a comment", "")]
    public void Normalises_command(string raw, string expected) => Assert.Equal(expected, GcodeLine.Parse(raw).Command);

    [Fact]
    public void Rewrites_one_parameter_and_keeps_everything_else()
    {
        var line = GcodeLine.Parse("G1 X5 Z12.4 F600 ; lift");
        Assert.Equal("G1 X5 Z2.2 F600 ; lift", line.WithParam('Z', 2.2));
    }

    [Fact]
    public void Handles_parameters_without_spaces()
    {
        var line = GcodeLine.Parse("G1X10Z0.6E1.5");
        Assert.True(line.TryGet('Z', out var z));
        Assert.Equal(0.6, z, 6);
        Assert.Equal("G1X10Z0.4E1.5", line.WithParam('Z', 0.4));
    }

    [Fact]
    public void Klipper_parameters_are_not_misread_as_classic()
    {
        var line = GcodeLine.Parse("SET_GCODE_OFFSET Z=0.1");
        Assert.False(line.Has('Z'));
    }
}

public class LayerParserTests
{
    [Fact]
    public void Bambu_markers_give_exact_layers()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 50));
        Assert.Equal(50, model.Layers.Count);
        Assert.Equal("Marker", model.LayerDetection);
        Assert.Equal(2.0, model.Layers[9].Z, 6);
        Assert.Equal(0.2, model.Layers[9].Height, 6);
        Assert.Equal(10.0, model.MaxZ, 6);
        Assert.Equal("Bambu Studio", model.Slicer);
        Assert.Equal("Bambu Lab P1S", model.PrinterModel);
        Assert.StartsWith("; CHANGE_LAYER", model.Lines[model.Layers[9].StartLine]);
    }

    [Fact]
    public void First_layer_height_is_read_from_slicer_settings()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(firstLayer: 0.28, layerHeight: 0.12, style: SyntheticGcode.Style.Orca));
        Assert.True(model.FirstLayerHeightFromSettings);
        Assert.Equal(0.28, model.FirstLayerHeight, 6);
        Assert.Equal(0.28, model.Layers[0].Z, 6);
        Assert.Equal(0.4, model.Layers[1].Z, 6);
        Assert.Equal("OrcaSlicer", model.Slicer);
    }

    [Fact]
    public void Files_without_markers_fall_back_to_z_detection()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 30, style: SyntheticGcode.Style.NoMarkers));
        Assert.Equal("Z", model.LayerDetection);
        Assert.Contains(model.Layers, l => Math.Abs(l.Z - 3.0) < 1e-6);
        Assert.Equal(6.0, model.MaxZ, 6);
        var layer = model.Layers.First(l => Math.Abs(l.Z - 3.0) < 1e-6);
        Assert.Equal(0.2, layer.Height, 6);
        Assert.NotEmpty(layer.Segments);
    }

    [Fact]
    public void Layer_start_state_tracks_absolute_extrusion_and_fans()
    {
        var model = LayerParser.Parse(SyntheticGcode.SolidBlock(layers: 10, absoluteE: true));
        var layer = model.Layers[5];
        Assert.True(layer.StateAtStart.AbsoluteE);
        Assert.Equal(5 + 5 * 51 * 0.8, layer.StateAtStart.E, 4); // purge 5 + 5 layers × 51 lines × 0.8
        Assert.Equal(220, layer.StateAtStart.NozzleTemp);
        Assert.Equal(60, layer.StateAtStart.BedTemp);
        Assert.Equal("M106 P1 S255", layer.StateAtStart.Fans[1]);
    }

    [Fact]
    public void Arcs_are_linearised()
    {
        var lines = new List<string> { "G90", "M83", ";LAYER_CHANGE", ";Z:0.2", "G1 X10 Y0 Z0.2 F600", "G3 X-10 Y0 I-10 J0 E5" };
        var model = LayerParser.Parse(lines);
        var segs = model.Layers[0].Segments;
        Assert.True(segs.Count > 10);
        Assert.All(segs, s => Assert.InRange(Math.Sqrt(s.X2 * s.X2 + s.Y2 * s.Y2), 9.99, 10.01));
        Assert.True(segs.Max(s => s.Y2) > 9.9); // counter-clockwise goes through +Y
    }
}
