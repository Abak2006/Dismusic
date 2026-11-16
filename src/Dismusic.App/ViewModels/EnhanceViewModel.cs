using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Dismusic.Core;
using Dismusic.Core.Effects;
using Dismusic.Core.Enhance;
using Dismusic.Core.Routing;
using Dismusic.Core.Sessions;
using Dismusic.Core.Settings;

namespace Dismusic.App.ViewModels;

/// <summary>
/// Drives per-app enhancement: keeps enhanced apps' Windows volume at the attenuated level, starts/stops
/// <see cref="EnhancerEngine"/> pipelines, restores original volumes, and manages presets.
/// </summary>
public sealed class EnhanceViewModel : ObservableObject, IDisposable
{
    public const string CustomPresetKey = "";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly AudioSystem _audio;
    private readonly AppSettings _settings;
    private readonly Action _scheduleSave;
    private readonly Action _saveNow;
    private readonly Dispatcher _dispatcher;
    private readonly EnhancerEngine _engine = new();
    private readonly DispatcherTimer _enforceTimer;
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<uint>> _trees = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> _self = [(uint)Environment.ProcessId];
    private RoutingSnapshot? _snapshot;
    private EnhanceAppViewModel? _selected;
    private string? _statusText;
    private string _newPresetName = "";
    private bool _suppress;
    private bool _disposed;

