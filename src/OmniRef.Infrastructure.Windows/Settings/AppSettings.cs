using System.Text.Json;
using System.IO;

namespace OmniRef.Infrastructure.Windows.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string Language { get; set; } = "auto";
    public bool AlwaysOnTop { get; set; }
    public bool ShowCanvasGrid { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool LastExitClean { get; set; } = true;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    public List<string> OpenWorkspacePaths { get; set; } = [];
    public List<string> RecentWorkspacePaths { get; set; } = [];
    public int ActiveWorkspaceIndex { get; set; }
}

public sealed class AppSettingsStore
{
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public AppSettingsStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmniRef");
        _settingsPath = Path.Combine(RootDirectory, "settings.json");
    }

    public string RootDirectory { get; }
    public string RecoveryDirectory => Path.Combine(RootDirectory, "Recovery");
    public string CacheDirectory => Path.Combine(RootDirectory, "Cache");
    public string LogDirectory => Path.Combine(RootDirectory, "Logs");

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, _options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            var corruptPath = _settingsPath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(_settingsPath, corruptPath, overwrite: false);
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(RootDirectory);
        var temporaryPath = _settingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, _options);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }
}
