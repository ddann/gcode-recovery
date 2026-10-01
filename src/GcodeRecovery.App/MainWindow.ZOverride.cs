using GcodeRecovery.Core.Recovery;
using GcodeRecovery.Printers;

namespace GcodeRecovery.App;

public enum ZSetup
{
    /// <summary>The printer can run the program as it is.</summary>
    Ready,

    /// <summary>The user chose to home Z at the clear bed corner: regenerate the program, then run it.</summary>
    SwitchToZHome,

    Cancel,
}

public partial class MainWindow
{
    /// <summary>
    /// Klipper refuses Z moves until Z is homed, and homing Z on the part would crash into it. The recovery
    /// program therefore sets a provisional Z with SET_KINEMATIC_POSITION and homes only X/Y. Klipper offers that
    /// command only with [force_move] enabled. This checks the printer and, with the user's consent, either
    /// enables it (config writable) or switches to homing Z at the bed corner when that spot is clear of the part.
    /// </summary>
    private async Task<ZSetup> EnsureZOverrideAsync(IPrinterConnection printer, IEnumerable<string> program)
    {
        bool needed, writable;
        try
        {
            needed = await printer.NeedsZOverrideSetupAsync(program);
            if (!needed) return ZSetup.Ready;
            writable = await printer.CanEnableZOverrideAsync();
        }
        catch (Exception ex)
        {
            PrinterLog("Could not check the printer configuration: " + ex.Message);
            return ZSetup.Cancel;
        }

        const string why =
            "The part is on the bed, so Z must not be homed on it. The program only homes X/Y and sets Z with " +
            "SET_KINEMATIC_POSITION, which Klipper only allows with '[force_move] enable_force_move: True'. " +
            "This printer does not have that enabled.";

        var cornerClear = CornerClearance() is { } d && d >= ResumeGenerator.MinZHomeClearanceMm ? d : (double?)null;

        if (writable)
        {
            var ok = await ConfirmDialog.AskAsync(this, "Allow Z without homing",
                why + "\n\nGcode Recovery can set this up now:\n" +
                "  • back up printer.cfg (printer.cfg.gcode-recovery.bak)\n" +
                $"  • write {KlipperConfigEditor.IncludePath} with the [force_move] section\n" +
                $"  • add {KlipperConfigEditor.IncludeLine} to printer.cfg\n" +
                "  • restart Klipper (heaters switch off briefly, the part is not moved)\n\nContinue?",
                "Set up and continue");
            if (!ok)
            {
                PrinterLog("Cancelled: the printer still refuses Z moves without homing.");
                return ZSetup.Cancel;
            }
            try
            {
                await printer.EnableZOverrideAsync(new Progress<string>(PrinterLog));
                return ZSetup.Ready;
            }
            catch (Exception ex)
            {
                PrinterLog("Setup failed: " + ex.Message);
                return ZSetup.Cancel;
            }
        }

        if (cornerClear is { } clear)
        {
            var useCorner = await ConfirmDialog.AskAsync(this, "Printer configuration is read-only",
                why + "\n\nThe printer's configuration is read-only over the network (on the Snapmaker U1 this changes with " +
                "advanced mode), so this cannot be enabled from here.\n\n" +
                $"Alternative: home Z at the bed corner X{_profile.ZHomeX:0} Y{_profile.ZHomeY:0}. That spot is {clear:0} mm clear of " +
                "the part. The U1 first lowers the bed to its bottom endstop, then touches the bed with the nozzle at that " +
                "corner, rises above the part and only then moves to the touch point on the part.\n\nUse the corner Z home?",
                "Home Z at the corner");
            if (useCorner)
            {
                ZHomeBox.IsChecked = true;
                PrinterLog($"Using Z home at the bed corner (part {clear:0} mm away). Regenerating the program.");
                return ZSetup.SwitchToZHome;
            }
            PrinterLog("Cancelled.");
            return ZSetup.Cancel;
        }

        await ConfirmDialog.AskAsync(this, "Printer configuration needed",
            why + "\n\nThe configuration is read-only over the network, and the bed corner used for Z homing is not clear of " +
            "the part, so neither automatic option is safe.\n\nTo fix it: enable advanced mode on the printer, open Fluidd → " +
            "Configuration → printer.cfg and add at the end (above the #*# block):\n\n[force_move]\nenable_force_move: True\n\n" +
            "Save & restart, then try again.", "OK", "Close");
        PrinterLog("Add [force_move] enable_force_move: True to printer.cfg (see dialog), then retry.");
        return ZSetup.Cancel;
    }

    /// <summary>Distance between the profile's Z-home spot and the printed part, if a plan exists.</summary>
    private double? CornerClearance()
    {
        if (_model is null || string.IsNullOrWhiteSpace(_profile.ZHomePrepareTemplate) || SelectedSurfaceLayer() is not { } s) return null;
        return PartClearance.DistanceToPart(_model, s, _profile.ZHomeX, _profile.ZHomeY);
    }
}
