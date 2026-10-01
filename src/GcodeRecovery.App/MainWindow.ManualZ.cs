using GcodeRecovery.Core.Recovery;

namespace GcodeRecovery.App;

/// <summary>
/// Manual Z zero: the user jogs the nozzle onto the top of the part and presses "Set Z0 here". The recovery program
/// then starts by lifting off the part; it never homes or probes. Used by default on Bambu printers, whose
/// force-probe and homing commands are undocumented.
/// </summary>
public partial class MainWindow
{
    /// <summary>Contact Z sent with "Set Z0 here" in this session (null = not set).</summary>
    private double? _manualZSentAt;

    private void InitManualZ() => SetZ0Button.Click += async (_, _) => await SetZ0HereAsync();

    private async Task SetZ0HereAsync()
    {
        if (_printer is not { IsConnected: true } printer)
        {
            Log("Connect to the printer first (connection details are in Settings).");
            return;
        }
        if (BuildPlan(requireSafe: false) is not { } built)
        {
            Log("Analyze the file and choose the layer first: Z zero means the top of that layer.");
            return;
        }
        var contact = built.Plan.ContactZCompensated;
        var ok = await ConfirmDialog.AskAsync(this, "Set Z zero here",
            "Is the nozzle touching the top of the part right now (a sheet of paper just drags)?\n\n" +
            $"This position becomes Z {contact:0.###} (top of layer {built.Plan.SurfaceLayer.Index + 1}). " +
            "The recovery stream will lift off from here, purge and wipe, and continue from layer " +
            $"{built.Plan.ResumeLayer.Index + 1}. Nothing is homed or probed.",
            "Yes, set Z here");
        if (!ok) return;
        try
        {
            await printer.SendGcodeAsync([FormattableString.Invariant($"G92 Z{contact:0.###}")]);
            _manualZSentAt = contact;
            PrinterLog($"Z set to {contact:0.###} at the nozzle's current position.");
        }
        catch (Exception ex)
        {
            PrinterLog("Setting Z failed: " + ex.Message);
        }
    }

    /// <summary>A manual-Z program may only start right after Z was set for the same contact height.</summary>
    private bool ManualZReady()
    {
        if (ZZeroBox.SelectedIndex != 1 || !_streamFromRecovery) return true;
        var expected = BuildPlan(requireSafe: false)?.Plan.ContactZCompensated;
        if (_manualZSentAt is { } sent && expected is { } e && Math.Abs(sent - e) < 1e-6) return true;
        Log("Z zero is set by hand for this printer: jog the nozzle onto the top of the part and press " +
            "\"Set Z0 here\" (Printer panel) before starting.");
        return false;
    }
}
