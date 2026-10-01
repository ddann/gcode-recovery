using System.Text.Json;

namespace GcodeRecovery.App;

/// <summary>Non-secret UI settings kept between sessions (the printer access code is never stored).</summary>
public sealed class AppSettings
{
    public int ConnectionType { get; set; }
    public string Host { get; set; } = "";
    public string Serial { get; set; } = "";
    public string CameraUrl { get; set; } = "";

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
