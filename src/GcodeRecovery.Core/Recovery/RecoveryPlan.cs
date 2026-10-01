using GcodeRecovery.Core.Gcode;

namespace GcodeRecovery.Core.Recovery;

public enum ZFrameMode
{
    /// <summary>The contact point becomes Z = 0 and all remaining Z values are shifted down (resume layer is printed like a first layer).</summary>
    ShiftToContactZero,

    /// <summary>The contact point is set to its original height; remaining G-code keeps its Z values. Safer with mesh fade.</summary>
    KeepOriginal,
}

public enum ResumeGapMode
{
    /// <summary>Resume layer is printed one of its own layer heights above the touched surface (geometrically exact).</summary>
    LayerThickness,

    /// <summary>Resume layer is printed at the first-layer height read from the G-code, like a fresh first layer.</summary>
    FirstLayerHeight,
}

public sealed record RecoveryOptions
{
    public ZFrameMode ZFrame { get; init; } = ZFrameMode.ShiftToContactZero;
    public ResumeGapMode Gap { get; init; } = ResumeGapMode.LayerThickness;
    public double ClearanceMm { get; init; } = 5;
    public double LiftBeforeHomingMm { get; init; } = 5;
    public double ProbeSpeedMmS { get; init; } = 2;
    public double ProbeTravelMm { get; init; } = 40;
    public double ProbeNozzleTemp { get; init; }
    public double ProbeNozzleMaxTemp { get; init; } = 80;
    public int CooldownSeconds { get; init; } = 180;
    public double PurgeLengthMm { get; init; } = 30;

    /// <summary>How far the nozzle travels past first contact before the sensor triggers (usually ~0).</summary>
    public double TriggerOvertravelMm { get; init; }

    public double TravelFeed { get; init; } = 6000;

    /// <summary>
    /// Home Z by touching the bed at the profile's Z-home spot instead of setting a provisional Z.
    /// Only allowed when that spot is clear of the part (see <see cref="PartClearance"/>).
    /// </summary>
    public bool HomeZAtClearSpot { get; init; }

    /// <summary>How Z zero is found. Manual: the user jogs the nozzle onto the part and sets Z before starting.</summary>
    public ZZeroMethod ZZero { get; init; } = ZZeroMethod.Probe;
}

public enum ZZeroMethod
{
    /// <summary>The program lowers the cold nozzle onto the part with the printer's force sensor.</summary>
    Probe,

    /// <summary>
    /// Z was set by hand before the program starts (nozzle touching the part, then G92 Z = contact height).
    /// The program does no homing and no probing at all; it starts by lifting off the part.
    /// </summary>
    Manual,
}

/// <summary>Distance checks between a bed position and everything printed so far.</summary>
public static class PartClearance
{
    /// <summary>Smallest XY distance from (x, y) to any extrusion of layers up to <paramref name="lastLayerIndex"/>.</summary>
    public static double DistanceToPart(GcodeModel model, int lastLayerIndex, double x, double y)
    {
        var best = double.MaxValue;
        foreach (var layer in model.Layers.Take(lastLayerIndex + 1))
            foreach (var s in layer.Segments)
            {
                double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1;
                var len2 = dx * dx + dy * dy;
                var t = len2 < 1e-12 ? 0 : Math.Clamp(((x - s.X1) * dx + (y - s.Y1) * dy) / len2, 0, 1);
                var ex = s.X1 + t * dx - x;
                var ey = s.Y1 + t * dy - y;
                best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey) - s.Width / 2);
            }
        return best;
    }
}

/// <summary>Everything decided about one recovery: which layer was touched, where, and how Z is remapped.</summary>
public sealed class RecoveryPlan
{
    /// <summary>The last fully printed layer — its top is the surface the nozzle touches.</summary>
    public required LayerInfo SurfaceLayer { get; init; }

    /// <summary>The first layer to print again (printed from its very first line).</summary>
    public required LayerInfo ResumeLayer { get; init; }

    public required double TouchX { get; init; }
    public required double TouchY { get; init; }
    public required RecoveryOptions Options { get; init; }

    /// <summary>Subtracted from every absolute Z in the remaining G-code.</summary>
    public required double ZShift { get; init; }

    /// <summary>Z value assigned (G92) at the moment of contact, before compensation.</summary>
    public required double ContactZ { get; init; }

    /// <summary>Distance between the touched surface and the resume layer's nozzle height.</summary>
    public required double ResumeGap { get; init; }

    public double ResumeZ => ResumeLayer.Z - ZShift;
    public double SafeZ => ContactZ + Options.ClearanceMm;
    public double ContactZCompensated => ContactZ - Options.TriggerOvertravelMm;

    public static RecoveryPlan Create(GcodeModel model, int lastCompletedLayerIndex, double touchX, double touchY, RecoveryOptions options)
    {
        if (lastCompletedLayerIndex < 0 || lastCompletedLayerIndex >= model.Layers.Count - 1)
            throw new ArgumentOutOfRangeException(nameof(lastCompletedLayerIndex),
                "The last completed layer must be followed by at least one more layer to print.");

        var surface = model.Layers[lastCompletedLayerIndex];
        var resume = model.Layers[lastCompletedLayerIndex + 1];
        var gap = options.Gap == ResumeGapMode.FirstLayerHeight ? model.FirstLayerHeight : resume.Z - surface.Z;
        if (gap <= 0) gap = resume.Height > 0 ? resume.Height : model.FirstLayerHeight;

        var shift = options.ZFrame == ZFrameMode.ShiftToContactZero ? resume.Z - gap : 0;
        var contact = resume.Z - shift - gap;
        return new RecoveryPlan
        {
            SurfaceLayer = surface,
            ResumeLayer = resume,
            TouchX = touchX,
            TouchY = touchY,
            Options = options,
            ZShift = shift,
            ContactZ = contact,
            ResumeGap = gap,
        };
    }
}
