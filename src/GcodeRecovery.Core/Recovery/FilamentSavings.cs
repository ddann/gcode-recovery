using GcodeRecovery.Core.Gcode;

namespace GcodeRecovery.Core.Recovery;

/// <summary>
/// Filament that does not have to be printed again thanks to the recovery: everything in the layers that are
/// already on the bed (up to and including the touched surface layer). Start G-code (purge lines) is not counted.
/// </summary>
public static class FilamentSavings
{
    public static double FilamentMm(GcodeModel model, RecoveryPlan plan) =>
        Math.Max(0, model.Layers.Take(plan.SurfaceLayer.Index + 1).Sum(l => l.FilamentMm));

    public static double Grams(GcodeModel model, RecoveryPlan plan)
    {
        var radius = model.FilamentDiameter / 2;
        var volumeMm3 = FilamentMm(model, plan) * Math.PI * radius * radius;
        return volumeMm3 / 1000.0 * model.FilamentDensity; // mm³ → cm³ × g/cm³
    }
}
