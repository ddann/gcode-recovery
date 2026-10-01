using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace GcodeRecovery.Printers;

public sealed partial class MoonrakerConnection
{
    public async Task<bool> NeedsZOverrideSetupAsync(IEnumerable<string> program, CancellationToken ct = default)
    {
        var usesOverride = program.Any(l => l.TrimStart().StartsWith("SET_KINEMATIC_POSITION", StringComparison.OrdinalIgnoreCase));
        return usesOverride && !await IsForceMoveEnabledAsync(ct);
    }

    public async Task EnableZOverrideAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (await IsForceMoveEnabledAsync(ct))
        {
            progress?.Report("force_move is already enabled.");
            return;
        }

        progress?.Report("Reading printer.cfg…");
        var cfg = await DownloadConfigAsync("printer.cfg", ct);

        progress?.Report("Backing up printer.cfg → printer.cfg.gcode-recovery.bak");
        await UploadConfigAsync("", "printer.cfg.gcode-recovery.bak", cfg, ct);

        progress?.Report($"Writing {KlipperConfigEditor.IncludePath}");
        await UploadConfigAsync("custom", "gcode_recovery.cfg", KlipperConfigEditor.IncludeContent + "\n", ct);

        if (!KlipperConfigEditor.HasInclude(cfg))
        {
            progress?.Report("Adding the include line to printer.cfg");
            await UploadConfigAsync("", "printer.cfg", KlipperConfigEditor.AddInclude(cfg), ct);
        }

        progress?.Report("Restarting Klipper (FIRMWARE_RESTART)…");
        await Post("printer/firmware_restart", ct);
        await WaitForReadyAsync(TimeSpan.FromSeconds(90), ct);

        if (!await IsForceMoveEnabledAsync(ct))
            throw new InvalidOperationException(
                "Klipper restarted but force_move is still disabled. Check printer.cfg in Fluidd (the U1 may need advanced mode " +
                $"to allow config edits) and add:\n[force_move]\nenable_force_move: True");
        progress?.Report("force_move enabled: Z can now be set without homing.");
    }

    /// <summary>Moonraker reports per-root permissions; the Snapmaker U1 exposes "config" read-only unless advanced mode is on.</summary>
    public async Task<bool> CanEnableZOverrideAsync(CancellationToken ct = default)
    {
        var roots = await GetJson("server/files/roots", ct);
        return roots?["result"] is JsonArray list && list.OfType<JsonObject>().Any(r =>
            r["name"]?.GetValue<string>() == "config" && (r["permissions"]?.GetValue<string>() ?? "").Contains('w'));
    }

    private async Task<bool> IsForceMoveEnabledAsync(CancellationToken ct)
    {
        var json = await GetJson("printer/objects/query?configfile=settings", ct);
        var setting = json?["result"]?["status"]?["configfile"]?["settings"]?["force_move"]?["enable_force_move"];
        return setting is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> DownloadConfigAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(_base, "server/files/config/" + path), ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Could not read {path} from the printer ({(int)response.StatusCode}).");
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task UploadConfigAsync(string folder, string fileName, string text, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("config"), "root");
        if (folder.Length > 0) content.Add(new StringContent(folder), "path");
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        using var response = await _http.PostAsync(new Uri(_base, "server/files/upload"), content, ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Could not write {(folder.Length > 0 ? folder + "/" : "")}{fileName} ({(int)response.StatusCode}): " +
                                  await response.Content.ReadAsStringAsync(ct));
    }

    private async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        await Task.Delay(2000, ct);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var info = await GetJson("printer/info", ct);
                if (info?["result"]?["state"]?.GetValue<string>() == "ready") return;
            }
            catch (HttpRequestException)
            {
                // Klipper is restarting.
            }
            catch (IOException)
            {
            }
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException("Klipper did not report 'ready' after the restart.");
    }
}
