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
            ["print"] = new JsonObject { ["sequence_id"] = NextSequence(), ["command"] = "gcode_line", ["param"] = gcode },
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

        // Where the SD card is mounted differs between models/firmware (P1: /mnt/sdcard, X1: /sdcard), so each
        // candidate is tried until the printer accepts one. The printer answers every command on the report topic.
        var is3mf = fileName.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase);
        foreach (var location in StartLocations(fileName, is3mf))
        {
            var command = is3mf
                ? new JsonObject
                {
                    ["sequence_id"] = NextSequence(), ["command"] = "project_file", ["param"] = "Metadata/plate_1.gcode",
                    ["url"] = location, ["subtask_name"] = Path.GetFileNameWithoutExtension(fileName),
                    ["project_id"] = "0", ["profile_id"] = "0", ["task_id"] = "0", ["subtask_id"] = "0",
                    ["md5"] = "", ["timelapse"] = false, ["bed_type"] = "auto",
                    ["bed_levelling"] = false, // never re-level: the part is on the bed
                    ["flow_cali"] = false, ["vibration_cali"] = false, ["layer_inspect"] = false, ["use_ams"] = false,
                }
                : new JsonObject { ["sequence_id"] = NextSequence(), ["command"] = "gcode_file", ["param"] = location };

            var reply = AwaitReply(command["command"]!.GetValue<string>(), ct);
            await Publish(new JsonObject { ["print"] = command });
            var answer = await reply;
            if (answer is null)
            {
                Log?.Invoke(this, $"Start command sent ({location}); no reply from the printer yet, watch its status.");
                return;
            }
            if (!IsFailure(answer))
            {
                Log?.Invoke(this, $"Printer accepted the job ({location}).");
                return;
            }
            Log?.Invoke(this, $"Printer refused {location}, trying the next location…");
        }
        throw new IOException($"The printer refused to start {fileName} from every known SD-card location. " +
                              "Check that an SD card is inserted and that LAN Only Mode + Developer Mode are on.");
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

    // Bambu firmware has no user configuration for this; the profile sets Z with G92 after the touch-down.
    public Task<bool> NeedsZOverrideSetupAsync(IEnumerable<string> program, CancellationToken ct = default) => Task.FromResult(false);
    public Task EnableZOverrideAsync(IProgress<string>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> CanEnableZOverrideAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private BambuMQTTClient Client => _mqtt ?? throw new InvalidOperationException("Not connected.");

    private int _sequence;
    private readonly object _replyGate = new();
    private (string Command, TaskCompletionSource<JsonObject?> Reply)? _pendingReply;

    private string NextSequence() => Interlocked.Increment(ref _sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Paths/URLs to try, most likely first, for a file uploaded to the SD-card root.</summary>
    public static IReadOnlyList<string> StartLocations(string fileName, bool is3mf) => is3mf
        ? ["file:///mnt/sdcard/" + fileName, "file:///sdcard/" + fileName, "ftp://" + fileName]
        : ["/mnt/sdcard/" + fileName, "/sdcard/" + fileName];

    private static bool IsFailure(JsonObject reply) =>
        reply["result"] is JsonValue r && r.TryGetValue<string>(out var s) && s.StartsWith("fail", StringComparison.OrdinalIgnoreCase);

    /// <summary>Waits (max 8 s) for the printer's answer to <paramref name="command"/>; null when none arrives.</summary>
    private async Task<JsonObject?> AwaitReply(string command, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_replyGate) _pendingReply = (command, tcs);
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(8), ct));
        lock (_replyGate) _pendingReply = null;
        return winner == tcs.Task ? tcs.Task.Result : null;
    }

    private async Task Publish(JsonObject payload)
    {
        if (!await Client.Publish(payload.ToJsonString())) throw new IOException("The printer did not accept the MQTT message.");
    }

    private void OnReport(string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject root) return;
            if (DescribeRejection(root) is { } rejection) Log?.Invoke(this, rejection);
            if (root["print"] is JsonObject answer && answer["command"] is JsonValue cmd && cmd.TryGetValue<string>(out var name))
                lock (_replyGate)
                    if (_pendingReply is { } pending && pending.Command == name && answer.ContainsKey("result"))
                        pending.Reply.TrySetResult(answer);
            if (root["print"] is not JsonObject print) return;
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

    /// <summary>Firmware error for commands that are not signed by Bambu software (Authorization Control, 2025+).</summary>
    public const long AuthorizationRejected = 84033543;

    /// <summary>
    /// Bambu answers every command on the report topic. Turns a failed answer into a readable message, with the
    /// remedy for Authorization Control rejections. Returns null for normal reports.
    /// </summary>
    public static string? DescribeRejection(JsonObject root)
    {
        foreach (var (section, node) in root)
        {
            if (node is not JsonObject obj) continue;
            var result = obj["result"] is JsonValue r && r.TryGetValue<string>(out var rs) ? rs : null;
            long errCode = 0;
            if (obj["err_code"] is JsonValue e)
                errCode = e.TryGetValue<long>(out var l) ? l : e.TryGetValue<string>(out var es) && long.TryParse(es, out l) ? l : 0;
            var failed = result is not null && result.StartsWith("fail", StringComparison.OrdinalIgnoreCase);
            if (!failed && errCode == 0) continue;

            var command = obj["command"] is JsonValue c && c.TryGetValue<string>(out var cs) ? cs : section;
            var reason = obj["reason"] is JsonValue rv && rv.TryGetValue<string>(out var rss) && rss.Length > 0 ? rss : null;
            var message = $"Printer rejected '{command}'" + (reason is null ? "" : $": {reason}") + (errCode != 0 ? $" (error {errCode})" : "") + ".";
            if (errCode == AuthorizationRejected || (reason ?? "").Contains("verify", StringComparison.OrdinalIgnoreCase))
                message += " The printer's Authorization Control only accepts control commands from Bambu's own software. " +
                           "To control it from Gcode Recovery, enable LAN Only Mode and then Developer Mode on the printer " +
                           "(printer screen: Settings → General / Network). Status and camera keep working without it.";
            return message;
        }
        return null;
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
