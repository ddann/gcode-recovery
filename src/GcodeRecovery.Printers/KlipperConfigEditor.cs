namespace GcodeRecovery.Printers;

/// <summary>Pure text helpers for editing Klipper's printer.cfg safely.</summary>
public static class KlipperConfigEditor
{
    public const string IncludePath = "custom/gcode_recovery.cfg";

    public const string IncludeContent = """
        # Added by Gcode Recovery.
        # Lets a recovery program set the Z position without homing Z (the failed part is still on the bed):
        # it enables SET_KINEMATIC_POSITION. Remove the [include] line in printer.cfg to undo.
        [force_move]
        enable_force_move: True
        """;

    public static string IncludeLine => $"[include {IncludePath}]";

    public static bool HasInclude(string printerCfg) =>
        printerCfg.Replace("\r\n", "\n").Split('\n').Any(l => l.Trim().Equals(IncludeLine, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds the include at the end of the user section, i.e. before Klipper's SAVE_CONFIG block (<c>#*#</c> lines),
    /// so it overrides any earlier <c>[force_move]</c> settings and SAVE_CONFIG keeps working.
    /// </summary>
    public static string AddInclude(string printerCfg)
    {
        if (HasInclude(printerCfg)) return printerCfg;
        var newline = printerCfg.Contains("\r\n") ? "\r\n" : "\n";
        var lines = printerCfg.Replace("\r\n", "\n").Split('\n').ToList();
        var saveConfig = lines.FindIndex(l => l.StartsWith("#*#", StringComparison.Ordinal));
        var insertAt = saveConfig >= 0 ? saveConfig : lines.Count;
        while (insertAt > 0 && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;
        lines.InsertRange(insertAt, ["", "# Gcode Recovery: allow setting Z without homing (part on the bed)", IncludeLine, ""]);
        return string.Join(newline, lines);
    }
}
