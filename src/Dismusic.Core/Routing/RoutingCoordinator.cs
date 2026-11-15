using System.Runtime.InteropServices;
using Dismusic.Core.Screens;
using Dismusic.Core.Sessions;
using Dismusic.Core.Settings;

namespace Dismusic.Core.Routing;

public sealed record AppState(
    string ExeKey,
    string DisplayName,
    bool IsRunning,
    bool HasSession,
    bool IsPlaying,
    IReadOnlyList<uint> SessionPids,
    string? CurrentDeviceId,
    RouteReason ActiveReason,
    string? MonitorName,
    string? Error,
    string? SessionDeviceId);

public sealed record RoutingSnapshot(
    IReadOnlyList<AudioDevice> Devices,
    IReadOnlyList<AppState> Apps,
    IReadOnlyList<MonitorInfo> Monitors,
    ProcessTable Processes);

/// <summary>
/// Owns per-app routing: reads devices, sessions, windows and monitors, decides each app's output with
/// <see cref="RoutingPlanner"/>, and applies changes through <see cref="AudioPolicyConfig"/>.
/// Not thread-safe; use it from the UI thread (where the policy factory was created).
/// </summary>
public sealed class RoutingCoordinator : IDisposable
{
    private readonly AudioSystem _audio;
    private readonly AppSettings _settings;
    private readonly AudioPolicyConfig? _policy;
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (RouteReason Reason, string? Monitor)> _lastDecision = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AudioSessionInfo> _sessions = [];
    private RoutingSnapshot? _last;

    public RoutingCoordinator(AudioSystem audio, AppSettings settings)
    {
        _audio = audio;
        _settings = settings;
        try
        {
            _policy = AudioPolicyConfig.Create();
        }
        catch (Exception ex) when (ex is AudioRoutingUnavailableException or COMException)
        {
            RoutingUnavailableReason = ex.Message;
            Log.Error("Per-app routing unavailable", ex);
        }
    }

    /// <summary>Non-null when per-app routing can't work on this system.</summary>
    public string? RoutingUnavailableReason { get; }

    public RoutingSnapshot? Last => _last;

    /// <summary>Full refresh: devices, processes, sessions, monitors; then applies routing.</summary>
    public RoutingSnapshot Refresh()
    {
        var devices = _audio.GetRenderDevices();
        var processes = ProcessTable.Capture();
        // Dismusic's own mirror streams are device-specific; listing or routing them would only confuse.
        uint self = (uint)Environment.ProcessId;
        _sessions = _audio.GetSessions(processes).Where(s => s.ProcessId != self).ToList();
        var monitors = MonitorService.GetMonitors();
        foreach (var d in devices) _settings.KnownDeviceNames[d.Id] = d.Name;
        _last = Apply(devices, processes, monitors);
        return _last;
    }

    /// <summary>Cheap re-evaluation after windows moved; reuses the last device/session/monitor lists.</summary>
    public RoutingSnapshot? ReevaluateScreens()
    {
        if (_last is null) return null;
        var monitors = MonitorService.GetMonitors(); // handles change after display reconfiguration
        _last = Apply(_last.Devices, _last.Processes, monitors);
        return _last;
    }

    /// <summary>Immediately pins an app to a device (null = Windows default), outside the normal rule flow.</summary>
    public string? RouteNow(string exeKey, string? deviceId)
    {
        if (_policy is null) return RoutingUnavailableReason;
        var pids = _sessions.Where(s => s.ExeKey == exeKey).Select(s => s.ProcessId).Distinct().ToList();
        if (pids.Count == 0) return null; // not running; the rule applies when it starts
        return TrySet(exeKey, pids, deviceId);
    }

    public void ClearAllAppRoutes()
    {
        _policy?.ClearAll();
        _lastErrors.Clear();
    }

