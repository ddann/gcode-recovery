namespace GcodeRecovery.Core.Gcode;

/// <summary>A straight extrusion path in the XY plane (arcs are linearised).</summary>
public readonly record struct Segment(float X1, float Y1, float X2, float Y2, float Width);

public sealed class LayerInfo
{
    public int Index { get; init; }

    /// <summary>Index of the first line belonging to this layer (the layer-change marker when present).</summary>
    public int StartLine { get; init; }

    /// <summary>Index one past the last line of this layer.</summary>
    public int EndLine { get; set; }

    /// <summary>Print height (top of this layer) in the original coordinate frame.</summary>
    public double Z { get; set; } = double.NaN;

    /// <summary>Layer thickness.</summary>
    public double Height { get; set; } = double.NaN;

    public List<Segment> Segments { get; } = new();

    /// <summary>Net filament length fed in this layer (mm of filament, retractions subtracted).</summary>
    public double FilamentMm { get; set; }

    /// <summary>Machine state immediately before <see cref="StartLine"/> is executed.</summary>
    public required MachineState StateAtStart { get; init; }

    public override string ToString() => $"Layer {Index + 1} @ Z {Z:0.###}";
}
