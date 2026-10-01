using GcodeRecovery.Core.Gcode;

namespace GcodeRecovery.Core.Streaming;

/// <summary>Live correction added to every absolute X/Y/Z the printer is sent.</summary>
public readonly record struct Offset3(double X, double Y, double Z)
{
    public static readonly Offset3 Zero = new(0, 0, 0);
    public bool IsZero => X == 0 && Y == 0 && Z == 0;
    public override string ToString() => $"X {X:+0.00;-0.00;0.00}  Y {Y:+0.00;-0.00;0.00}  Z {Z:+0.00;-0.00;0.00}";
}

/// <summary>
/// Applies an <see cref="Offset3"/> to outgoing G-code. Only absolute-mode G0–G3 coordinates are moved;
/// relative moves are already relative, and G92 is left alone on purpose — shifting G92 by the same
/// amount would cancel the correction instead of moving the nozzle.
/// </summary>
public sealed class OffsetTransformer
{
    public bool Absolute { get; set; } = true;

    public string Apply(string raw, Offset3 offset)
    {
        var line = GcodeLine.Parse(raw);
        switch (line.Command)
        {
            case "G90":
                Absolute = true;
                return raw;
            case "G91":
                Absolute = false;
                return raw;
        }
        if (offset.IsZero || !Absolute || !line.IsMove) return raw;

        var result = raw;
        foreach (var (axis, delta) in new[] { ('X', offset.X), ('Y', offset.Y), ('Z', offset.Z) })
        {
            if (delta == 0) continue;
            var parsed = GcodeLine.Parse(result);
            if (parsed.TryGet(axis, out var v)) result = parsed.WithParam(axis, v + delta);
        }
        return result;
    }
}
