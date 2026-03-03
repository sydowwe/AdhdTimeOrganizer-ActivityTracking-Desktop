using System.Text.Json;

namespace DesktopActivityTracker.Models;

public sealed class AppConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopActivityTracker",
        "config.json");

    public string ApiBaseUrl { get; set; } = "https://localhost:8080";
    public int PollIntervalMs { get; set; } = 2000;
    public int IdleThresholdSeconds { get; set; } = 30;
    public bool AutoStart { get; set; } = true;
    public string? RefreshToken { get; set; }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch
        {
            // Fall through to default
        }

        return new AppConfig();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
