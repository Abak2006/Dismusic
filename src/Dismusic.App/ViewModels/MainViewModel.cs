using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Dismusic.Core;
using Dismusic.Core.Enhance;
using Dismusic.Core.Mirror;
using Dismusic.Core.Routing;
using Dismusic.Core.Screens;
using Dismusic.Core.Sessions;
using Dismusic.Core.Settings;

namespace Dismusic.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly AudioSystem _audio;
    private readonly RoutingCoordinator _routing;
    private readonly MirrorEngine _mirror = new();
    private readonly WindowEventWatcher _windowWatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _screenDebounce;
    private readonly DispatcherTimer _saveDebounce;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _suppress;
    private bool _disposed;

    private string? _routingWarning;
    private string? _statusText;
    private bool _mirrorRunning;
    private bool _mirrorBusy;
    private string _mirrorStatus = "Off";
    private string? _mirrorSourceKey;
    private HashSet<string> _activeMirrorTargets = new(StringComparer.OrdinalIgnoreCase);
    private const string OriginalHereNote = "Skipped: the original already plays here";

    public MainViewModel(SettingsStore store, AppSettings settings, string? loadWarning)
    {
        _store = store;
        _settings = settings;
        _audio = new AudioSystem();
        _routing = new RoutingCoordinator(_audio, settings);
        RoutingWarning = string.Join(" ", new[] { _routing.RoutingUnavailableReason, loadWarning }.Where(s => s is not null));
        if (RoutingWarning.Length == 0) RoutingWarning = null;
        _mirrorSourceKey = settings.Mirror.Source;
        Enhance = new EnhanceViewModel(_audio, settings, ScheduleSave, Save);

        ToggleMirrorCommand = new RelayCommand(() => _ = ToggleMirrorAsync(), () => !MirrorBusy);
        AutoDetectMonitorsCommand = new RelayCommand(AutoDetectMonitors);
        ResetAllRoutesCommand = new RelayCommand(ResetAllRoutes, () => CanRoute);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);

        _mirror.TargetStatusChanged += (id, error) => _dispatcher.BeginInvoke(() => OnMirrorTargetStatus(id, error));
        _mirror.Faulted += message => _dispatcher.BeginInvoke(async () =>
        {
            await StopMirrorAsync(rememberOff: false);
            MirrorStatus = message;
        });

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); Save(); };

        _screenDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _screenDebounce.Tick += (_, _) => { _screenDebounce.Stop(); ReevaluateScreens(); };
        _windowWatcher = new WindowEventWatcher();
        _windowWatcher.WindowsChanged += () =>
        {
            if (!_settings.ScreenFollowEnabled || _settings.FollowScreenApps.Count == 0) return;
            _screenDebounce.Stop();
            _screenDebounce.Start();
        };

        _pollTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh(), _dispatcher);
        Refresh();
        _pollTimer.Start();

        if (settings.Mirror.Enabled)
            _dispatcher.BeginInvoke(async () => await StartMirrorAsync(), DispatcherPriority.ApplicationIdle);
    }

    // ---------- Bindable state ----------

    public EnhanceViewModel Enhance { get; }
    public ObservableCollection<Option> DeviceOptions { get; } = new();
    public ObservableCollection<Option> MonitorDeviceOptions { get; } = new();
    public ObservableCollection<Option> MirrorSources { get; } = new();
    public ObservableCollection<AppRowViewModel> Apps { get; } = new();
    public ObservableCollection<MonitorRowViewModel> Monitors { get; } = new();
    public ObservableCollection<MirrorTargetViewModel> MirrorTargets { get; } = new();

    /// <summary>Output devices with their Windows volume, mute and default state.</summary>
    public ObservableCollection<DeviceViewModel> OutputDevices { get; } = new();
    public DeviceViewModel? DefaultOutput => OutputDevices.FirstOrDefault(d => d.IsDefault);

    public RelayCommand ToggleMirrorCommand { get; }
    public RelayCommand AutoDetectMonitorsCommand { get; }
    public RelayCommand ResetAllRoutesCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }

    public bool CanRoute => _routing.RoutingUnavailableReason is null;
    public bool HasApps => Apps.Count > 0;
    public string? RoutingWarning { get => _routingWarning; private set => Set(ref _routingWarning, value); }
    public string? StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public bool ScreenFollowEnabled
    {
        get => _settings.ScreenFollowEnabled;
        set
        {
            if (_settings.ScreenFollowEnabled == value) return;
            _settings.ScreenFollowEnabled = value;
            Raise(nameof(ScreenFollowEnabled));
            if (!value)
            {
                // Hand screen-routed apps back to their fixed rule (or the Windows default).
                foreach (var exe in _settings.FollowScreenApps)
                    _routing.RouteNow(exe, _settings.AppRoutes.GetValueOrDefault(exe));
            }
            ScheduleSave();
            Refresh();
        }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set { _settings.StartMinimized = value; Raise(nameof(StartMinimized)); ScheduleSave(); }
    }

    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled();
        set
        {
            try
            {
                StartupRegistration.SetEnabled(value);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Error("Could not change startup registration", ex);
                StatusText = "Couldn't change the start-with-Windows setting.";
            }
            Raise(nameof(StartWithWindows));
        }
    }

    public bool MirrorRunning
    {
        get => _mirrorRunning;
        private set { if (Set(ref _mirrorRunning, value)) { Raise(nameof(MirrorButtonText)); Raise(nameof(CanEditSource)); } }
    }

    public bool MirrorBusy
    {
        get => _mirrorBusy;
        private set { if (Set(ref _mirrorBusy, value)) { ToggleMirrorCommand.RaiseCanExecuteChanged(); Raise(nameof(CanEditSource)); } }
    }

    public bool CanEditSource => !MirrorRunning && !MirrorBusy;
    public string MirrorButtonText => MirrorRunning ? "Stop" : "Start";
    public string MirrorStatus { get => _mirrorStatus; private set => Set(ref _mirrorStatus, value); }

    public string? MirrorSourceKey
    {
        get => _mirrorSourceKey;
        set
        {
            if (!Set(ref _mirrorSourceKey, value) || _suppress || value is null) return;
            _settings.Mirror.Source = value;
            ScheduleSave();
            if (_routing.Last is { } snapshot) UpdateOriginalDevices(snapshot);
        }
    }

    public string Version => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
    public string SettingsPath => _store.FilePath;

    // ---------- Refresh ----------

    private void Refresh()
    {
        if (_disposed) return;
        try
        {
            Merge(_routing.Refresh());
            if (StatusText is not null && StatusText.StartsWith("Couldn't read")) StatusText = null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Log.Error("Refresh failed", ex);
            StatusText = "Couldn't read audio devices from Windows. Retrying…";
        }
        UpdateMirrorStats();
    }

    private void ReevaluateScreens()
    {
        if (_disposed) return;
        try
        {
            if (_routing.ReevaluateScreens() is { } snapshot) Merge(snapshot);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Log.Error("Screen re-evaluation failed", ex);
        }
    }

    private void Merge(RoutingSnapshot snapshot)
    {
        _suppress = true;
        try
        {
            SyncDeviceOptions(snapshot.Devices);
            SyncOutputDevices(snapshot.Devices);
            SyncApps(snapshot);
            SyncMonitors(snapshot.Monitors);
            SyncMirror(snapshot);
        }
        finally
        {
            _suppress = false;
        }
        Raise(nameof(HasApps));
        Enhance.OnSnapshot(snapshot);
    }

    private string DeviceName(string id, IReadOnlyList<AudioDevice> devices) =>
        devices.FirstOrDefault(d => d.Id == id)?.Name
        ?? (_settings.KnownDeviceNames.TryGetValue(id, out var n) ? $"{n} (disconnected)" : "Unknown device (disconnected)");

    private void SyncOutputDevices(IReadOnlyList<AudioDevice> devices)
    {
        var desired = devices.Select(d => OutputDevices.FirstOrDefault(x => x.Id == d.Id)
            ?? new DeviceViewModel(d.Id, OnDeviceVolumeChanged, OnDeviceMuteChanged)).ToList();
        CollectionSync.Sync(OutputDevices, desired, d => d.Id);
        foreach (var (vm, d) in desired.Zip(devices)) vm.Load(d.Name, d.IsDefault, d.Volume, d.IsMuted);
        Raise(nameof(DefaultOutput));
    }

    /// <summary>Re-reads devices now (e.g. when the tray flyout opens) instead of waiting for the 2 s poll.</summary>
    public void RefreshNow() => Refresh();

    private void OnDeviceVolumeChanged(DeviceViewModel device)
    {
        if (_suppress) return;
        try
        {
            _audio.SetDeviceVolume(device.Id, device.Volume / 100f);
        }
        catch (COMException ex)
        {
            Log.Error($"Setting volume of {device.Name} failed", ex);
            StatusText = $"Couldn't change the volume of {device.Name}. It may have been disconnected.";
        }
    }

    private void OnDeviceMuteChanged(DeviceViewModel device)
    {
        if (_suppress) return;
        try
        {
            _audio.SetDeviceMute(device.Id, device.IsMuted);
        }
        catch (COMException ex)
        {
            Log.Error($"Muting {device.Name} failed", ex);
            StatusText = $"Couldn't mute or unmute {device.Name}. It may have been disconnected.";
        }
    }

    /// <summary>Makes the device the Windows default output (all roles). Apps that follow the default move to it.</summary>
    public void MakeDefaultOutput(DeviceViewModel device)
    {
        if (device.IsDefault) return;
        try
        {
            DefaultDevice.Set(device.Id);
            Log.Info($"Default output set to {device.Name}");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Error($"Setting default output to {device.Name} failed", ex);
            StatusText = $"Windows didn't switch the default output to {device.Name}.";
        }
        Refresh();
    }

    private void SyncDeviceOptions(IReadOnlyList<AudioDevice> devices)
    {
        // Devices referenced by saved rules stay listed while unplugged, so selections don't silently reset.
        var referenced = _settings.AppRoutes.Values.Concat(_settings.MonitorDevices.Values)
            .Where(id => devices.All(d => d.Id != id)).Distinct().ToList();
        var deviceItems = devices.Select(d => new Option(d.Id, d.Name))
            .Concat(referenced.Select(id => new Option(id, DeviceName(id, devices)))).ToList();

        var forApps = new List<Option> { new(null, "Windows default") };
        forApps.AddRange(deviceItems);
        CollectionSync.Sync(DeviceOptions, forApps, o => o.Key, (have, want) => have.Name = want.Name);

        var forMonitors = new List<Option> { new(null, "Not mapped") };
        forMonitors.AddRange(deviceItems.Select(o => new Option(o.Key, o.Name)));
        CollectionSync.Sync(MonitorDeviceOptions, forMonitors, o => o.Key, (have, want) => have.Name = want.Name);
    }

    private void SyncApps(RoutingSnapshot snapshot)
    {
        var desired = snapshot.Apps.Select(state =>
            Apps.FirstOrDefault(r => r.ExeKey == state.ExeKey) ?? new AppRowViewModel(state.ExeKey, OnAppDeviceChanged, OnAppFollowChanged)).ToList();
        CollectionSync.Sync(Apps, desired, r => r.ExeKey);

        foreach (var (row, state) in desired.Zip(snapshot.Apps))
        {
            row.DisplayName = state.DisplayName;
            row.IsPlaying = state.IsPlaying;
            row.StatusText = state.IsPlaying ? "Playing" : state.HasSession ? "Idle" : state.IsRunning ? "Running, no audio yet" : "Not running";
            row.Load(_settings.AppRoutes.GetValueOrDefault(state.ExeKey), _settings.IsFollowing(state.ExeKey));
            row.ErrorText = state.Error;
            row.NowOnText = !state.HasSession ? "—" : DescribeNowOn(state, snapshot.Devices);
        }
    }

    private string DescribeNowOn(AppState state, IReadOnlyList<AudioDevice> devices)
    {
        if (!CanRoute) return "Unknown";
        string device = state.CurrentDeviceId is null
            ? $"{devices.FirstOrDefault(d => d.IsDefault)?.Name ?? "No device"} (default)"
            : DeviceName(state.CurrentDeviceId, devices);
        return state.ActiveReason switch
        {
            RouteReason.Screen => $"{device} · via {state.MonitorName}",
            RouteReason.ScreenFallbackToDefault => $"{device} · {state.MonitorName} isn't mapped",
            _ => device,
        };
    }

    private void SyncMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        var desired = monitors.GroupBy(m => m.Key).Select(g => g.First()).Select(m =>
            Monitors.FirstOrDefault(r => r.Key == m.Key) ?? new MonitorRowViewModel(m.Key, OnMonitorDeviceChanged)).ToList();
        CollectionSync.Sync(Monitors, desired, r => r.Key);
        foreach (var row in desired)
        {
            var m = monitors.First(x => x.Key == row.Key);
            row.Name = m.Name;
            var parts = new List<string> { $"{m.Width}×{m.Height}" };
            if (m.IsPrimary) parts.Add("Primary");
            if (m.IsInternal) parts.Add("Built-in");
            row.Details = string.Join(" · ", parts);
            row.Load(_settings.MonitorDevices.GetValueOrDefault(m.Key));
        }
    }

    private void SyncMirror(RoutingSnapshot snapshot)
    {
        var devices = snapshot.Devices;
        var defaultName = devices.FirstOrDefault(d => d.IsDefault)?.Name ?? "none";
        var sources = new List<Option> { new(MirrorSettings.DefaultDeviceSource, $"Everything on the default device ({defaultName})") };
        sources.AddRange(devices.Select(d => new Option(MirrorSettings.DevicePrefix + d.Id, $"Everything on {d.Name}")));
        sources.Add(new Option(MirrorSettings.AllAppsSource, "All apps except Dismusic"));
        sources.AddRange(snapshot.Apps.Where(a => a.HasSession).Select(a => new Option(MirrorSettings.AppPrefix + a.ExeKey, $"Only {a.DisplayName}")));
        var saved = _settings.Mirror.Source;
        if (sources.All(s => s.Key != saved))
        {
            string label = saved.StartsWith(MirrorSettings.AppPrefix)
                ? $"Only {_settings.KnownAppNames.GetValueOrDefault(saved[MirrorSettings.AppPrefix.Length..], saved[MirrorSettings.AppPrefix.Length..])} (not playing)"
                : saved.StartsWith(MirrorSettings.DevicePrefix) ? $"Everything on {DeviceName(saved[MirrorSettings.DevicePrefix.Length..], devices)}" : saved;
            sources.Add(new Option(saved, label));
        }
        CollectionSync.Sync(MirrorSources, sources, o => o.Key, (have, want) => have.Name = want.Name);
        MirrorSourceKey = saved;

        var targets = devices.Select(d =>
        {
            var row = MirrorTargets.FirstOrDefault(t => t.DeviceId == d.Id);
            if (row is null)
            {
                row = new MirrorTargetViewModel(d.Id, OnMirrorTargetChanged);
                var cfg = _settings.Mirror.Targets.FirstOrDefault(t => t.DeviceId == d.Id);
                if (cfg is not null) row.Load(cfg.Enabled, cfg.DelayMs, (int)Math.Round(cfg.Volume * 100));
            }
            row.Name = d.Name;
            row.Device = OutputDevices.FirstOrDefault(x => x.Id == d.Id);
            return row;
        }).ToList();
        CollectionSync.Sync(MirrorTargets, targets, t => t.DeviceId);
        UpdateOriginalDevices(snapshot);
    }

    /// <summary>
    /// Marks targets where the source already plays, and while running restarts the copy if that changed
    /// (e.g. the app was moved to another device), so a device never gets the original plus a late copy.
    /// </summary>
    private void UpdateOriginalDevices(RoutingSnapshot snapshot)
    {
        var originals = MirrorPlanner.OriginalDevices(_settings.Mirror.Source, snapshot);
        foreach (var row in MirrorTargets)
        {
            row.IsOriginal = originals.Contains(row.DeviceId);
            if (row.IsOriginal && row.Enabled && !row.HasError) row.Status = OriginalHereNote;
            else if (row.Status == OriginalHereNote) row.Status = "";
        }
        if (MirrorRunning && !MirrorBusy && !EffectiveMirrorTargets().SetEquals(_activeMirrorTargets))
        {
            Log.Info("Mirror targets changed (source moved device); restarting");
            _ = RestartMirrorAsync();
        }
    }

    private HashSet<string> EffectiveMirrorTargets() =>
        MirrorTargets.Where(t => t.Enabled && !t.IsOriginal).Select(t => t.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ---------- User actions: apps ----------

    private void OnAppDeviceChanged(AppRowViewModel row)
    {
        if (_suppress) return;
        if (row.SelectedDeviceId is null) _settings.AppRoutes.Remove(row.ExeKey);
        else _settings.AppRoutes[row.ExeKey] = row.SelectedDeviceId;
        if (!(row.Follow && _settings.ScreenFollowEnabled))
            row.ErrorText = _routing.RouteNow(row.ExeKey, row.SelectedDeviceId);
        ScheduleSave();
        Refresh();
    }

    private void OnAppFollowChanged(AppRowViewModel row)
    {
        if (_suppress) return;
        _settings.SetFollowing(row.ExeKey, row.Follow);
        if (!row.Follow)
            row.ErrorText = _routing.RouteNow(row.ExeKey, _settings.AppRoutes.GetValueOrDefault(row.ExeKey));
        ScheduleSave();
        Refresh();
    }

    // ---------- User actions: screens ----------

    private void OnMonitorDeviceChanged(MonitorRowViewModel row)
    {
        if (_suppress) return;
        if (row.SelectedDeviceId is null) _settings.MonitorDevices.Remove(row.Key);
        else _settings.MonitorDevices[row.Key] = row.SelectedDeviceId;
        ScheduleSave();
        Refresh();
    }

    private void AutoDetectMonitors()
    {
        var snapshot = _routing.Last;
        if (snapshot is null) return;
        int matched = 0;
        foreach (var monitor in snapshot.Monitors)
        {
            if (_settings.MonitorDevices.ContainsKey(monitor.Key)) continue;
            var guess = DeviceMatcher.GuessDeviceForMonitor(monitor, snapshot.Devices);
            if (guess is null) continue;
            _settings.MonitorDevices[monitor.Key] = guess;
            matched++;
        }
        StatusText = matched > 0
            ? $"Matched {matched} screen(s) to an audio device. Check the result below."
            : "No confident match found. Pick the audio device for each screen manually.";
        ScheduleSave();
        Refresh();
    }

    // ---------- User actions: multi-output ----------

    private async Task ToggleMirrorAsync()
    {
        if (MirrorRunning) await StopMirrorAsync(rememberOff: true);
        else await StartMirrorAsync();
    }

    private void OnMirrorTargetChanged(MirrorTargetViewModel row, bool enabledChanged)
    {
        if (_suppress) return;
        var cfg = _settings.Mirror.Targets.FirstOrDefault(t => t.DeviceId == row.DeviceId);
        if (cfg is null)
        {
            cfg = new MirrorTargetSettings { DeviceId = row.DeviceId };
            _settings.Mirror.Targets.Add(cfg);
        }
        cfg.Enabled = row.Enabled;
        cfg.DelayMs = Math.Clamp(row.DelayMs, 0, MirrorTargetSettings.MaxDelayMs);
        cfg.Volume = Math.Clamp(row.Volume / 100f, 0f, 1f);
        ScheduleSave();

        if (!MirrorRunning) return;
        if (enabledChanged) _ = RestartMirrorAsync();
        else _mirror.UpdateTarget(row.DeviceId, cfg.DelayMs, cfg.Volume);
    }

    private async Task RestartMirrorAsync()
    {
        await StopMirrorAsync(rememberOff: false);
        await StartMirrorAsync();
    }

    private async Task StartMirrorAsync()
    {
        if (MirrorBusy || MirrorRunning) return;
        MirrorBusy = true;
        try
        {
            var source = ResolveMirrorSource(out var problem);
            if (source is null)
            {
                MirrorStatus = problem!;
                return;
            }
            if (_routing.Last is { } snapshot) UpdateOriginalDevices(snapshot);
            var targets = MirrorTargets.Where(t => t.Enabled && !t.IsOriginal)
                .Select(t => new MirrorTarget(t.DeviceId, t.DelayMs, t.Volume / 100f)).ToList();
            if (targets.Count == 0)
            {
                MirrorStatus = MirrorTargets.Any(t => t.Enabled)
                    ? "The source already plays on every device you turned on. Turn on a different device."
                    : "Turn on at least one device below, then press Start.";
                return;
            }
            MirrorStatus = "Starting…";
            foreach (var t in MirrorTargets)
            {
                t.HasError = false;
                t.Status = !t.Enabled ? "" : t.IsOriginal ? OriginalHereNote : "Starting…";
            }
            await Task.Run(() => _mirror.Start(source, targets));
            _activeMirrorTargets = targets.Select(t => t.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            MirrorRunning = true;
            _settings.Mirror.Enabled = true;
            ScheduleSave();
            MirrorStatus = "Running";
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or TimeoutException or NotSupportedException or ArgumentException)
        {
            Log.Error("Mirror start failed", ex);
            MirrorStatus = ex switch
            {
                NotSupportedException => "This source device's audio format isn't supported.",
                COMException => "Windows couldn't start capturing audio from that source.",
                _ => ex.Message,
            };
            foreach (var t in MirrorTargets.Where(t => !t.HasError)) t.Status = "";
        }
        finally
        {
            MirrorBusy = false;
        }
    }

    private async Task StopMirrorAsync(bool rememberOff)
    {
        MirrorBusy = true;
        try
        {
            await Task.Run(_mirror.Stop);
        }
        finally
        {
            MirrorBusy = false;
        }
        MirrorRunning = false;
        _activeMirrorTargets.Clear();
        MirrorStatus = "Off";
        foreach (var t in MirrorTargets) { t.Status = t.IsOriginal && t.Enabled ? OriginalHereNote : ""; t.HasError = false; }
        if (rememberOff)
        {
            _settings.Mirror.Enabled = false;
            ScheduleSave();
        }
    }

    private MirrorSource? ResolveMirrorSource(out string? problem)
    {
        problem = null;
        var key = _settings.Mirror.Source;
        var snapshot = _routing.Last;
        if (key == MirrorSettings.AllAppsSource)
            return MirrorSource.AllExcept((uint)Environment.ProcessId);

        if (key.StartsWith(MirrorSettings.DevicePrefix))
        {
            string? id = key == MirrorSettings.DefaultDeviceSource
                ? snapshot?.Devices.FirstOrDefault(d => d.IsDefault)?.Id
                : key[MirrorSettings.DevicePrefix.Length..];
            if (id is null || snapshot?.Devices.Any(d => d.Id == id) != true)
            {
                problem = "The source device isn't connected.";
                return null;
            }
            return MirrorSource.FromDevice(id);
        }

        var exe = key[MirrorSettings.AppPrefix.Length..];
        var app = snapshot?.Apps.FirstOrDefault(a => a.ExeKey == exe && a.SessionPids.Count > 0);
        if (app is null || snapshot is null)
        {
            problem = $"Start playing audio in {_settings.KnownAppNames.GetValueOrDefault(exe, exe)} first.";
            return null;
        }
        // An enhanced app plays at 1/256 in Windows; undo that so the copy isn't near-silent.
        return MirrorSource.FromProcessTree(snapshot.Processes.ResolveRootOfSameExe(app.SessionPids[0])) with
        {
            InputGain = Enhance.IsEnhancing(exe) ? EnhancerEngine.RecoveryGain : 1f,
        };
    }

    private void OnMirrorTargetStatus(string deviceId, string? error)
    {
        var row = MirrorTargets.FirstOrDefault(t => t.DeviceId == deviceId);
        if (row is null) return;
        row.HasError = error is not null;
        row.Status = error ?? "Playing";
    }

    private void UpdateMirrorStats()
    {
        if (!MirrorRunning) return;
        try
        {
            _mirror.SetOwnSessionVolume(_audio.GetSessionVolume(new HashSet<uint> { (uint)Environment.ProcessId }) ?? 1f);
        }
        catch (COMException ex)
        {
            Log.Warn($"Reading own volume failed: 0x{ex.HResult:X8}");
        }
        foreach (var s in _mirror.GetStats())
        {
            var row = MirrorTargets.FirstOrDefault(t => t.DeviceId == s.DeviceId);
            if (row is null || row.HasError || row.IsOriginal) continue;
            row.Status = s.Underruns > 0 ? $"Playing · {s.BufferedMs:F0} ms buffer · {s.Underruns} dropouts" : $"Playing · {s.BufferedMs:F0} ms buffer";
        }
    }

    // ---------- Settings tab ----------

    private void ResetAllRoutes()
    {
        var answer = MessageBox.Show(
            "This removes every per-app output choice on this PC, including ones made in Windows Settings, and turns off screen-follow for all apps. Apps go back to the Windows default device.\n\nContinue?",
            "Reset app outputs", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            _routing.ClearAllAppRoutes();
            _settings.AppRoutes.Clear();
            _settings.FollowScreenApps.Clear();
            Save();
            StatusText = "All app outputs were reset.";
        }
        catch (COMException ex)
        {
            Log.Error("Reset all routes failed", ex);
            StatusText = "Windows refused to reset app outputs.";
        }
        Refresh();
    }

    private static void OpenLogFolder()
    {
        var folder = Path.GetDirectoryName(Log.FilePath)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    // ---------- Persistence / lifetime ----------

    private void ScheduleSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void Save()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving settings failed", ex);
            StatusText = "Couldn't save settings. Your changes apply now but may not be kept after a restart.";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _screenDebounce.Stop();
        _saveDebounce.Stop();
        _windowWatcher.Dispose();
        Enhance.Dispose(); // restores apps' original volumes
        bool wasRunning = _settings.Mirror.Enabled;
        _mirror.Dispose();
        _settings.Mirror.Enabled = wasRunning; // resume next launch if it was on
        Save();
        _routing.Dispose();
        _audio.Dispose();
    }
}
