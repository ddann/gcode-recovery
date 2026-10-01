using System.Text.Json;
using System.Text.Json.Serialization;

namespace GcodeRecovery.Core.Recovery;

public enum FirmwareFlavor
{
    Klipper,
    Bambu,
}

/// <summary>
/// Printer-specific G-code templates. Every routine the recovery file adds (preparation, mesh reuse,
/// load-cell touch-down, purge/wipe) is a plain-text template with <c>{placeholders}</c>, so it can be
/// reviewed and adjusted in the app or in a JSON file without recompiling.
/// </summary>
public sealed class PrinterProfile
{
    public string Id { get; set; } = "custom";
    public string Name { get; set; } = "Custom printer";
    public FirmwareFlavor Flavor { get; set; } = FirmwareFlavor.Klipper;
    public string Notes { get; set; } = "";
    public double BedWidth { get; set; } = 256;
    public double BedDepth { get; set; } = 256;

    /// <summary>Write the result back into the .gcode.3mf container when the input was one.</summary>
    public bool PreferArchiveOutput { get; set; }

    /// <summary>Fixed purge position; null = choose the bed corner farthest from the object automatically.</summary>
    public double? PurgeX { get; set; }
    public double? PurgeY { get; set; }

    /// <summary>Heats the bed, cools the nozzle, homes X/Y without touching Z, establishes a provisional Z.</summary>
    public string PrepareTemplate { get; set; } = "";

    /// <summary>Re-activates the bed mesh measured for the original print. Must not probe the bed again.</summary>
    public string LevelingTemplate { get; set; } = "";

    /// <summary>Moves over the touch point and lowers the cold nozzle until the force sensor triggers, then sets Z.</summary>
    public string ProbeTemplate { get; set; } = "";

    /// <summary>Heats the nozzle away from the part, purges and wipes it.</summary>
    public string PurgeTemplate { get; set; } = "";

    /// <summary>
    /// Filament the purge routine leaves retracted (e.g. the U1 cleaner retracts before cutting the strand).
    /// It is pushed back right at the resume point, so printing starts from a primed, freshly wiped nozzle.
    /// </summary>
    public double PrimeAfterPurgeMm { get; set; }

    /// <summary>Selects a tool. Empty to skip (e.g. AMS slot handled manually).</summary>
    public string ToolSelectTemplate { get; set; } = "";

    /// <summary>End of the touch-test-only program.</summary>
    public string TouchTestEndTemplate { get; set; } = "";

    /// <summary>
    /// Optional alternative preparation that homes Z by touching the bed at (<see cref="ZHomeX"/>, <see cref="ZHomeY"/>)
    /// instead of setting a provisional Z. Only safe when that spot is clear of the part; used when
    /// <see cref="RecoveryOptions.HomeZAtClearSpot"/> is set. Empty = not supported by this printer.
    /// </summary>
    public string ZHomePrepareTemplate { get; set; } = "";

    public double ZHomeX { get; set; } = 10;
    public double ZHomeY { get; set; } = 10;

    public PrinterProfile Clone() => JsonSerializer.Deserialize<PrinterProfile>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static PrinterProfile FromJson(string json) =>
        JsonSerializer.Deserialize<PrinterProfile>(json, JsonOptions) ?? throw new InvalidDataException("Empty profile.");

    public override string ToString() => Name;
}
