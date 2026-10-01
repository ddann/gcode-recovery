using System.Text.Json;

namespace GcodeRecovery.App;

/// <summary>Non-secret UI settings kept between sessions (the printer access code is never stored).</summary>
public sealed class AppSettings
{
    public int ConnectionType { get; set; }
    public string Host { get; set; } = "";
    public string Serial { get; set; } = "";
    public string CameraUrl { get; set; } = "";

    /// <summary>Anonymous statistics consent: null = not asked yet.</summary>
    public bool? ShareStatistics { get; set; }

    public bool CheckForUpdates { get; set; } = true;
    public string CommunityServer { get; set; } = GcodeRecovery.Telemetry.CommunityClient.DefaultServer;

    public static string DataDirectory => Path.GetDirectoryName(FilePath)!;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GcodeRecovery", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (IOException)
        {
            // Settings are a convenience only.
        }
    }
}
