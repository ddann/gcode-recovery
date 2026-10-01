namespace GcodeRecovery.Printers;

/// <summary>Live printer state as reported by the printer (fields are null until first reported).</summary>
public sealed record PrinterStatus
{
    public string State { get; init; } = "Unknown";
    public double? NozzleTemp { get; init; }
    public double? NozzleTarget { get; init; }
    public double? BedTemp { get; init; }
    public double? BedTarget { get; init; }
    public double? ChamberTemp { get; init; }
    public double? ProgressPercent { get; init; }
    public int? Layer { get; init; }
    public int? TotalLayers { get; init; }
    public int? RemainingMinutes { get; init; }
    public string? FileName { get; init; }
    public bool? LightOn { get; init; }
}

public enum Axis { X, Y, Z }

public sealed record ConnectionSettings
{
    /// <summary>IP address or host name of the printer on the local network.</summary>
    public required string Host { get; init; }

    /// <summary>Bambu: LAN access code shown on the printer screen. Moonraker: API key (optional).</summary>
    public string Secret { get; init; } = "";

    /// <summary>Bambu: printer serial number (needed for the MQTT topics).</summary>
    public string Serial { get; init; } = "";

    /// <summary>Moonraker: port (default 7125). Camera URL override for Moonraker (default /webcam/?action=stream).</summary>
    public int Port { get; init; }
    public string? CameraUrl { get; init; }
}

/// <summary>Common control surface for the supported printers.</summary>
public interface IPrinterConnection : IAsyncDisposable
{
    string Name { get; }
    bool IsConnected { get; }

    /// <summary>True when <see cref="SendGcodeAsync"/> returns only after the printer has processed the lines.</summary>
    bool SendWaitsForExecution { get; }
    PrinterStatus Status { get; }

    event EventHandler<PrinterStatus>? StatusChanged;
    event EventHandler<string>? Log;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    /// <summary>Runs one or more G-code lines immediately.</summary>
    Task SendGcodeAsync(IEnumerable<string> lines, CancellationToken ct = default);

    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
    Task SetLightAsync(bool on);

    /// <summary>Uploads a file to the printer's storage and starts it. Bed leveling is disabled for the job.</summary>
    Task UploadAndStartAsync(string localPath, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Streams camera frames (complete JPEG images) until cancelled.</summary>
    Task StreamCameraAsync(Action<byte[]> onJpegFrame, CancellationToken ct);
}

/// <summary>Validated helpers shared by the connection implementations.</summary>
public static class PrinterCommands
{
    public static IReadOnlyList<string> Jog(Axis axis, double distanceMm, double feed) =>
    [
        "G91",
        FormattableString.Invariant($"G1 {axis}{distanceMm:0.###} F{feed:0}"),
        "G90",
    ];

    public static string SetNozzle(double celsius) => FormattableString.Invariant($"M104 S{Math.Clamp(celsius, 0, 300):0}");
    public static string SetBed(double celsius) => FormattableString.Invariant($"M140 S{Math.Clamp(celsius, 0, 120):0}");
    /// <summary>Part-cooling fan. Bambu addresses it as fan P1; Klipper's M106 has no fan index.</summary>
    public static string PartFan(double percent, bool bambu) =>
        FormattableString.Invariant($"M106 {(bambu ? "P1 " : "")}S{Math.Clamp(percent, 0, 100) * 2.55:0}");

    /// <summary>Removes control characters so one user-typed command cannot smuggle in extra lines.</summary>
    public static string Sanitize(string line) =>
        new(line.Where(c => !char.IsControl(c)).ToArray());

    public static void ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host.Trim()) == UriHostNameType.Unknown)
            throw new ArgumentException($"'{host}' is not a valid IP address or host name.", nameof(host));
    }
}
