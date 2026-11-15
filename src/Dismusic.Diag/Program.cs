using System.Diagnostics;
using Dismusic.Core;
using Dismusic.Core.Mirror;
using Dismusic.Core.Routing;
using Dismusic.Core.Screens;
using Dismusic.Core.Sessions;

// Command-line diagnostics for the Windows audio plumbing Dismusic relies on.
// Usage: Dismusic.Diag [devices|sessions|monitors|policytest|capture [sec]|mirrortest [sec]|mirror <device-name-part> [sec]
//                       |follow <exe>|tonewindow [sec]|all]

Log.FilePath = Path.Combine(Path.GetTempPath(), "dismusic-diag.log");
var command = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

try
{
    switch (command)
    {
        case "devices": Devices(); break;
        case "sessions": Sessions(); break;
        case "monitors": Monitors(); break;
        case "policytest": return PolicyTest();
        case "capture": return Capture(args.Length > 1 ? int.Parse(args[1]) : 3);
        case "mirror":
            if (args.Length < 2) { Console.Error.WriteLine("mirror needs a device name fragment"); return 2; }
            return Mirror(args[1], args.Length > 2 ? int.Parse(args[2]) : 5);
        case "follow":
            if (args.Length < 2) { Console.Error.WriteLine("follow needs an exe name, e.g. brave.exe"); return 2; }
            Follow(args[1].ToLowerInvariant()); break;
        case "tonewindow": ToneWindow(args.Length > 1 ? int.Parse(args[1]) : 30); break;
        case "mirrortest": return MirrorSelfTest(args.Length > 1 ? int.Parse(args[1]) : 6);
        case "all":
            Devices(); Sessions(); Monitors();
            int a = PolicyTest();
            int b = Capture(2);
            int c = MirrorSelfTest(6);
            return a != 0 ? a : b != 0 ? b : c;
        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            return 2;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

static void Devices()
{
    using var audio = new AudioSystem();
    Console.WriteLine("== Output devices ==");
    foreach (var d in audio.GetRenderDevices())
        Console.WriteLine($"  {(d.IsDefault ? "*" : " ")} {d.Name}  [{d.Id}]");
}

static void Sessions()
{
    using var audio = new AudioSystem();
    var table = ProcessTable.Capture();
    var devices = audio.GetRenderDevices().ToDictionary(d => d.Id, d => d.Name);
    Console.WriteLine("== Audio sessions ==");
    AudioPolicyConfig? policy = null;
    try { policy = AudioPolicyConfig.Create(); } catch (AudioRoutingUnavailableException ex) { Console.WriteLine("  (routing unavailable: " + ex.Message + ")"); }
    var sessions = audio.GetSessions(table);
    if (sessions.Count == 0) Console.WriteLine("  (none)");
    foreach (var s in sessions)
    {
        string routed = "?";
        if (policy is not null)
        {
            var id = policy.GetAppEndpoint(s.ProcessId);
            routed = id is null ? "Windows default" : devices.GetValueOrDefault(id, id);
        }
        Console.WriteLine($"  pid {s.ProcessId,6} {s.ExeKey,-28} {(s.IsActive ? "playing" : "idle   ")} on {devices.GetValueOrDefault(s.DeviceId, s.DeviceId)} | pinned: {routed} | root pid {table.ResolveRootOfSameExe(s.ProcessId)}");
    }
    policy?.Dispose();
}

static void Monitors()
{
    using var audio = new AudioSystem();
    var devices = audio.GetRenderDevices();
    Console.WriteLine("== Monitors ==");
    foreach (var m in MonitorService.GetMonitors())
    {
        var guess = DeviceMatcher.GuessDeviceForMonitor(m, devices);
        Console.WriteLine($"  {m.Name} {(m.IsPrimary ? "(primary)" : "")} {m.Width}x{m.Height} internal={m.IsInternal}");
        Console.WriteLine($"      key: {m.Key}");
        Console.WriteLine($"      suggested audio: {(guess is null ? "-" : devices.First(d => d.Id == guess).Name)}");
    }
}

// Verifies the undocumented vtable on this Windows build by pinning this process to a non-default device,
// reading it back, and then clearing the assignment again.
static int PolicyTest()
{
    Console.WriteLine("== Per-app routing self-test ==");
    using var audio = new AudioSystem();
    var devices = audio.GetRenderDevices();
    var target = devices.FirstOrDefault(d => !d.IsDefault) ?? devices.FirstOrDefault();
    if (target is null) { Console.WriteLine("  SKIP: no output devices"); return 0; }

    using var policy = AudioPolicyConfig.Create();
    uint pid = (uint)Environment.ProcessId;
    var before = policy.GetAppEndpoint(pid);
    policy.SetAppEndpoint(pid, target.Id);
    var after = policy.GetAppEndpoint(pid);
    policy.SetAppEndpoint(pid, null);
    var cleared = policy.GetAppEndpoint(pid);

    bool ok = RoutingPlanner.SameDevice(after, target.Id) && cleared is null;
    Console.WriteLine($"  before={before ?? "default"} set->{target.Name} readback={(after is null ? "default" : after == target.Id ? "match" : after)} cleared={(cleared is null ? "yes" : "NO")}");
    Console.WriteLine(ok ? "  PASS" : "  FAIL");
    return ok ? 0 : 1;
}

static int Capture(int seconds)
{
    Console.WriteLine($"== System loopback capture ({seconds}s, excluding this process) ==");
    long samples = 0;
    float peak = 0;
    using var capture = new ProcessLoopbackCapture((uint)Environment.ProcessId, includeProcessTree: false);
    capture.DataAvailable += (buffer, count) =>
    {
        Interlocked.Add(ref samples, count);
        for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
    };
    capture.Start();
    Thread.Sleep(seconds * 1000);
    capture.Stop();
    Console.WriteLine($"  frames={samples / 2} (~{samples / 2.0 / ProcessLoopbackCapture.SampleRate:F2}s) peak={peak:F3}");
    Console.WriteLine(samples == 0 ? "  NOTE: no packets; Windows delivers none while nothing is playing." : "  PASS");
    return 0;
}

static int Mirror(string deviceFragment, int seconds)
{
    using var audio = new AudioSystem();
    var device = audio.GetRenderDevices().FirstOrDefault(d => d.Name.Contains(deviceFragment, StringComparison.OrdinalIgnoreCase));
    if (device is null) { Console.Error.WriteLine("No matching device."); return 2; }
    var source = audio.GetRenderDevices().First(d => d.IsDefault);
    Console.WriteLine($"== Mirroring '{source.Name}' to '{device.Name}' for {seconds}s ==");
    using var engine = new MirrorEngine();
    engine.TargetStatusChanged += (id, err) => Console.WriteLine($"  target {id}: {err ?? "running"}");
    engine.Start(MirrorSource.FromDevice(source.Id),
        [new MirrorTarget(device.Id, DelayMs: 0, Volume: 1f)]);
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        Thread.Sleep(1000);
        foreach (var s in engine.GetStats())
            Console.WriteLine($"  buffer {s.BufferedMs,6:F1} ms  correction {s.Correction * 100,7:F3}%  underruns {s.Underruns}");
    }
    engine.Stop();
    return 0;
}

// Silent end-to-end check: this process plays a very quiet tone on the default device, the engine captures
// this process tree and renders it to every other device at volume 0. Verifies capture → buffer → drift
// control → render with real streams, without audible output.
static int MirrorSelfTest(int seconds)
{
    Console.WriteLine($"== Mirror pipeline self-test ({seconds}s, inaudible) ==");
    using var audio = new AudioSystem();
    var devices = audio.GetRenderDevices();
    var targets = devices.Where(d => !d.IsDefault).Select(d => new MirrorTarget(d.Id, 0, 0f)).ToList();
    if (targets.Count == 0) targets = devices.Select(d => new MirrorTarget(d.Id, 0, 0f)).ToList();
    if (targets.Count == 0) { Console.WriteLine("  SKIP: no devices"); return 0; }

    using var tone = new NAudio.Wave.WasapiOut();
    var generator = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2)
    { Frequency = 440, Gain = 0.001, Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin };
    tone.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(generator));
    tone.Play();

    using var engine = new MirrorEngine();
    string? targetError = null;
    engine.TargetStatusChanged += (id, err) => { if (err is not null) targetError = err; };
    engine.Start(MirrorSource.FromProcessTree((uint)Environment.ProcessId), targets);
    IReadOnlyList<MirrorTargetStats> stats = [];
    for (int i = 0; i < seconds; i++)
    {
        Thread.Sleep(1000);
        stats = engine.GetStats();
        foreach (var s in stats)
            Console.WriteLine($"  t={i + 1}s buffer {s.BufferedMs,6:F1} ms  correction {s.Correction * 100,7:F3}%  underruns {s.Underruns}");
    }
    engine.Stop();
    tone.Stop();

    bool ok = targetError is null && stats.Count > 0 &&
              stats.All(s => s.BufferedMs > MirrorChannel.BaseLatencyMs * 0.5 && s.BufferedMs < MirrorChannel.BaseLatencyMs * 3);
    Console.WriteLine(ok ? "  PASS (buffer held near target)" : $"  FAIL {targetError}");
    return ok ? 0 : 1;
}

