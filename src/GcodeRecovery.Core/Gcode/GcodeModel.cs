namespace GcodeRecovery.Core.Gcode;

/// <summary>Parsed print: the original lines plus the layer index built from them.</summary>
public sealed class GcodeModel
{
    public required IReadOnlyList<string> Lines { get; init; }
    public required IReadOnlyList<LayerInfo> Layers { get; init; }

    /// <summary>First-layer height read from the slicer settings block (falls back to the first layer's Z).</summary>
    public double FirstLayerHeight { get; init; }

    /// <summary>True when the first-layer height came from an explicit slicer setting.</summary>
    public bool FirstLayerHeightFromSettings { get; init; }

    public string Slicer { get; init; } = "Unknown";

    /// <summary>Filament diameter (mm) from slicer settings; 1.75 when not stated.</summary>
    public double FilamentDiameter { get; init; } = 1.75;

    /// <summary>Filament density (g/cm³) from slicer settings; 1.24 (PLA) when not stated.</summary>
    public double FilamentDensity { get; init; } = 1.24;
    public string? PrinterModel { get; init; }

    /// <summary>"Marker" when slicer layer-change comments were used, "Z" for the motion-based fallback.</summary>
    public string LayerDetection { get; init; } = "Marker";

    /// <summary>Number of leading comment / blank lines (slicer header, thumbnails).</summary>
    public int HeaderLineCount { get; init; }

    public double MaxZ => Layers.Count == 0 ? 0 : Layers.Max(l => double.IsNaN(l.Z) ? 0 : l.Z);

    /// <summary>Index of the layer whose top is closest to <paramref name="heightMm"/>.</summary>
    public int NearestLayerIndex(double heightMm)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < Layers.Count; i++)
        {
            var d = Math.Abs(Layers[i].Z - heightMm);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }
}
