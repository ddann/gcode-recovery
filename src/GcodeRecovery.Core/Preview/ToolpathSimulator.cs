using GcodeRecovery.Core.Gcode;

namespace GcodeRecovery.Core.Preview;

public enum MoveKind : byte
{
    Travel,
    Extrude,

    /// <summary>A Z-down move issued by a force-sensor probe command (G380 / PROBE).</summary>
    Probe,
}

/// <summary>One straight motion of the nozzle in 3D, with the source line it came from.</summary>
public readonly record struct Move3D(float X1, float Y1, float Z1, float X2, float Y2, float Z2, MoveKind Kind, int Line);

/// <summary>
/// Executes a G-code program "on paper" and returns the nozzle path. Used for the 3D preview of what the
/// printer is about to do. Unknown/firmware-specific commands are ignored except for the probe commands,
/// which are shown as a downward move ending at the probe travel limit.
/// </summary>
public static class ToolpathSimulator
{
    public const double PreviewProbeApproachMm = 5;

    private static double? FindContactZ(IReadOnlyList<string> lines, int probeLine)
    {
        for (var j = probeLine + 1; j < Math.Min(lines.Count, probeLine + 6); j++)
        {
            var l = GcodeLine.Parse(lines[j]);
            if (l.Command == "G92" && l.TryGet('Z', out var z)) return z;
            if (l.IsMove) return null;
        }
        return null;
    }

    public static List<Move3D> Simulate(IReadOnlyList<string> lines, double probeTravelMm = 40, double startX = 0, double startY = 0, double startZ = 0)
    {
        var moves = new List<Move3D>(lines.Count / 2);
        var s = new MachineState { X = startX, Y = startY, Z = startZ };

        for (var i = 0; i < lines.Count; i++)
        {
            var line = GcodeLine.Parse(lines[i]);
            if (line.Command.Length == 0) continue;

            if (line.Command == "G380" || line.Command == "PROBE")
            {
                // Where the nozzle meets the part is only known on the printer. If the program sets Z right
                // after the probe (G92 Z), draw the probe ending there and re-base everything before it,
                // assuming the descent started PreviewProbeApproachMm above the part.
                var contact = FindContactZ(lines, i);
                if (contact is { } c)
                {
                    var delta = (float)(c + PreviewProbeApproachMm - s.Z);
                    for (var m = 0; m < moves.Count; m++)
                        moves[m] = moves[m] with { Z1 = moves[m].Z1 + delta, Z2 = moves[m].Z2 + delta };
                    moves.Add(new Move3D((float)s.X, (float)s.Y, (float)(c + PreviewProbeApproachMm), (float)s.X, (float)s.Y, (float)c, MoveKind.Probe, i));
                    s.Z = c;
                }
                else
                {
                    var z2 = line.Command == "G380" && line.TryGet('Z', out var pz) ? s.Z + pz : s.Z - probeTravelMm;
                    moves.Add(new Move3D((float)s.X, (float)s.Y, (float)s.Z, (float)s.X, (float)s.Y, (float)z2, MoveKind.Probe, i));
                    s.Z = z2;
                }
                continue;
            }
            if (line.Command == "G28")
            {
                // Homing X/Y: path is irrelevant for the preview, the position is not.
                if (line.Raw.Contains('X', StringComparison.OrdinalIgnoreCase)) s.X = 0;
                if (line.Raw.Contains('Y', StringComparison.OrdinalIgnoreCase)) s.Y = 0;
                continue;
            }

            s.ApplyModal(line);
            if (!line.IsMove) continue;

            double x0 = s.X, y0 = s.Y, z0 = s.Z, e0 = s.E;
            if (line.TryGet('X', out var x)) s.X = s.AbsoluteXyz ? x : s.X + x;
            if (line.TryGet('Y', out var y)) s.Y = s.AbsoluteXyz ? y : s.Y + y;
            if (line.TryGet('Z', out var z)) s.Z = s.AbsoluteXyz ? z : s.Z + z;
            var extruded = 0.0;
            if (line.TryGet('E', out var e))
            {
                extruded = s.AbsoluteE ? e - e0 : e;
                s.E = s.AbsoluteE ? e : e0 + e;
            }
            if (x0 == s.X && y0 == s.Y && z0 == s.Z) continue;
            var kind = extruded > 1e-6 ? MoveKind.Extrude : MoveKind.Travel;
            moves.Add(new Move3D((float)x0, (float)y0, (float)z0, (float)s.X, (float)s.Y, (float)s.Z, kind, i));
        }
        return moves;
    }
}
