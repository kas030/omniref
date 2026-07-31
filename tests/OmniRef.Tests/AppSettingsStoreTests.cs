using OmniRef.Infrastructure.Windows.Settings;

namespace OmniRef.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OmniRef.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadWithoutSettings_DefaultsCanvasGridAndSnappingToOff()
    {
        var store = new AppSettingsStore(_directory);

        var settings = store.Load();

        Assert.False(settings.ShowCanvasGrid);
        Assert.False(settings.SnapToGrid);
    }

    [Fact]
    public void SaveAndLoad_PreservesCanvasGridSettings()
    {
        var store = new AppSettingsStore(_directory);
        var settings = new AppSettings
        {
            ShowCanvasGrid = true,
            SnapToGrid = true
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.True(loaded.ShowCanvasGrid);
        Assert.True(loaded.SnapToGrid);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
