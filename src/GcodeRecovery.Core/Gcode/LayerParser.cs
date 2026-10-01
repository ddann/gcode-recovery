using System.Globalization;
using System.Text.RegularExpressions;

namespace GcodeRecovery.Core.Gcode;

/// <summary>
/// Splits a G-code program into layers and records the extrusion paths of each layer.
/// Layer boundaries come from slicer comments (Bambu Studio / OrcaSlicer / PrusaSlicer / Snapmaker Orca /
/// Cura); files without such comments fall back to "a new layer starts when extrusion happens at a higher Z".
/// </summary>
public static partial class LayerParser
{
    public const double DefaultLineWidth = 0.42;

    [GeneratedRegex(@"^\s*;\s*(CHANGE_LAYER|LAYER_CHANGE)\s*$|^\s*;LAYER:\s*-?\d+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LayerMarker();

    [GeneratedRegex(@"^\s*;\s*(?:Z_HEIGHT|Z)\s*:\s*([-+]?\d*\.?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ZComment();

    [GeneratedRegex(@"^\s*;\s*(?:LAYER_HEIGHT|HEIGHT)\s*:\s*([-+]?\d*\.?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex HeightComment();

    [GeneratedRegex(@"^\s*;\s*(?:LINE_WIDTH|WIDTH)\s*:\s*([-+]?\d*\.?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WidthComment();

    [GeneratedRegex(@"^\s*;\s*(?:initial_layer_print_height|first_layer_height)\s*=\s*([-+]?\d*\.?\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FirstLayerSetting();

    [GeneratedRegex(@"^\s*;\s*(?:printer_model|printer_settings_id)\s*=\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PrinterModelSetting();

    [GeneratedRegex(@"^\s*;\s*filament_diameter\s*=\s*([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DiameterSetting();

    [GeneratedRegex(@"^\s*;\s*filament_density\s*=\s*([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DensitySetting();

    public static GcodeModel Parse(IReadOnlyList<string> lines)
    {
        var useMarkers = lines.Any(l => l.Length < 40 && LayerMarker().IsMatch(l));
        var state = new MachineState { LineWidth = DefaultLineWidth };
        var layers = new List<LayerInfo>();
        LayerInfo? current = null;
        double? firstLayerSetting = null;
        double? diameter = null, density = null;
        string? printerModel = null;
        var lastZMoveLine = 0;
        MachineState? beforeLastZMove = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var line = GcodeLine.Parse(raw);

            if (line.Command.Length == 0)
            {
                if (line.Comment is null) continue;
                if (useMarkers && LayerMarker().IsMatch(raw))
                {
                    current = StartLayer(layers, current, i, state);
                    continue;
                }
                ReadComment(raw, current, state, ref firstLayerSetting, ref printerModel);
                if (diameter is null && DiameterSetting().Match(raw) is { Success: true } dm) diameter = Num(dm);
                if (density is null && DensitySetting().Match(raw) is { Success: true } dn) density = Num(dn);
                continue;
            }

            state.ApplyModal(line);
            if (!line.IsMove) continue;

            var stateBeforeMove = line.Has('Z') ? state.Clone() : null;
            var x0 = state.X;
            var y0 = state.Y;
            var z0 = state.Z;
            var e0 = state.E;
            if (line.TryGet('F', out var f)) state.Feedrate = f;
            Advance(line, 'X', state.AbsoluteXyz, v => state.X = v, state.X);
            Advance(line, 'Y', state.AbsoluteXyz, v => state.Y = v, state.Y);
            Advance(line, 'Z', state.AbsoluteXyz, v => state.Z = v, state.Z);
            var extruded = 0.0;
            if (line.TryGet('E', out var e))
            {
                extruded = state.AbsoluteE ? e - e0 : e;
                state.E = state.AbsoluteE ? e : e0 + e;
            }
            if (current is not null && extruded != 0) current.FilamentMm += extruded;
            if (Math.Abs(state.Z - z0) > 1e-9)
            {
                lastZMoveLine = i;
                beforeLastZMove = stateBeforeMove;
            }

            var isExtrusion = extruded > 1e-6 && (Math.Abs(state.X - x0) > 1e-6 || Math.Abs(state.Y - y0) > 1e-6);
            if (!isExtrusion) continue;

            if (!useMarkers && (current is null || state.Z > current.Z + 1e-4))
            {
                var before = beforeLastZMove ?? state.Clone();
                current = StartLayer(layers, current, beforeLastZMove is null ? i : lastZMoveLine, before);
                current.Z = state.Z;
            }
            if (current is null) continue;
            if (double.IsNaN(current.Z)) current.Z = state.Z;
            AddPath(current, line, x0, y0, state);
        }

        if (current is not null) current.EndLine = lines.Count;
        FinaliseHeights(layers);

        var firstLayer = firstLayerSetting ?? (layers.Count > 0 ? layers[0].Z : 0.2);
        return new GcodeModel
        {
            Lines = lines,
            Layers = layers,
            FirstLayerHeight = firstLayer,
            FirstLayerHeightFromSettings = firstLayerSetting.HasValue,
            Slicer = DetectSlicer(lines),
            FilamentDiameter = diameter is > 0.5 and < 5 ? diameter.Value : 1.75,
            FilamentDensity = density is > 0.3 and < 5 ? density.Value : 1.24,
            PrinterModel = printerModel,
            LayerDetection = useMarkers ? "Marker" : "Z",
            HeaderLineCount = CountHeaderLines(lines),
            IsRecoveryProgram = lines.Take(5000).Any(l => l.StartsWith("; Gcode Recovery: resume program", StringComparison.Ordinal)
                                                     || l.StartsWith("; Gcode Recovery: TOUCH TEST", StringComparison.Ordinal)),
        };
    }

    private static LayerInfo StartLayer(List<LayerInfo> layers, LayerInfo? current, int startLine, MachineState state)
    {
        if (current is not null) current.EndLine = startLine;
        var layer = new LayerInfo { Index = layers.Count, StartLine = startLine, EndLine = startLine, StateAtStart = state.Clone() };
        layers.Add(layer);
        return layer;
    }

    private static void ReadComment(string raw, LayerInfo? current, MachineState state, ref double? firstLayer, ref string? printerModel)
    {
        Match m;
        if (current is not null && double.IsNaN(current.Z) && (m = ZComment().Match(raw)).Success)
            current.Z = Num(m);
        else if (current is not null && double.IsNaN(current.Height) && (m = HeightComment().Match(raw)).Success)
            current.Height = Num(m);
        else if ((m = WidthComment().Match(raw)).Success && Num(m) > 0)
            state.LineWidth = Num(m);
        else if ((m = FirstLayerSetting().Match(raw)).Success && Num(m) > 0)
            firstLayer = Num(m);
        else if (printerModel is null && (m = PrinterModelSetting().Match(raw)).Success)
            printerModel = m.Groups[1].Value.Trim().Trim('"');
    }

    private static double Num(Match m) => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

    private static void Advance(GcodeLine line, char axis, bool absolute, Action<double> set, double current)
    {
        if (line.TryGet(axis, out var v)) set(absolute ? v : current + v);
    }

    private static void AddPath(LayerInfo layer, GcodeLine line, double x0, double y0, MachineState state)
    {
        var w = (float)(state.LineWidth > 0 ? state.LineWidth : DefaultLineWidth);
        var isArc = line.Command is "G2" or "G3";
        if (!isArc || !line.TryGet('I', out var iOff) | !line.TryGet('J', out var jOff))
        {
            layer.Segments.Add(new Segment((float)x0, (float)y0, (float)state.X, (float)state.Y, w));
            return;
        }

        // Linearise the arc into chords of roughly 0.5 mm.
        var cx = x0 + iOff;
        var cy = y0 + jOff;
        var r = Math.Sqrt(iOff * iOff + jOff * jOff);
        var a0 = Math.Atan2(y0 - cy, x0 - cx);
        var a1 = Math.Atan2(state.Y - cy, state.X - cx);
        var clockwise = line.Command == "G2";
        var sweep = clockwise ? a0 - a1 : a1 - a0;
        if (sweep <= 1e-9) sweep += 2 * Math.PI;
        var steps = Math.Clamp((int)Math.Ceiling(sweep * r / 0.5), 1, 720);
        double px = x0, py = y0;
        for (var s = 1; s <= steps; s++)
        {
            var a = a0 + (clockwise ? -1 : 1) * sweep * s / steps;
            var nx = s == steps ? state.X : cx + r * Math.Cos(a);
            var ny = s == steps ? state.Y : cy + r * Math.Sin(a);
            layer.Segments.Add(new Segment((float)px, (float)py, (float)nx, (float)ny, w));
            px = nx;
            py = ny;
        }
    }

    private static void FinaliseHeights(List<LayerInfo> layers)
    {
        // Drop marker layers that never printed anything and have no Z (e.g. trailing markers).
        layers.RemoveAll(l => double.IsNaN(l.Z));
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (double.IsNaN(layer.Height) || layer.Height <= 0)
                layer.Height = i == 0 ? layer.Z : Math.Round(layer.Z - layers[i - 1].Z, 4);
        }
        // Re-index and make layer ranges contiguous after removals.
        for (var i = 0; i < layers.Count; i++)
        {
            var copy = layers[i];
            if (copy.Index != i)
            {
                layers[i] = new LayerInfo
                {
                    Index = i, StartLine = copy.StartLine, EndLine = copy.EndLine,
                    Z = copy.Z, Height = copy.Height, StateAtStart = copy.StateAtStart, FilamentMm = copy.FilamentMm,
                };
                layers[i].Segments.AddRange(copy.Segments);
            }
            if (i + 1 < layers.Count) layers[i].EndLine = layers[i + 1].StartLine;
        }
    }

    private static string DetectSlicer(IReadOnlyList<string> lines)
    {
        foreach (var l in lines.Take(400))
        {
            if (l.Contains("BambuStudio", StringComparison.OrdinalIgnoreCase)) return "Bambu Studio";
            if (l.Contains("OrcaSlicer", StringComparison.OrdinalIgnoreCase)) return "OrcaSlicer";
            if (l.Contains("Snapmaker", StringComparison.OrdinalIgnoreCase)) return "Snapmaker Orca";
            if (l.Contains("PrusaSlicer", StringComparison.OrdinalIgnoreCase)) return "PrusaSlicer";
            if (l.Contains("SuperSlicer", StringComparison.OrdinalIgnoreCase)) return "SuperSlicer";
            if (l.Contains("Cura", StringComparison.OrdinalIgnoreCase)) return "Cura";
        }
        return "Unknown";
    }

    private static int CountHeaderLines(IReadOnlyList<string> lines)
    {
        var n = 0;
        while (n < lines.Count && (lines[n].TrimStart().StartsWith(';') || string.IsNullOrWhiteSpace(lines[n]))) n++;
        return n;
    }
}