    private RoutingSnapshot Apply(IReadOnlyList<AudioDevice> devices, ProcessTable processes, IReadOnlyList<MonitorInfo> monitors)
    {
        var present = devices.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var monitorByHandle = monitors.GroupBy(m => m.Handle).ToDictionary(g => g.Key, g => g.First());
        bool anyFollow = _settings.ScreenFollowEnabled && _settings.FollowScreenApps.Count > 0;
        var windows = anyFollow ? WindowLocator.GetAppWindowsInZOrder() : [];

        var exeKeys = _sessions.Select(s => s.ExeKey)
            .Concat(_settings.AppRoutes.Keys)
            .Concat(_settings.FollowScreenApps)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var apps = new List<AppState>();
        foreach (var exe in exeKeys)
        {
            var sessions = _sessions.Where(s => s.ExeKey == exe).ToList();
            var pids = sessions.Select(s => s.ProcessId).Distinct().ToList();
            bool running = processes.IsRunning(exe);
            string name = DisplayNameFor(exe, pids);
            string? current = null;
            string? monitorName = null;
            RouteReason reason = RouteReason.Unmanaged;
            _lastErrors.TryGetValue(exe, out var error);

            if (pids.Count > 0 && _policy is not null)
            {
                bool follow = _settings.ScreenFollowEnabled && _settings.IsFollowing(exe);
                var screen = new ScreenState(false, null);
                if (follow)
                {
                    var window = WindowLocator.FindTopmost(windows, processes.PidsForExe(exe).ToHashSet());
                    if (window is { } w && monitorByHandle.TryGetValue(WindowLocator.MonitorOf(w.Handle), out var monitor))
                    {
                        monitorName = monitor.Name;
                        screen = new ScreenState(true, _settings.MonitorDevices.GetValueOrDefault(monitor.Key));
                    }
                }

                _settings.AppRoutes.TryGetValue(exe, out var rule);
                var decision = RoutingPlanner.Decide(rule, follow, screen, present);
                try
                {
                    current = _policy.GetAppEndpoint(pids[0]);
                    if (decision.Manage && !RoutingPlanner.SameDevice(current, decision.DeviceId))
                    {
                        error = TrySet(exe, pids, decision.DeviceId);
                        if (error is null)
                        {
                            Log.Info($"Routed {exe} → {DeviceName(decision.DeviceId, devices)} ({decision.Reason}{(monitorName is null ? "" : ", " + monitorName)})");
                            current = decision.DeviceId;
                        }
                    }
                }
                catch (COMException ex)
                {
                    error = "Couldn't read this app's current output.";
                    Log.Error($"GetAppEndpoint failed for {exe}", ex);
                }
                reason = decision.Manage ? decision.Reason : RouteReason.Unmanaged;
                if (!decision.Manage && _lastDecision.TryGetValue(exe, out var prev) && follow && !screen.WindowFound)
                    (reason, monitorName) = prev; // minimized: keep showing where it was routed
                else
                    _lastDecision[exe] = (reason, monitorName);
            }

            var sessionDevice = (sessions.FirstOrDefault(s => s.IsActive) ?? sessions.FirstOrDefault())?.DeviceId;
            apps.Add(new AppState(exe, name, running, pids.Count > 0, sessions.Any(s => s.IsActive), pids,
                current, reason, monitorName, error, sessionDevice));
        }

        apps.Sort((a, b) =>
        {
            int c = b.HasSession.CompareTo(a.HasSession);
            return c != 0 ? c : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        });
        return new RoutingSnapshot(devices, apps, monitors, processes);
    }

    private string? TrySet(string exe, IReadOnlyList<uint> pids, string? deviceId)
    {
        try
        {
            foreach (var pid in pids) _policy!.SetAppEndpoint(pid, deviceId);
            _lastErrors.Remove(exe);
            return null;
        }
        catch (COMException ex)
        {
            Log.Error($"SetAppEndpoint failed for {exe}", ex);
            const string message = "Windows refused to change this app's output.";
            _lastErrors[exe] = message;
            return message;
        }
    }

    private string DisplayNameFor(string exe, IReadOnlyList<uint> pids)
    {
        if (_displayNames.TryGetValue(exe, out var cached)) return cached;
        if (pids.Count > 0)
        {
            var name = ProcessTable.DescribeExe(pids[0], exe);
            _displayNames[exe] = name;
            _settings.KnownAppNames[exe] = name;
            return name;
        }
        return _settings.KnownAppNames.GetValueOrDefault(exe) ?? Path.GetFileNameWithoutExtension(exe);
    }

    private string DeviceName(string? id, IReadOnlyList<AudioDevice> devices) =>
        id is null ? "Windows default" : devices.FirstOrDefault(d => d.Id == id)?.Name ?? _settings.KnownDeviceNames.GetValueOrDefault(id, id);

    public void Dispose() => _policy?.Dispose();
}
