using System.Text.RegularExpressions;

namespace GcodeRecovery.Telemetry;

/// <summary>
/// Everything the app can ever send to the community server. These records are the whole contract: there is no
/// field for G-code, file names, paths, positions, printer addresses, serial numbers or user identifiers.
/// The server validates every field against the allow-lists below and rejects anything else.
/// </summary>
public static partial class Contract
{
    public static readonly string[] Platforms = ["osx-arm64", "osx-x64", "win-x64", "win-arm64", "linux-x64", "linux-arm64", "other"];
    public static readonly string[] Printers = ["bambu-p1s", "snapmaker-u1", "custom"];
    public static readonly string[] Methods = ["stream", "upload"];

    public const double MaxGramsPerJob = 20000;

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}$")]
    public static partial Regex VersionPattern();

    public static string NormalisePlatform(string rid) => Platforms.Contains(rid) ? rid : "other";
    public static string NormalisePrinter(string profileId) => Printers.Contains(profileId) ? profileId : "custom";
}

/// <summary>
/// "This app is open right now." <paramref name="Session"/> is a random value created when the app starts, kept
/// only in memory and never written to disk, so it cannot follow a user across launches.
/// </summary>
public sealed record Heartbeat(Guid Session, string Version, string Platform);

/// <summary>
/// One recovery job that ran to completion. <paramref name="Job"/> is a random value created for this job only and
/// used by the server to ignore duplicates. <paramref name="GramsSaved"/> is rounded to whole grams.
/// </summary>
public sealed record JobReport(Guid Job, string Version, string Platform, string Printer, string Method, int GramsSaved);

public sealed record UpdateInfo(string Latest, bool UpdateAvailable, string? DownloadUrl, string? ReleaseUrl, string? Notes);

public sealed record CommunityStats(int ActiveNow, int CompletedJobs, double GramsSaved, string Since);
