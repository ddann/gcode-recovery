using System.Text.Json.Nodes;
using GcodeRecovery.Telemetry;

namespace GcodeRecovery.Server;

/// <summary>Latest GitHub release of the app, cached so the GitHub API is asked at most every 30 minutes.</summary>
public sealed class ReleaseFeed(HttpClient http, string repo, ILogger<ReleaseFeed> log)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private JsonObject? _release;
    private DateTime _fetched = DateTime.MinValue;

    /// <summary>Asset name suffix per platform, matching scripts/package-macos.sh and the CI artifacts.</summary>
    private static readonly Dictionary<string, string> AssetSuffix = new()
    {
        ["osx-arm64"] = "-macos-arm64.dmg",
        ["osx-x64"] = "-macos-x64.dmg",
        ["win-x64"] = "-windows-x64.zip",
        ["linux-x64"] = "-linux-x64.tar.gz",
    };

    public async Task<UpdateInfo> CheckAsync(Version current, string platform, CancellationToken ct)
    {
        var release = await GetLatestAsync(ct);
        if (release is null) return new UpdateInfo(current.ToString(3), false, null, null, null);

        var tag = release["tag_name"]?.GetValue<string>() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return new UpdateInfo(current.ToString(3), false, null, null, null);

        string? download = null;
        if (AssetSuffix.TryGetValue(platform, out var suffix) && release["assets"] is JsonArray assets)
            download = assets.OfType<JsonObject>()
                .Where(a => a["name"]?.GetValue<string>()?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true)
                .Select(a => a["browser_download_url"]?.GetValue<string>())
                .FirstOrDefault();

        var notes = release["body"]?.GetValue<string>();
        if (notes is { Length: > 2000 }) notes = notes[..2000] + "…";
        return new UpdateInfo(
            latest.ToString(3),
            latest > current,
            download,
            release["html_url"]?.GetValue<string>(),
            notes);
    }

    private async Task<JsonObject?> GetLatestAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _fetched < CacheFor) return _release;
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _fetched < CacheFor) return _release;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd("gcode-recovery-community-server");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                _release = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
            else
                log.LogWarning("GitHub release lookup failed: {Status}", (int)response.StatusCode);
            _fetched = DateTime.UtcNow;
            return _release;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("GitHub release lookup failed: {Message}", ex.Message);
            _fetched = DateTime.UtcNow - CacheFor + TimeSpan.FromMinutes(2); // retry soon
            return _release;
        }
        finally
        {
            _lock.Release();
        }
    }
}
