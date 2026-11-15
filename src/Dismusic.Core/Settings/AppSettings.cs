using System.Text.Json;

namespace Dismusic.Core.Settings;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    /// <summary>Fixed output per app: lower-case exe name → MMDevice id.</summary>
    public Dictionary<string, string> AppRoutes { get; set; } = new();

    /// <summary>Apps (lower-case exe names) whose output follows the screen their window is on.</summary>
    public List<string> FollowScreenApps { get; set; } = new();

    /// <summary>Global switch for screen-follow routing.</summary>
    public bool ScreenFollowEnabled { get; set; } = true;

    /// <summary>Monitor key (stable monitor device path) → MMDevice id.</summary>
    public Dictionary<string, string> MonitorDevices { get; set; } = new();

    /// <summary>Last known names, so rules for disconnected devices/apps are still readable.</summary>
    public Dictionary<string, string> KnownDeviceNames { get; set; } = new();
    public Dictionary<string, string> KnownAppNames { get; set; } = new();

    public MirrorSettings Mirror { get; set; } = new();

    public bool StartMinimized { get; set; }

    public bool IsFollowing(string exeKey) => FollowScreenApps.Contains(exeKey, StringComparer.OrdinalIgnoreCase);

    public void SetFollowing(string exeKey, bool follow)
    {
        FollowScreenApps.RemoveAll(e => string.Equals(e, exeKey, StringComparison.OrdinalIgnoreCase));
        if (follow) FollowScreenApps.Add(exeKey.ToLowerInvariant());
    }

    /// <summary>Lower-cases exe keys and drops empty entries after loading from disk.</summary>
    internal void Normalize()
    {
        AppRoutes = (AppRoutes ?? new())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .GroupBy(kv => kv.Key.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Last().Value);
        FollowScreenApps = (FollowScreenApps ?? new())
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.ToLowerInvariant()).Distinct().ToList();
        MonitorDevices ??= new();
        KnownDeviceNames ??= new();
        KnownAppNames ??= new();
        Mirror ??= new();
        Mirror.Targets ??= new();
        foreach (var t in Mirror.Targets)
        {
            t.DelayMs = Math.Clamp(t.DelayMs, 0, MirrorTargetSettings.MaxDelayMs);
            t.Volume = Math.Clamp(t.Volume, 0f, 1f);
        }
        Mirror.Targets.RemoveAll(t => string.IsNullOrWhiteSpace(t.DeviceId));
        if (string.IsNullOrWhiteSpace(Mirror.Source) ||
            !(Mirror.Source == MirrorSettings.AllAppsSource || Mirror.Source.StartsWith(MirrorSettings.DevicePrefix) ||
              Mirror.Source.StartsWith(MirrorSettings.AppPrefix)))
            Mirror.Source = MirrorSettings.DefaultDeviceSource;
    }
}

public sealed class MirrorSettings
{
    /// <summary>Everything playing on whatever the current Windows default device is.</summary>
    public const string DefaultDeviceSource = "device:*default*";
    /// <summary>Every app except Dismusic (process loopback, exclude mode).</summary>
    public const string AllAppsSource = "allapps";
    public const string DevicePrefix = "device:";
    public const string AppPrefix = "app:";

    /// <summary>"device:&lt;id&gt;", <see cref="DefaultDeviceSource"/>, <see cref="AllAppsSource"/> or "app:&lt;exe&gt;".</summary>
    public string Source { get; set; } = DefaultDeviceSource;

    /// <summary>Whether multi-output was running when the app last closed (it resumes on launch).</summary>
    public bool Enabled { get; set; }

    public List<MirrorTargetSettings> Targets { get; set; } = new();
}

public sealed class MirrorTargetSettings
{
    public const int MaxDelayMs = 1000;

    public string DeviceId { get; set; } = "";
    public bool Enabled { get; set; }
    public int DelayMs { get; set; }
    public float Volume { get; set; } = 1f;
}

public sealed class SettingsStore(string? path = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string FilePath { get; } = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dismusic", "settings.json");

    /// <summary>Loads settings. A corrupt file is moved aside and defaults are returned with a warning.</summary>
    public AppSettings Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (JsonException ex)
        {
            var backup = FilePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(FilePath, backup, overwrite: true);
            warning = $"Your settings file could not be read and was reset. The old file was kept as {Path.GetFileName(backup)}.";
            Log.Error($"Settings file corrupt, moved to {backup}", ex);
            return new AppSettings();
        }
    }

    /// <summary>Writes atomically (temp file + move) so a crash never leaves a half-written file.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}
