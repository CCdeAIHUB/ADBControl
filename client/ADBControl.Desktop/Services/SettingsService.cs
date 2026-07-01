using System.Text.Json;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public AppSettings Current { get; private set; } = new();

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ADBControl");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
    }

    public void Load()
    {
        if (!File.Exists(_settingsPath))
        {
            Current = new AppSettings();
            return;
        }

        var json = File.ReadAllText(_settingsPath);
        Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}
