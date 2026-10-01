using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bambu.NET.MQTT;
using FluentFTP;

namespace GcodeRecovery.Printers;

/// <summary>
/// Bambu Lab printer in LAN mode (P1S / P1P / X1 / A1). Uses:
/// <list type="bullet">
/// <item>Bambu.NET (MQTT over TLS, port 8883, user "bblp" + access code) for status and commands;</item>
/// <item>FluentFTP (implicit FTPS, port 990) to put files on the SD card;</item>
/// <item>the P1/A1 camera protocol (TLS, port 6000, 80-byte auth then length-prefixed JPEG frames).</item>
/// </list>
/// LAN mode / "LAN only liveview" must be enabled on the printer; the access code is on its screen.
/// </summary>
public sealed class BambuConnection(ConnectionSettings settings) : IPrinterConnection
{
    private const string User = "bblp";
    private BambuMQTTClient? _mqtt;
    private JsonObject _print = new();

    public string Name => $"Bambu Lab {settings.Serial} @ {settings.Host}";
    public bool IsConnected => _mqtt?.Connected == true;
    public bool SendWaitsForExecution => false;
    public PrinterStatus Status { get; private set; } = new();

    public event EventHandler<PrinterStatus>? StatusChanged;
    public event EventHandler<string>? Log;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        PrinterCommands.ValidateHost(settings.Host);
        if (string.IsNullOrWhiteSpace(settings.Serial)) throw new ArgumentException("The printer serial number is required for Bambu LAN mode.");
        if (string.IsNullOrWhiteSpace(settings.Secret)) throw new ArgumentException("The LAN access code is required.");

