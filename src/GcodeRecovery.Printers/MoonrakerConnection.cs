using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace GcodeRecovery.Printers;

/// <summary>
/// Klipper printers through the Moonraker HTTP API (Snapmaker U1 runs a Moonraker fork).
/// Status is polled once per second; the camera is read from the MJPEG stream.
/// </summary>
public sealed class MoonrakerConnection(ConnectionSettings settings) : IPrinterConnection
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? _poll;
    private Uri _base = new("http://localhost");

    public string Name => $"Moonraker @ {settings.Host}";
    public bool IsConnected { get; private set; }
    public bool SendWaitsForExecution => true;
    public PrinterStatus Status { get; private set; } = new();

    public event EventHandler<PrinterStatus>? StatusChanged;
    public event EventHandler<string>? Log;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        PrinterCommands.ValidateHost(settings.Host);
        _base = new Uri($"http://{settings.Host.Trim()}:{(settings.Port > 0 ? settings.Port : 7125)}/");
        if (!string.IsNullOrWhiteSpace(settings.Secret)) _http.DefaultRequestHeaders.Add("X-Api-Key", settings.Secret.Trim());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var info = await GetJson("printer/info", timeout.Token);
        IsConnected = true;
        Log?.Invoke(this, $"Connected: Klipper state '{info?["result"]?["state"]}'.");

        _poll = new CancellationTokenSource();
        _ = PollLoop(_poll.Token);
    }

    public Task DisconnectAsync()
    {
        _poll?.Cancel();
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task SendGcodeAsync(IEnumerable<string> lines, CancellationToken ct = default) =>
        Post("printer/gcode/script?script=" + Uri.EscapeDataString(string.Join("\n", lines.Select(PrinterCommands.Sanitize))), ct);

    public Task PauseAsync() => Post("printer/print/pause");
    public Task ResumeAsync() => Post("printer/print/resume");
    public Task StopAsync() => Post("printer/print/cancel");

    // Klipper has no standard light command; the U1 exposes its LED as an output pin in many configs.
    public Task SetLightAsync(bool on) => SendGcodeAsync([on ? "SET_LED LED=cavity_led WHITE=1" : "SET_LED LED=cavity_led WHITE=0"]);

    public async Task UploadAndStartAsync(string localPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(localPath).Replace(' ', '_');
        await using var file = File.OpenRead(localPath);
        using var content = new MultipartFormDataContent();
        var stream = new StreamContent(file);
        stream.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(stream, "file", fileName);
        progress?.Report(0);
        using var response = await _http.PostAsync(new Uri(_base, "server/files/upload"), content, ct);
        response.EnsureSuccessStatusCode();
        progress?.Report(100);
        Log?.Invoke(this, $"Uploaded {fileName}.");
        await Post("printer/print/start?filename=" + Uri.EscapeDataString(fileName), ct);
        Log?.Invoke(this, $"Started {fileName}.");
    }

    public async Task StreamCameraAsync(Action<byte[]> onJpegFrame, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(settings.CameraUrl)
            ? new Uri($"http://{settings.Host.Trim()}/webcam/?action=stream")
            : new Uri(settings.CameraUrl);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await JpegStreamReader.ReadFramesAsync(stream, onJpegFrame, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _http.Dispose();
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var json = await GetJson("printer/objects/query?extruder&heater_bed&print_stats&display_status&virtual_sdcard", ct);
                var s = json?["result"]?["status"];
                Status = new PrinterStatus
                {
                    State = s?["print_stats"]?["state"]?.GetValue<string>() ?? "unknown",
                    NozzleTemp = D(s?["extruder"]?["temperature"]),
                    NozzleTarget = D(s?["extruder"]?["target"]),
                    BedTemp = D(s?["heater_bed"]?["temperature"]),
                    BedTarget = D(s?["heater_bed"]?["target"]),
                    ProgressPercent = D(s?["display_status"]?["progress"]) * 100,
                    Layer = (int?)D(s?["print_stats"]?["info"]?["current_layer"]),
                    TotalLayers = (int?)D(s?["print_stats"]?["info"]?["total_layer"]),
                    FileName = s?["print_stats"]?["filename"]?.GetValue<string>(),
                };
                StatusChanged?.Invoke(this, Status);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log?.Invoke(this, "Status poll failed: " + ex.Message); }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static double? D(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private async Task<JsonNode?> GetJson(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(_base, path), ct);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task Post(string path, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync(new Uri(_base, path), null, ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Moonraker returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }
}

/// <summary>Extracts complete JPEG images (FFD8 … FFD9) from an MJPEG byte stream.</summary>
public static class JpegStreamReader
{
    public static async Task ReadFramesAsync(Stream stream, Action<byte[]> onFrame, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var frame = new MemoryStream();
        var inFrame = false;
        byte prev = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (!inFrame)
                {
                    if (prev == 0xFF && b == 0xD8)
                    {
                        inFrame = true;
                        frame.SetLength(0);
                        frame.WriteByte(0xFF);
                        frame.WriteByte(0xD8);
                    }
                }
                else
                {
                    frame.WriteByte(b);
                    if (prev == 0xFF && b == 0xD9)
                    {
                        onFrame(frame.ToArray());
                        inFrame = false;
                    }
                    else if (frame.Length > 16 * 1024 * 1024)
                    {
                        inFrame = false; // corrupt stream, resynchronise
                    }
                }
                prev = b;
            }
        }
    }
}
