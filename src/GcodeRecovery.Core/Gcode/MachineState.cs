namespace GcodeRecovery.Core.Gcode;

/// <summary>
/// Printer state reconstructed while walking the G-code. A snapshot is stored at the start of every layer
/// so that the recovery file can restore extrusion mode, extruder position, temperatures, fans and tool.
/// </summary>
public sealed class MachineState
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double E { get; set; }
    public double Feedrate { get; set; } = 3000;
    public bool AbsoluteXyz { get; set; } = true;
    public bool AbsoluteE { get; set; } = true;
    public int Tool { get; set; } = -1;
    public double NozzleTemp { get; set; }
    public double BedTemp { get; set; }
    public double LineWidth { get; set; }

    /// <summary>Last fan command per fan index (M106 P&lt;n&gt; / M107), kept verbatim.</summary>
    public Dictionary<int, string> Fans { get; init; } = new();

    public MachineState Clone() => new()
    {
        X = X, Y = Y, Z = Z, E = E, Feedrate = Feedrate,
        AbsoluteXyz = AbsoluteXyz, AbsoluteE = AbsoluteE, Tool = Tool,
        NozzleTemp = NozzleTemp, BedTemp = BedTemp, LineWidth = LineWidth,
        Fans = new Dictionary<int, string>(Fans),
    };

    /// <summary>Applies the non-motion side effects of a line (modes, temperatures, fans, tool, G92).</summary>
    public void ApplyModal(GcodeLine line)
    {
        switch (line.Command)
        {
            case "G90":
                AbsoluteXyz = true;
                AbsoluteE = true;
                break;
            case "G91":
                AbsoluteXyz = false;
                AbsoluteE = false;
                break;
            case "M82":
                AbsoluteE = true;
                break;
            case "M83":
                AbsoluteE = false;
                break;
            case "G92":
                if (!line.Has('X') && !line.Has('Y') && !line.Has('Z') && !line.Has('E'))
                {
                    X = Y = Z = E = 0;
                    break;
                }
                if (line.TryGet('X', out var x)) X = x;
                if (line.TryGet('Y', out var y)) Y = y;
                if (line.TryGet('Z', out var z)) Z = z;
                if (line.TryGet('E', out var e)) E = e;
                break;
            case "M104":
            case "M109":
                if (line.TryGet('S', out var s) && s > 0) NozzleTemp = s;
                break;
            case "M140":
            case "M190":
                if (line.TryGet('S', out var b) && b > 0) BedTemp = b;
                break;
            case "M106":
            case "M107":
                var index = line.TryGet('P', out var p) ? (int)p : 0;
                Fans[index] = line.Raw.Trim();
                break;
            default:
                if (line.Command.Length > 1 && line.Command[0] == 'T' && int.TryParse(line.Command.AsSpan(1), out var t) && t >= 0 && t < 255)
                    Tool = t;
                break;
        }
    }
}