        _mqtt = new BambuMQTTClient(settings.Host.Trim(), 8883, User, settings.Secret.Trim(), settings.Serial.Trim());
        await _mqtt.Connect().WaitAsync(TimeSpan.FromSeconds(15), ct);
        await _mqtt.Subscribe(OnReport);
        Log?.Invoke(this, "Connected (MQTT).");
    }

    public async Task DisconnectAsync()
    {
        if (_mqtt is null) return;
        try { await _mqtt.Disconnect(); } catch (Exception ex) { Log?.Invoke(this, "Disconnect: " + ex.Message); }
        _mqtt.Dispose();
        _mqtt = null;
    }

    public Task SendGcodeAsync(IEnumerable<string> lines, CancellationToken ct = default)
    {
        // Built with a JSON serializer so quotes/newlines are escaped correctly.
        var gcode = string.Join("\n", lines.Select(PrinterCommands.Sanitize)) + "\n";
        return Publish(new JsonObject
        {
            ["print"] = new JsonObject { ["sequence_id"] = "0", ["command"] = "gcode_line", ["param"] = gcode },
        });
    }

    public Task PauseAsync() => Client.Pause();
    public Task ResumeAsync() => Client.Resume();
    public Task StopAsync() => Client.Stop();
    public Task SetLightAsync(bool on) => on ? Client.CameraLightOn() : Client.CameraLightOff();

    public async Task UploadAndStartAsync(string localPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(localPath).Replace(' ', '_');
        await using (var ftp = new AsyncFtpClient(settings.Host.Trim(), User, settings.Secret.Trim(), 990))
        {
            ftp.Config.EncryptionMode = FtpEncryptionMode.Implicit;
            ftp.Config.ValidateAnyCertificate = true;
            ftp.Config.DataConnectionType = FtpDataConnectionType.PASV;
            await ftp.Connect(ct);
            var ftpProgress = progress is null ? null : new Progress<FtpProgress>(p => progress.Report(p.Progress));
            var status = await ftp.UploadFile(localPath, "/" + fileName, FtpRemoteExists.Overwrite, false, FtpVerify.None, ftpProgress, ct);
            if (status != FtpStatus.Success) throw new IOException($"Upload failed ({status}).");
        }
        Log?.Invoke(this, $"Uploaded {fileName}.");

        JsonObject command = fileName.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase)
            ? new JsonObject
            {
                ["sequence_id"] = "0", ["command"] = "project_file", ["param"] = "Metadata/plate_1.gcode",
                ["url"] = "file:///sdcard/" + fileName, ["subtask_name"] = fileName,
                ["project_id"] = "0", ["profile_id"] = "0", ["task_id"] = "0", ["subtask_id"] = "0",
                ["md5"] = "", ["timelapse"] = false, ["bed_type"] = "auto",
                ["bed_levelling"] = false, // never re-level: the part is on the bed
                ["flow_cali"] = false, ["vibration_cali"] = false, ["layer_inspect"] = false, ["use_ams"] = false,
            }
            : new JsonObject { ["sequence_id"] = "0", ["command"] = "gcode_file", ["param"] = "/sdcard/" + fileName };
        await Publish(new JsonObject { ["print"] = command });
        Log?.Invoke(this, $"Start command sent for {fileName}.");
    }

    public async Task StreamCameraAsync(Action<byte[]> onJpegFrame, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(settings.Host.Trim(), 6000, ct);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true); // printer uses a self-signed certificate
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = settings.Host.Trim() }, ct);

        var auth = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(auth.AsSpan(0), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(auth.AsSpan(4), 0x3000);
        Encoding.ASCII.GetBytes(User).CopyTo(auth, 16);
        var code = Encoding.ASCII.GetBytes(settings.Secret.Trim());
        code.AsSpan(0, Math.Min(32, code.Length)).CopyTo(auth.AsSpan(48));
        await ssl.WriteAsync(auth, ct);

        var header = new byte[16];
        while (!ct.IsCancellationRequested)
        {
            await ssl.ReadExactlyAsync(header, ct);
            var size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size <= 0 || size > 8 * 1024 * 1024) throw new InvalidDataException("Unexpected camera frame size; is LAN liveview enabled?");
            var frame = new byte[size];
            await ssl.ReadExactlyAsync(frame, ct);
            if (frame.Length > 4 && frame[0] == 0xFF && frame[1] == 0xD8) onJpegFrame(frame);
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private BambuMQTTClient Client => _mqtt ?? throw new InvalidOperationException("Not connected.");

    private async Task Publish(JsonObject payload)
    {
        if (!await Client.Publish(payload.ToJsonString())) throw new IOException("The printer did not accept the MQTT message.");
    }

    private void OnReport(string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject root || root["print"] is not JsonObject print) return;
            // P1-series printers send incremental reports: merge into the last known state.
            foreach (var (key, value) in print) _print[key] = value?.DeepClone();
            Status = new PrinterStatus
            {
                State = Str("gcode_state") ?? Status.State,
                NozzleTemp = Num("nozzle_temper"),
                NozzleTarget = Num("nozzle_target_temper"),
                BedTemp = Num("bed_temper"),
                BedTarget = Num("bed_target_temper"),
                ChamberTemp = Num("chamber_temper"),
                ProgressPercent = Num("mc_percent"),
                Layer = (int?)Num("layer_num"),
                TotalLayers = (int?)Num("total_layer_num"),
                RemainingMinutes = (int?)Num("mc_remaining_time"),
                FileName = Str("subtask_name") ?? Str("gcode_file"),
                LightOn = LightState(),
            };
            StatusChanged?.Invoke(this, Status);
        }
        catch (JsonException ex)
        {
            Log?.Invoke(this, "Unreadable report: " + ex.Message);
        }
    }

    private string? Str(string key) => _print[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private double? Num(string key) => _print[key] is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d : v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : null
        : null;

    private bool? LightState()
    {
        if (_print["lights_report"] is not JsonArray lights) return null;
        foreach (var l in lights.OfType<JsonObject>())
            if (l["node"]?.GetValue<string>() == "chamber_light") return l["mode"]?.GetValue<string>() == "on";
        return null;
    }
}
