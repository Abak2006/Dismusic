using Dismusic.Core.Settings;

namespace Dismusic.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dismusic-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var settings = new SettingsStore(FilePath).Load(out var warning);
        Assert.Null(warning);
        Assert.True(settings.ScreenFollowEnabled);
        Assert.Equal(MirrorSettings.DefaultDeviceSource, settings.Mirror.Source);
    }

    [Fact]
    public void RoundTripsSettings()
    {
        var store = new SettingsStore(FilePath);
        var settings = new AppSettings();
        settings.AppRoutes["spotify.exe"] = "dev1";
        settings.SetFollowing("chrome.exe", true);
        settings.MonitorDevices["mon"] = "dev2";
        settings.Mirror.Targets.Add(new MirrorTargetSettings { DeviceId = "dev3", Enabled = true, DelayMs = 120, Volume = 0.5f });
        store.Save(settings);

        var loaded = store.Load(out _);
        Assert.Equal("dev1", loaded.AppRoutes["spotify.exe"]);
        Assert.True(loaded.IsFollowing("CHROME.EXE"));
        Assert.Equal("dev2", loaded.MonitorDevices["mon"]);
        Assert.Equal(120, loaded.Mirror.Targets.Single().DelayMs);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void CorruptFileIsMovedAsideAndDefaultsReturned()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var settings = new SettingsStore(FilePath).Load(out var warning);
        Assert.NotNull(warning);
        Assert.Empty(settings.AppRoutes);
        Assert.False(File.Exists(FilePath));
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void NormalizesHandEditedValues()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """
        {
          "AppRoutes": { "Spotify.EXE": "dev1", "": "x", "vlc.exe": "" },
          "FollowScreenApps": [ "Chrome.exe", "chrome.exe", " " ],
          "Mirror": { "Source": "bogus", "Targets": [ { "DeviceId": "d", "DelayMs": 99999, "Volume": 7 }, { "DeviceId": "" } ] }
        }
        """);
        var s = new SettingsStore(FilePath).Load(out _);
        Assert.Equal(new[] { "spotify.exe" }, s.AppRoutes.Keys);
        Assert.Equal(new[] { "chrome.exe" }, s.FollowScreenApps);
        Assert.Equal(MirrorSettings.DefaultDeviceSource, s.Mirror.Source);
        var t = Assert.Single(s.Mirror.Targets);
        Assert.Equal(MirrorTargetSettings.MaxDelayMs, t.DelayMs);
        Assert.Equal(1f, t.Volume);
    }

    [Fact]
    public void FollowToggleIsIdempotent()
    {
        var s = new AppSettings();
        s.SetFollowing("App.exe", true);
        s.SetFollowing("app.exe", true);
        Assert.Single(s.FollowScreenApps);
        s.SetFollowing("APP.EXE", false);
        Assert.Empty(s.FollowScreenApps);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