    public EnhanceViewModel(AudioSystem audio, AppSettings settings, Action scheduleSave, Action saveNow)
    {
        _audio = audio;
        _settings = settings;
        _scheduleSave = scheduleSave;
        _saveNow = saveNow;
        _dispatcher = Dispatcher.CurrentDispatcher;

        SavePresetCommand = new RelayCommand(SavePreset, () => Selected is not null);
        DeletePresetCommand = new RelayCommand(DeletePreset, () => Selected is not null && IsCustomPreset(Selected.PresetKey));
        ResetCommand = new RelayCommand(() => { if (Selected is not null) Selected.PresetKey = "Flat"; }, () => Selected is not null);

        _engine.StatusChanged += (exe, error) => _dispatcher.BeginInvoke(() =>
        {
            if (error is null) _errors.Remove(exe);
            else
            {
                _errors[exe] = error;
                _ = StopAsync(exe, restore: true);
            }
            UpdateRowStatuses();
        });
        // Un-attenuated audio reached the enhancer: re-apply the volume right away rather than at the next tick.
        _engine.GuardTripped += _ => _dispatcher.BeginInvoke(Enforce);

        _enforceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => Enforce(), _dispatcher);
        _enforceTimer.Start();
        RebuildPresetOptions();
    }

    public ObservableCollection<EnhanceAppViewModel> Apps { get; } = new();
    public ObservableCollection<Option> Presets { get; } = new();
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public RelayCommand ResetCommand { get; }

    public bool HasApps => Apps.Count > 0;
    public bool HasSelection => Selected is not null;

    /// <summary>Dismusic's own Windows per-app volume, refreshed while anything is enhanced.</summary>
    public float OwnSessionVolume { get; private set; } = 1f;

    public EnhanceAppViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection));
            if (!_suppress && value is not null)
            {
                _settings.LastEnhanceApp = value.ExeKey;
                _scheduleSave();
            }
            SavePresetCommand.RaiseCanExecuteChanged();
            DeletePresetCommand.RaiseCanExecuteChanged();
            ResetCommand.RaiseCanExecuteChanged();
        }
    }

    public string? StatusText
    {
        get => _statusText;
        private set { if (Set(ref _statusText, value)) Raise(nameof(IsStatusOpen)); }
    }

    public bool IsStatusOpen
    {
        get => StatusText is not null;
        set { if (!value) StatusText = null; }
    }
    public string NewPresetName { get => _newPresetName; set => Set(ref _newPresetName, value); }

    public bool IsEnhancing(string exe) => _engine.IsRunning(exe);

    // ---------- Snapshot handling (every 2 s, from MainViewModel) ----------

    public void OnSnapshot(RoutingSnapshot snapshot)
    {
        if (_disposed) return;
        _snapshot = snapshot;
        SyncRows(snapshot);
        Reconcile();
        UpdateRowStatuses();
    }

    private void SyncRows(RoutingSnapshot snapshot)
    {
        var exes = snapshot.Apps.Where(a => a.HasSession).Select(a => a.ExeKey)
            .Concat(_settings.Enhance.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var desired = exes.Select(exe => Apps.FirstOrDefault(r => r.ExeKey == exe) ?? new EnhanceAppViewModel(exe, this)).ToList();
        desired.Sort((a, b) =>
        {
            int c = IsPlaying(b.ExeKey).CompareTo(IsPlaying(a.ExeKey));
            return c != 0 ? c : string.Compare(NameOf(a.ExeKey), NameOf(b.ExeKey), StringComparison.CurrentCultureIgnoreCase);
        });

        _suppress = true;
        try
        {
            CollectionSync.Sync(Apps, desired, r => r.ExeKey);
            foreach (var row in Apps)
            {
                row.DisplayName = NameOf(row.ExeKey);
                var cfg = _settings.Enhance.GetValueOrDefault(row.ExeKey);
                row.Load(cfg?.Enabled ?? false, PresetKeyFor(cfg), cfg?.ToEffects() ?? LastEffects);
            }
            if (Selected is null || !Apps.Contains(Selected))
                Selected = Apps.FirstOrDefault(r => string.Equals(r.ExeKey, _settings.LastEnhanceApp, StringComparison.OrdinalIgnoreCase))
                           ?? Apps.FirstOrDefault();
        }
        finally
        {
            _suppress = false;
        }
        Raise(nameof(HasApps));
    }

    private bool IsPlaying(string exe) => _snapshot?.Apps.Any(a => a.ExeKey == exe && a.HasSession) == true;

    private string NameOf(string exe) =>
        _snapshot?.Apps.FirstOrDefault(a => a.ExeKey == exe)?.DisplayName
        ?? _settings.KnownAppNames.GetValueOrDefault(exe) ?? Path.GetFileNameWithoutExtension(exe);

    /// <summary>Starts, restarts or stops pipelines so they match settings and what is actually playing.</summary>
    private void Reconcile()
    {
        var snapshot = _snapshot;
        if (snapshot is null || _disposed) return;

        foreach (var app in snapshot.Apps)
        {
            if (_busy.Contains(app.ExeKey)) continue;
            var cfg = _settings.Enhance.GetValueOrDefault(app.ExeKey);
            bool want = cfg?.Enabled == true && app.HasSession && app.SessionDeviceId is not null;
            var running = _engine.TargetOf(app.ExeKey);

            if (want)
            {
                uint root = snapshot.Processes.ResolveRootOfSameExe(app.SessionPids[0]);
                var tree = snapshot.Processes.TreeOf(root);
                tree.UnionWith(app.SessionPids);
                tree.ExceptWith(_self); // never attenuate Dismusic itself
                _trees[app.ExeKey] = tree;

                if (running is null)
                {
                    if (_retryAfter.TryGetValue(app.ExeKey, out var after) && DateTime.UtcNow < after) continue;
                    _ = StartAsync(app.ExeKey, root, app.SessionDeviceId!, tree, cfg!.ToEffects());
                }
                else if (running.DeviceId != app.SessionDeviceId || running.RootProcessId != root)
                {
                    _ = RestartAsync(app.ExeKey, root, app.SessionDeviceId!, tree, cfg!.ToEffects());
                }
            }
            else if (running is not null)
            {
                // Disabled, or the app stopped having a session: stop. Restore volume only if the user turned it off.
                _ = StopAsync(app.ExeKey, restore: cfg?.Enabled != true);
            }
        }

        // Pipelines for apps that exited.
        foreach (var exe in _engine.RunningApps)
            if (!_busy.Contains(exe) && snapshot.Apps.All(a => a.ExeKey != exe || !a.HasSession))
                _ = StopAsync(exe, restore: false);

        // Volumes left turned down (crash, or app was closed while enhanced) for apps no longer enhanced.
        foreach (var exe in _settings.SavedAppVolumes.Keys.ToList())
        {
            if (_busy.Contains(exe) || _engine.IsRunning(exe) || _settings.Enhance.GetValueOrDefault(exe)?.Enabled == true) continue;
            var app = snapshot.Apps.FirstOrDefault(a => a.ExeKey == exe && a.HasSession);
            if (app is null) continue;
            var tree = snapshot.Processes.TreeOf(snapshot.Processes.ResolveRootOfSameExe(app.SessionPids[0]));
            tree.UnionWith(app.SessionPids);
            tree.ExceptWith(_self);
            RestoreVolume(exe, tree);
        }
    }

    private async Task StartAsync(string exe, uint root, string deviceId, HashSet<uint> tree, EffectSettings effects)
    {
        if (!_busy.Add(exe)) return;
        try
        {
            RememberOriginalVolume(exe, tree);
            _audio.SetSessionVolume(tree, EnhancerEngine.AttenuatedSessionVolume);
            await Task.Run(() => _engine.Start(new EnhanceTarget(exe, root, deviceId, effects)));
            _errors.Remove(exe);
            _retryAfter.Remove(exe);
            Enforce(); // also picks up our own session volume now that we render
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException or ArgumentException)
        {
            Log.Error($"Enhancer start failed for {exe}", ex);
            _errors[exe] = "Couldn't start. Retrying shortly…";
            _retryAfter[exe] = DateTime.UtcNow + RetryDelay;
            RestoreVolume(exe, tree);
        }
        finally
        {
            _busy.Remove(exe);
        }

        // Turned off while it was starting.
        if (_settings.Enhance.GetValueOrDefault(exe)?.Enabled != true && _engine.IsRunning(exe))
            await StopAsync(exe, restore: true);
        UpdateRowStatuses();
    }

    private async Task RestartAsync(string exe, uint root, string deviceId, HashSet<uint> tree, EffectSettings effects)
    {
        await StopAsync(exe, restore: false);
        await StartAsync(exe, root, deviceId, tree, effects);
    }

    private async Task StopAsync(string exe, bool restore)
    {
        if (!_busy.Add(exe)) return;
        try
        {
            await Task.Run(() => _engine.Stop(exe));
            if (restore && _trees.TryGetValue(exe, out var tree)) RestoreVolume(exe, tree);
        }
        finally
        {
            _busy.Remove(exe);
        }
        UpdateRowStatuses();
    }

    /// <summary>Persists the app's current volume before the first change, so it can always be put back.</summary>
    private void RememberOriginalVolume(string exe, HashSet<uint> tree)
    {
        if (_settings.SavedAppVolumes.ContainsKey(exe)) return;
        float volume = _audio.GetSessionVolume(tree) ?? 1f;
        // Already at the attenuated level with no record of it (e.g. settings were reset): assume 100%.
        if (volume <= EnhancerEngine.AttenuatedSessionVolume * 1.5f) volume = 1f;
        _settings.SavedAppVolumes[exe] = volume;
        _saveNow();
    }

    private void RestoreVolume(string exe, HashSet<uint> tree)
    {
        if (!_settings.SavedAppVolumes.TryGetValue(exe, out var volume)) return;
        try
        {
            var result = _audio.SetSessionVolume(tree, volume);
            if (result.Sessions == 0) return; // not playing now; restored the next time it is
            _settings.SavedAppVolumes.Remove(exe);
            _saveNow();
            Log.Info($"Restored {exe} volume to {volume:P0}");
        }
        catch (COMException ex)
        {
            Log.Error($"Restoring volume for {exe} failed", ex);
        }
    }

    /// <summary>Every 250 ms: keep enhanced apps attenuated and track Dismusic's own volume.</summary>
    private void Enforce()
    {
        if (_disposed) return;
        var active = _engine.RunningApps;
        if (active.Count == 0) return;
        try
        {
            float own = _audio.GetSessionVolume(_self) ?? 1f;
            if (Math.Abs(own - OwnSessionVolume) > 1e-4f)
            {
                OwnSessionVolume = own;
                _engine.SetOwnSessionVolume(own);
            }
            foreach (var exe in active)
                if (_trees.TryGetValue(exe, out var tree))
                    _audio.SetSessionVolume(tree, EnhancerEngine.AttenuatedSessionVolume);
        }
        catch (COMException ex)
        {
            Log.Warn($"Volume enforcement failed: 0x{ex.HResult:X8}");
        }
    }

    // ---------- Row callbacks ----------

    internal void OnEnabledChanged(EnhanceAppViewModel row)
    {
        if (_suppress) return;
        var cfg = ConfigFor(row.ExeKey);
        cfg.Enabled = row.Enabled;
        _saveNow();
        if (row.Enabled)
            StatusText = $"While enhanced, Windows' volume mixer shows {row.DisplayName} at 0%. That's expected: Dismusic plays the processed sound instead. Use the volume slider here.";
        _retryAfter.Remove(row.ExeKey);
        Reconcile();
        UpdateRowStatuses();
    }

    internal void OnPresetChanged(EnhanceAppViewModel row)
    {
        if (_suppress || row.PresetKey == CustomPresetKey) return;
        var preset = FindPreset(row.PresetKey);
        if (preset is null) return;
        var cfg = ConfigFor(row.ExeKey);
        cfg.Apply(preset.ToSettings(), preset.Name);
        _suppress = true;
        try { row.Load(cfg.Enabled, preset.Name, cfg.ToEffects()); }
        finally { _suppress = false; }
        _engine.Update(row.ExeKey, cfg.ToEffects());
        RememberLast(cfg);
        _scheduleSave();
        UpdateRowStatuses();
        DeletePresetCommand.RaiseCanExecuteChanged();
    }

    internal void OnEffectsChanged(EnhanceAppViewModel row)
    {
        if (_suppress) return;
        var effects = new EffectSettings(row.VolumePercent, row.Bands.Select(b => b.Gain).ToArray()).Sanitized();
        var match = AllPresets().FirstOrDefault(p => p.Matches(effects));
        var cfg = ConfigFor(row.ExeKey);
        cfg.Apply(effects, match?.Name ?? CustomPresetKey);
        _suppress = true;
        try { row.PresetKey = cfg.Preset; }
        finally { _suppress = false; }
        _engine.Update(row.ExeKey, effects);
        RememberLast(cfg);
        _scheduleSave();
        UpdateRowStatuses();
        DeletePresetCommand.RaiseCanExecuteChanged();
    }

    private AppEnhancementSettings ConfigFor(string exe)
    {
        if (!_settings.Enhance.TryGetValue(exe, out var cfg))
        {
            // A newly enhanced app starts from the last enhancement you chose, not from Flat.
            _settings.Enhance[exe] = cfg = new AppEnhancementSettings();
            cfg.Apply(LastEffects, LastPresetKey);
        }
        return cfg;
    }

    private EffectSettings LastEffects =>
        _settings.LastEnhancement is { } last ? new EffectSettings(last.VolumePercent, last.BandsDb).Sanitized() : EffectSettings.Flat;

    private string LastPresetKey =>
        _settings.LastEnhancement is null ? "Flat" : FindPreset(_settings.LastEnhancement.Name)?.Name ?? CustomPresetKey;

    private void RememberLast(AppEnhancementSettings cfg) =>
        _settings.LastEnhancement = new PresetSettings { Name = cfg.Preset, VolumePercent = cfg.VolumePercent, BandsDb = cfg.BandsDb.ToArray() };

    private void UpdateRowStatuses()
    {
        foreach (var row in Apps)
        {
            var cfg = _settings.Enhance.GetValueOrDefault(row.ExeKey);
            row.IsActive = _engine.IsRunning(row.ExeKey);
            string effects = $"{PresetLabel(cfg?.Preset)} · {cfg?.VolumePercent ?? 100}%";
            row.StatusText = _errors.TryGetValue(row.ExeKey, out var error) ? error
                : cfg?.Enabled != true ? "Off"
                : row.IsActive ? $"Enhanced · {effects}"
                : _busy.Contains(row.ExeKey) ? "Starting…"
                : IsPlaying(row.ExeKey) ? "Waiting…"
                : $"On when it plays · {effects}";
        }
    }

    // ---------- Presets ----------

    private IEnumerable<EffectPreset> AllPresets() =>
        BuiltInPresets.All.Concat(_settings.CustomPresets.Select(p => p.ToPreset()));

    private EffectPreset? FindPreset(string? name) =>
        AllPresets().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool IsCustomPreset(string? name) =>
        _settings.CustomPresets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private string PresetKeyFor(AppEnhancementSettings? cfg) =>
        cfg is null ? LastPresetKey : FindPreset(cfg.Preset)?.Name ?? CustomPresetKey;

    private static string PresetLabel(string? preset) => string.IsNullOrEmpty(preset) ? "Custom" : preset;

    private void RebuildPresetOptions()
    {
        var desired = AllPresets().Select(p => new Option(p.Name, p.IsBuiltIn ? p.Name : $"{p.Name} (yours)")).ToList();
        desired.Add(new Option(CustomPresetKey, "Custom"));
        _suppress = true;
        try { CollectionSync.Sync(Presets, desired, o => o.Key, (have, want) => have.Name = want.Name); }
        finally { _suppress = false; }
    }

    private void SavePreset()
    {
        var row = Selected;
        if (row is null) return;
        var name = NewPresetName.Trim();
        if (name.Length == 0) { StatusText = "Type a name for the preset first."; return; }
        if (name.Length > 40) { StatusText = "Preset names can be at most 40 characters."; return; }
        if (BuiltInPresets.IsBuiltInName(name)) { StatusText = $"\"{name}\" is a built-in preset. Choose another name."; return; }

        var existing = _settings.CustomPresets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null) _settings.CustomPresets.Add(existing = new PresetSettings { Name = name });
        existing.VolumePercent = row.VolumePercent;
        existing.BandsDb = row.Bands.Select(b => b.Gain).ToArray();
        RebuildPresetOptions();
        ConfigFor(row.ExeKey).Preset = existing.Name;
        _suppress = true;
        try { row.PresetKey = existing.Name; }
        finally { _suppress = false; }
        NewPresetName = "";
        _saveNow();
        StatusText = $"Saved preset \"{existing.Name}\". You can use it for any app.";
        UpdateRowStatuses();
        DeletePresetCommand.RaiseCanExecuteChanged();
    }

    private void DeletePreset()
    {
        var name = Selected?.PresetKey;
        if (!IsCustomPreset(name)) return;
        _settings.CustomPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        foreach (var cfg in _settings.Enhance.Values.Where(c => string.Equals(c.Preset, name, StringComparison.OrdinalIgnoreCase)))
            cfg.Preset = CustomPresetKey; // keep their sound, just no longer linked to the preset
        _suppress = true;
        try
        {
            foreach (var row in Apps.Where(r => string.Equals(r.PresetKey, name, StringComparison.OrdinalIgnoreCase)))
                row.PresetKey = CustomPresetKey;
            RebuildPresetOptions();
        }
        finally { _suppress = false; }
        _saveNow();
        StatusText = $"Deleted preset \"{name}\". Apps that used it keep their current sound.";
        UpdateRowStatuses();
        DeletePresetCommand.RaiseCanExecuteChanged();
    }

    // ---------- Lifetime ----------

    /// <summary>Stops all processing and gives every app its original volume back.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enforceTimer.Stop();
        _engine.StopAll();
        foreach (var (exe, tree) in _trees.ToList()) RestoreVolume(exe, tree);
        _saveNow();
    }
}

