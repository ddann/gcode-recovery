using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GcodeRecovery.Telemetry;

/// <summary>
/// Talks to the community server: update checks (always allowed unless disabled), and — only with the user's
/// consent — heartbeats ("app is open") and completed-job reports (grams of filament saved).
/// Job reports that cannot be delivered are queued on disk (anonymous fields only) and retried later.
/// </summary>
public sealed class CommunityClient : IDisposable
{
    public const string DefaultServer = "https://gcode-recovery.dachstar.app";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly string _queuePath;
    private readonly Guid _session = Guid.NewGuid(); // per launch, memory only
    private CancellationTokenSource? _heartbeat;

    public CommunityClient(string serverUrl, string queueDirectory, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(15);
        // No cookies, no custom identifiers; a generic agent so the server can't tell users apart by it.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GcodeRecovery");
        _queuePath = Path.Combine(queueDirectory, "pending-job-reports.json");
    }

    public static string AppVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    public static string Platform => Contract.NormalisePlatform(RuntimeInformation.RuntimeIdentifier);

    public Heartbeat CurrentHeartbeat => new(_session, AppVersion, Platform);

    public static JobReport CreateJobReport(string printerProfileId, string method, double gramsSaved) =>
        new(Guid.NewGuid(), AppVersion, Platform, Contract.NormalisePrinter(printerProfileId), method,
            (int)Math.Round(Math.Clamp(gramsSaved, 0, Contract.MaxGramsPerJob)));

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var url = $"v1/update?version={Uri.EscapeDataString(AppVersion)}&platform={Uri.EscapeDataString(Platform)}";
        return await _http.GetFromJsonAsync<UpdateInfo>(url, ct);
    }

    public Task<CommunityStats?> GetStatsAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<CommunityStats>("v1/stats", ct);

    /// <summary>Sends a heartbeat now and every 5 minutes until <see cref="StopHeartbeats"/>.</summary>
    public void StartHeartbeats()
    {
        if (_heartbeat is not null) return;
        _heartbeat = new CancellationTokenSource();
        var ct = _heartbeat.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var _ = await _http.PostAsJsonAsync("v1/heartbeat", CurrentHeartbeat, ct);
                    await FlushQueueAsync(ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Offline: try again next time.
                }
                try { await Task.Delay(HeartbeatInterval, ct); } catch (OperationCanceledException) { }
            }
        }, ct);
    }

    public void StopHeartbeats()
    {
        _heartbeat?.Cancel();
        _heartbeat = null;
    }

    /// <summary>Sends a completed-job report; if that fails it is queued and retried with the next heartbeat.</summary>
    public async Task ReportJobAsync(JobReport report, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("v1/jobs", report, ct);
            if (response.IsSuccessStatusCode || (int)response.StatusCode is >= 400 and < 500 and not 429) return;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }
        Enqueue(report);
    }

    public async Task FlushQueueAsync(CancellationToken ct = default)
    {
        var pending = LoadQueue();
        if (pending.Count == 0) return;
        var remaining = new List<JobReport>();
        foreach (var report in pending)
        {
            try
            {
                using var response = await _http.PostAsJsonAsync("v1/jobs", report, ct);
                if (!response.IsSuccessStatusCode && (int)response.StatusCode is >= 500 or 429) remaining.Add(report);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                remaining.Add(report);
            }
        }
        SaveQueue(remaining);
    }

    private void Enqueue(JobReport report)
    {
        var queue = LoadQueue();
        if (queue.Count < 100) queue.Add(report);
        SaveQueue(queue);
    }

    private List<JobReport> LoadQueue()
    {
        try
        {
            return File.Exists(_queuePath) ? JsonSerializer.Deserialize<List<JobReport>>(File.ReadAllText(_queuePath)) ?? [] : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void SaveQueue(List<JobReport> queue)
    {
        try
        {
            if (queue.Count == 0)
            {
                if (File.Exists(_queuePath)) File.Delete(_queuePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_queuePath)!);
            File.WriteAllText(_queuePath, JsonSerializer.Serialize(queue));
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        StopHeartbeats();
        _http.Dispose();
    }
}
