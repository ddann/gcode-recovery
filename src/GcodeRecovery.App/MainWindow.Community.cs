using System.Diagnostics;
using System.Text.Json;
using GcodeRecovery.Telemetry;

namespace GcodeRecovery.App;

/// <summary>
/// Update checks and anonymous community statistics. What can be sent is fixed by GcodeRecovery.Telemetry's
/// contract (version, platform, printer family, grams saved, random ids); G-code and file names never leave the app.
/// </summary>
public partial class MainWindow
{
    private CommunityClient? _community;
    private UpdateInfo? _update;
    private CompletionTracker? _uploadTracker;

    private const string PrivacyExplanation =
        "What is sent, and only with the box above ticked: (1) every 5 minutes while the app is open: a random number created " +
        "at start-up and forgotten when the app closes, the app version and platform; (2) when a recovery has finished: a random " +
        "job number, the app version, platform, printer family (bambu-p1s / snapmaker-u1 / custom), how it ran (stream / " +
        "upload) and the whole grams of filament saved. Never sent: G-code, file names, paths, printer IP, serial or access " +
        "code, positions, layer data. The server stores no IP addresses and keeps only the date (no time) of each job. " +
        "Update checks send only the app version and platform.";

    private async Task InitCommunityAsync()
    {
        var settings = AppSettings.Load();
        PrivacyText.Text = PrivacyExplanation;
        ServerUrlBox.Text = settings.CommunityServer;
        CheckUpdatesBox.IsChecked = settings.CheckForUpdates;

        if (settings.ShareStatistics is null)
        {
            var example = JsonSerializer.Serialize(CommunityClient.CreateJobReport("snapmaker-u1", "stream", 412),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            settings.ShareStatistics = await ConfirmDialog.AskAsync(this, "Anonymous statistics",
                "May Gcode Recovery count, anonymously, how much filament it saves and how many people use it right now?\n\n" +
                "Only finished recoveries are counted. Example of everything a finished job sends:\n\n" + example + "\n\n" +
                "No G-code, file names, printer addresses or identifiers are ever sent. You can change this in Settings.",
                "Share anonymously", "No thanks");
            settings.Save();
        }
        ShareStatsBox.IsChecked = settings.ShareStatistics == true;

        ShareStatsBox.IsCheckedChanged += (_, _) => SaveCommunitySettings();
        CheckUpdatesBox.IsCheckedChanged += (_, _) => SaveCommunitySettings();
        ServerUrlBox.LostFocus += (_, _) => SaveCommunitySettings();
        CheckUpdateNowButton.Click += async (_, _) => await CheckForUpdateAsync(userInitiated: true);
        UpdateLaterButton.Click += (_, _) => UpdateBanner.IsVisible = false;
        UpdateDownloadButton.Click += (_, _) => OpenUrl(_update?.DownloadUrl ?? _update?.ReleaseUrl);
        UpdateNotesButton.Click += (_, _) => OpenUrl(_update?.ReleaseUrl);

        RestartCommunityClient();
        if (settings.CheckForUpdates) await CheckForUpdateAsync(userInitiated: false);
        await RefreshCommunityStatsAsync();
    }

    private void SaveCommunitySettings()
    {
        var s = AppSettings.Load();
        s.ShareStatistics = ShareStatsBox.IsChecked == true;
        s.CheckForUpdates = CheckUpdatesBox.IsChecked == true;
        var url = ServerUrlBox.Text?.Trim();
        s.CommunityServer = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? url! : CommunityClient.DefaultServer;
        s.Save();
        RestartCommunityClient();
    }

    private void RestartCommunityClient()
    {
        _community?.Dispose();
        var s = AppSettings.Load();
        _community = new CommunityClient(s.CommunityServer, AppSettings.DataDirectory);
        if (s.ShareStatistics == true) _community.StartHeartbeats();
    }

    private async Task CheckForUpdateAsync(bool userInitiated)
    {
        if (_community is null) return;
        try
        {
            _update = await _community.CheckForUpdateAsync();
            if (_update is { UpdateAvailable: true })
            {
                UpdateText.Text = $"Gcode Recovery {_update.Latest} is available (you have {CommunityClient.AppVersion}).";
                UpdateBanner.IsVisible = true;
            }
            else if (userInitiated)
            {
                Log($"Gcode Recovery {CommunityClient.AppVersion} is up to date.");
            }
        }
        catch (Exception ex)
        {
            if (userInitiated) Log("Update check failed: " + ex.Message);
        }
    }

    private async Task RefreshCommunityStatsAsync()
    {
        if (_community is null) return;
        try
        {
            if (await _community.GetStatsAsync() is { } s)
                CommunityStatsText.Text =
                    $"Community: {s.GramsSaved / 1000.0:0.0} kg of filament saved in {s.CompletedJobs} recovered prints · {s.ActiveNow} using it right now";
        }
        catch (Exception)
        {
            CommunityStatsText.Text = "Community statistics are not reachable right now.";
        }
    }

    /// <summary>Called when a recovery job has run to completion (never for touch tests or dry runs).</summary>
    private void ReportCompletedJob(string method, string printerProfileId, double gramsSaved)
    {
        if (_community is null || AppSettings.Load().ShareStatistics != true) return;
        var report = CommunityClient.CreateJobReport(printerProfileId, method, gramsSaved);
        _ = Task.Run(async () =>
        {
            await _community.ReportJobAsync(report);
            OnUi(() => _ = RefreshCommunityStatsAsync());
        });
        Log($"Recovery finished: {gramsSaved:0} g of filament saved{(AppSettings.Load().ShareStatistics == true ? " (counted anonymously)" : "")}.");
    }

    /// <summary>Starts watching printer status for the uploaded recovery file to finish.</summary>
    private void TrackUploadedJob(string fileName, string printerProfileId, double gramsSaved) =>
        _uploadTracker = new CompletionTracker(fileName, () => OnUi(() => ReportCompletedJob("upload", printerProfileId, gramsSaved)));

    private static void OpenUrl(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") return;
        Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true });
    }
}