public sealed class EnhanceAppViewModel : ObservableObject
{
    private readonly EnhanceViewModel _owner;
    private string _displayName;
    private bool _enabled;
    private string _presetKey = "Flat";
    private int _volumePercent = 100;
    private string _statusText = "";
    private bool _isActive;

    public EnhanceAppViewModel(string exeKey, EnhanceViewModel owner)
    {
        ExeKey = exeKey;
        _owner = owner;
        _displayName = exeKey;
        Bands = new ObservableCollection<BandViewModel>(
            Enumerable.Range(0, EqBands.Count).Select(i => new BandViewModel(EqBands.Labels[i], () => _owner.OnEffectsChanged(this))));
    }

    public string ExeKey { get; }
    public ObservableCollection<BandViewModel> Bands { get; }

    public string DisplayName
    {
        get => _displayName;
        set { if (Set(ref _displayName, value)) Raise(nameof(EnableAutomationName)); }
    }

    public bool Enabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value)) _owner.OnEnabledChanged(this); }
    }

    public string PresetKey
    {
        get => _presetKey;
        set { if (value is not null && Set(ref _presetKey, value)) _owner.OnPresetChanged(this); }
    }

    public int VolumePercent
    {
        get => _volumePercent;
        set { if (Set(ref _volumePercent, value)) _owner.OnEffectsChanged(this); }
    }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public string EnableAutomationName => $"Enhance {DisplayName}";

    /// <summary>Updates from settings without triggering callbacks (caller suppresses).</summary>
    public void Load(bool enabled, string presetKey, EffectSettings effects)
    {
        Set(ref _enabled, enabled, nameof(Enabled));
        Set(ref _presetKey, presetKey, nameof(PresetKey));
        Set(ref _volumePercent, effects.VolumePercent, nameof(VolumePercent));
        for (int i = 0; i < Bands.Count; i++) Bands[i].Load(effects.BandsDb[i]);
    }
}

public sealed class BandViewModel(string label, Action changed) : ObservableObject
{
    private double _gain;

    public string Label { get; } = label;
    public string AutomationName => $"{Label} hertz band, decibels";

    public double Gain
    {
        get => _gain;
        set
        {
            if (!Set(ref _gain, Math.Round(value, 1))) return;
            Raise(nameof(GainText));
            changed();
        }
    }

    public string GainText => _gain > 0 ? $"+{_gain:0.#}" : $"{_gain:0.#}";

    public void Load(double gain)
    {
        if (Set(ref _gain, Math.Round(gain, 1), nameof(Gain))) Raise(nameof(GainText));
    }
}