// Shows what screen-follow sees for one app: its processes, its topmost window and that window's monitor.
static void Follow(string exe)
{
    var table = ProcessTable.Capture();
    var pids = table.PidsForExe(exe);
    Console.WriteLine($"== Screen-follow view of {exe} ({pids.Count} processes) ==");
    var windows = WindowLocator.GetAppWindowsInZOrder();
    var mine = windows.Where(w => pids.Contains(w.ProcessId)).ToList();
    Console.WriteLine($"  eligible top-level windows: {windows.Count} total, {mine.Count} for {exe}");
    var monitors = MonitorService.GetMonitors();
    foreach (var w in mine)
    {
        var h = WindowLocator.MonitorOf(w.Handle);
        var m = monitors.FirstOrDefault(x => x.Handle == h);
        Console.WriteLine($"  hwnd 0x{w.Handle:X} pid {w.ProcessId} → monitor {(m is null ? $"UNKNOWN handle 0x{h:X}" : m.Name)}");
    }
}

// Opens a small visible window and plays an inaudible tone, giving screen-follow an app to route in tests.
static void ToneWindow(int seconds)
{
    var thread = new Thread(() =>
    {
        using var tone = new NAudio.Wave.WasapiOut();
        tone.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2)
            { Frequency = 440, Gain = 0.001, Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin }));
        tone.Play();
        using var form = new System.Windows.Forms.Form { Text = "Dismusic follow test", Width = 360, Height = 140, StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen };
        var timer = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
        timer.Tick += (_, _) => form.Close();
        timer.Start();
        System.Windows.Forms.Application.Run(form);
        tone.Stop();
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
}
