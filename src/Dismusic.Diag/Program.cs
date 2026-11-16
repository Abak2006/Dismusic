using System.Diagnostics;
using Dismusic.Core;
using Dismusic.Core.Effects;
using Dismusic.Core.Enhance;
using Dismusic.Core.Mirror;
using Dismusic.Core.Routing;
using Dismusic.Core.Screens;
using Dismusic.Core.Sessions;

// Command-line diagnostics for the Windows audio plumbing Dismusic relies on.
// Usage: Dismusic.Diag [devices|sessions|monitors|policytest|capture [sec]|mirrortest [sec]|mirror <device-name-part> [sec]
//                       |follow <exe>|tonewindow [sec]|tone [sec]|enhancetest|mutetest|all]

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
        case "mutetest": MuteTest(); break;
        case "tone": PlayTone(args.Length > 1 ? int.Parse(args[1]) : 10); break;
        case "enhancetest": return EnhanceSelfTest();
        case "taper": Taper(args.Skip(1).ToArray()); break;
        case "resetownvolume": ResetOwnVolume(); break;
        case "devloop": DeviceLoopbackOwnVolume(); break;
        case "devstate": DeviceState(); break;
        case "defaulttest": return DefaultDeviceTest();
        case "volumetest": return DeviceVolumeTest();
        case "resetpowershellvolume": ResetPowerShellVolume(); break;
        case "mirrortest": return MirrorSelfTest(args.Length > 1 ? int.Parse(args[1]) : 6);
        case "all":
            Devices(); Sessions(); Monitors();
            int a = PolicyTest();
            int b = Capture(2);
            int c = MirrorSelfTest(6);
            int d = EnhanceSelfTest();
            int e = DefaultDeviceTest();
            int f = DeviceVolumeTest();
            return new[] { a, b, c, d, e, f }.FirstOrDefault(x => x != 0);
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
        var volume = audio.GetSessionVolume(new HashSet<uint> { s.ProcessId });
        Console.WriteLine($"  pid {s.ProcessId,6} {s.ExeKey,-28} {(s.IsActive ? "playing" : "idle   ")} vol {volume,7:F4} on {devices.GetValueOrDefault(s.DeviceId, s.DeviceId)} | pinned: {routed} | root pid {table.ResolveRootOfSameExe(s.ProcessId)}");
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

// Does process-loopback capture still receive audio when the session is muted or turned down? Note: this measures
// the diag process's own session, which shows gain = volume²; for other processes it is linear (see `taper`).
static void MuteTest()
{
    Console.WriteLine("== Process loopback vs. session mute/volume ==");
    using var tone = new NAudio.Wave.WasapiOut();
    tone.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2)
        { Frequency = 440, Gain = 0.001, Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin }));
    tone.Play();
    Thread.Sleep(500);
    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
    var sessions = device.AudioSessionManager.Sessions;
    NAudio.CoreAudioApi.AudioSessionControl? mine = null;
    for (int i = 0; i < sessions.Count; i++) if (sessions[i].GetProcessID == (uint)Environment.ProcessId) mine = sessions[i];
    if (mine is null) { Console.WriteLine("  own session not found"); return; }

    float Peak()
    {
        float peak = 0;
        using var cap = new ProcessLoopbackCapture((uint)Environment.ProcessId, includeProcessTree: true);
        cap.DataAvailable += (b, n) => { for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(b[i])); };
        cap.Start(); Thread.Sleep(1200); cap.Stop();
        return peak;
    }
    float reference = Peak();
    Console.WriteLine($"  normal:        peak {reference:F5}");
    foreach (var v in new[] { 0.5f, 0.25f, 0.1f, 0.05f, 0.02f })
    {
        mine.SimpleAudioVolume.Volume = v;
        float p = Peak();
        Console.WriteLine($"  session {v,4:P0}:  peak {p:F7}  gain {p / reference:F6}  (v^2 = {v * v:F6})");
    }
    mine.SimpleAudioVolume.Volume = 1f;
    var endpoint = device.AudioEndpointVolume;
    float master = endpoint.MasterVolumeLevelScalar;
    try
    {
        endpoint.MasterVolumeLevelScalar = master * 0.5f;
        Console.WriteLine($"  master x0.5:   peak {Peak():F5} (unchanged => capture is before master volume)");
    }
    finally
    {
        endpoint.MasterVolumeLevelScalar = master;
    }
    mine.SimpleAudioVolume.Mute = true;
    Console.WriteLine($"  muted:         peak {Peak():F5}");
    mine.SimpleAudioVolume.Mute = false;
    tone.Stop();
}

static void PlayTone(int seconds)
{
    using var tone = new NAudio.Wave.WasapiOut();
    tone.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2)
        { Frequency = 440, Gain = 0.001, Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin }));
    tone.Play();
    Thread.Sleep(seconds * 1000);
}

// End-to-end enhancer check, inaudible: a child process plays a -60 dB tone; it is attenuated to 1/256, captured,
// recovered ×256 and re-rendered; then its volume is changed behind the engine's back and restored.
static int EnhanceSelfTest()
{
    Console.WriteLine("== Enhancer self-test (inaudible) ==");
    using (var en = new NAudio.CoreAudioApi.MMDeviceEnumerator())
    using (var def = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia))
    {
        if (def.AudioEndpointVolume.Mute)
        {
            Console.WriteLine("  SKIP: the default output device is muted (Windows then delivers silence to loopback capture).");
            return 0;
        }
    }
    // The tone must come from a different executable: Windows shares per-app volume between processes of the
    // same exe, so a Dismusic.Diag child would also attenuate this process's own output and skew the result.
    var wav = Path.Combine(Path.GetTempPath(), "dismusic-tone-60db.wav");
    WriteToneWav(wav, amplitude: 0.001, seconds: 2);
    using var child = Process.Start(new ProcessStartInfo("powershell.exe",
        $"-NoProfile -Command \"$p = New-Object System.Media.SoundPlayer '{wav}'; $p.PlayLooping(); Start-Sleep 20\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
    using var audio = new AudioSystem();
    var tree = new HashSet<uint> { (uint)child.Id };
    float? original = null;
    for (int i = 0; i < 80 && original is null; i++) { Thread.Sleep(100); original = audio.GetSessionVolume(tree); }
    if (original is null) { Console.WriteLine("  FAIL: child session never appeared"); child.Kill(); return 1; }
    if (original < 0.02f) { Console.WriteLine($"  NOTE: powershell.exe volume was {original:F4} (left by an earlier run); restoring 100% afterwards"); original = 1f; }

    var device = audio.GetRenderDevices().First(d => d.IsDefault);

    // Reference: what Windows delivers for this app un-attenuated on this device. Endpoint effects (e.g. a
    // driver's "enhancements") can change the level, so the file's own amplitude isn't the right yardstick.
    float reference = 0;
    using (var cap = new ProcessLoopbackCapture((uint)child.Id, includeProcessTree: true))
    {
        cap.DataAvailable += (b, n) => { for (int i = 0; i < n; i++) reference = Math.Max(reference, Math.Abs(b[i])); };
        cap.Start(); Thread.Sleep(1200); cap.Stop();
    }
    if (reference <= 0) { Console.WriteLine("  SKIP: no audio reached capture (device muted or effects silencing it)"); child.Kill(); return 0; }
    // Bit-exact on devices without level-dependent effects; a driver's loudness/enhancement processing can shift
    // the quiet (attenuated) stream slightly, so allow 5% (0.4 dB) and report the actual deviation.
    bool Near(float peak) => Math.Abs(peak - reference) <= reference * 0.05f;
    using var engine = new EnhancerEngine();
    try
    {
        audio.SetSessionVolume(tree, EnhancerEngine.AttenuatedSessionVolume);
        engine.Start(new EnhanceTarget("tone", (uint)child.Id, device.Id, EffectSettings.Flat));
        Thread.Sleep(300);
        var self = new HashSet<uint> { (uint)Environment.ProcessId };
        Console.WriteLine($"  this process's own app volume: {audio.GetSessionVolume(self):F4} (compensated)");
        engine.SetOwnSessionVolume(audio.GetSessionVolume(self) ?? 1f);
        Thread.Sleep(1700);
        var s1 = engine.GetStats().Single();
        bool recovered = Near(s1.InputPeak) && s1.GuardTrips == 0;
        Console.WriteLine($"  attenuated: recovered peak {s1.InputPeak:F6} (un-attenuated reference {reference:F6}), buffer {s1.BufferedMs:F0} ms, underruns {s1.Underruns}, guard trips {s1.GuardTrips}");

        // The guard itself is unit-tested (AudioProcessorTests); a tone loud enough to trip it would be audible.
        audio.SetSessionVolume(tree, 0.5f); // simulate the user moving the app's slider
        Thread.Sleep(300);
        audio.SetSessionVolume(tree, EnhancerEngine.AttenuatedSessionVolume); // what the app does every 250 ms
        Thread.Sleep(1000);
        var s2 = engine.GetStats().Single();
        bool resumed = Near(s2.InputPeak);
        Console.WriteLine($"  after re-attenuating: recovered peak {s2.InputPeak:F6}, underruns {s2.Underruns}");

        audio.SetSessionVolume(self, 0.5f); // user turns Dismusic down in the mixer
        engine.SetOwnSessionVolume(0.5f);
        Thread.Sleep(1000);
        var s3 = engine.GetStats().Single();
        bool compensated = Near(s3.InputPeak);
        Console.WriteLine($"  own volume 50%: recovered peak {s3.InputPeak:F6} (compensated, expect {reference:F6})");
        audio.SetSessionVolume(self, 1f);

        double deviationDb = 20 * Math.Log10(s1.InputPeak / reference);
        Console.WriteLine($"  deviation from reference: {deviationDb:+0.00;-0.00} dB" +
            (Math.Abs(deviationDb) > 0.05 ? " (this device's audio effects process quiet audio differently; turn off its enhancements for exact results)" : " (exact)"));
        bool ok = recovered && resumed && compensated;
        Console.WriteLine(ok ? "  PASS" : "  FAIL");
        return ok ? 0 : 1;
    }
    finally
    {
        engine.StopAll();
        audio.SetSessionVolume(tree, original.Value);
        child.Kill();
    }
}

// Measures capture gain vs. session volume for ANOTHER process (cross-process case).
static void Taper(string[] options)
{
    NAudio.Wave.WasapiOut? parentOutput = null;
    if (options.Contains("render"))
    {
        parentOutput = new NAudio.Wave.WasapiOut();
        parentOutput.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Gain = 0 }));
        parentOutput.Play();
        Console.WriteLine("  (this process is also rendering silence)");
    }
    string childArgs = options.Contains("ps") ? "" : "tone 20";
    var wav = Path.Combine(Path.GetTempPath(), "dismusic-tone-60db.wav");
    WriteToneWav(wav, 0.001, 2);
    using var child = options.Contains("ps")
        ? Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"$p = New-Object System.Media.SoundPlayer '{wav}'; $p.PlayLooping(); Start-Sleep 25\"") { UseShellExecute = false, CreateNoWindow = true })!
        : Process.Start(new ProcessStartInfo(Environment.ProcessPath!, childArgs) { UseShellExecute = false, CreateNoWindow = true })!;
    using var audio = new AudioSystem();
    var tree = new HashSet<uint> { (uint)child.Id };
    for (int i = 0; i < 80 && audio.GetSessionVolume(tree) is null; i++) Thread.Sleep(100);
    Thread.Sleep(300);
    float Peak()
    {
        float peak = 0;
        using var cap = new ProcessLoopbackCapture((uint)child.Id, includeProcessTree: true);
        cap.DataAvailable += (b, n) => { for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(b[i])); };
        cap.Start(); Thread.Sleep(1000); cap.Stop();
        return peak;
    }
    try
    {
        foreach (var v in new[] { 1f, 1f / 16, 1f / 256, 1f, 1f / 256 })
        {
            var r = audio.SetSessionVolume(tree, v);
            Thread.Sleep(300);
            Console.WriteLine($"  set {v:F4} (sessions {r.Sessions}, readback {audio.GetSessionVolume(tree):F4}): gain {Peak() / 0.001f:F7} (x256 = {Peak() / 0.001f * 256:F5})");
        }
    }
    finally { audio.SetSessionVolume(tree, 1f); child.Kill(); parentOutput?.Dispose(); }
}

static void WriteToneWav(string path, double amplitude, int seconds)
{
    const int rate = 48000;
    int frames = rate * seconds;
    using var w = new BinaryWriter(File.Create(path));
    w.Write("RIFF"u8); w.Write(36 + frames * 4); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
    w.Write("data"u8); w.Write(frames * 4);
    for (int i = 0; i < frames; i++)
    {
        short v = (short)Math.Round(Math.Sin(2 * Math.PI * 440 * i / rate) * amplitude * 32767);
        w.Write(v); w.Write(v);
    }
}

// Windows remembers per-app volume. If a test left this tool's own volume low, put it back to 100%.
static void ResetOwnVolume()
{
    using var output = new NAudio.Wave.WasapiOut();
    output.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Gain = 0 }));
    output.Play();
    Thread.Sleep(300);
    using var audio = new AudioSystem();
    var self = new HashSet<uint> { (uint)Environment.ProcessId };
    Console.WriteLine($"  own volume was {audio.GetSessionVolume(self):F4}");
    audio.SetSessionVolume(self, 1f);
    Console.WriteLine($"  own volume now {audio.GetSessionVolume(self):F4}");
}

// Is endpoint (device) loopback capture also scaled by the capturing process's own per-app volume?
static void DeviceLoopbackOwnVolume()
{
    var wav = Path.Combine(Path.GetTempPath(), "dismusic-tone-60db.wav");
    WriteToneWav(wav, 0.001, 2);
    using var child = Process.Start(new ProcessStartInfo("powershell.exe",
        $"-NoProfile -Command \"$p = New-Object System.Media.SoundPlayer '{wav}'; $p.PlayLooping(); Start-Sleep 12\"") { UseShellExecute = false, CreateNoWindow = true })!;
    using var own = new NAudio.Wave.WasapiOut();
    own.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Gain = 0 }));
    own.Play();
    using var audio = new AudioSystem();
    var self = new HashSet<uint> { (uint)Environment.ProcessId };
    Thread.Sleep(2500);
    var device = audio.GetRenderDevices().First(d => d.IsDefault);
    float Peak()
    {
        float peak = 0;
        using var cap = new DeviceLoopbackCapture(audio.GetDevice(device.Id));
        cap.DataAvailable += (b, n) => { for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(b[i])); };
        cap.Start(); Thread.Sleep(1000); cap.Stop();
        return peak;
    }
    try
    {
        audio.SetSessionVolume(self, 1f);
        Console.WriteLine($"  own volume 100%: device loopback peak {Peak():F6}");
        audio.SetSessionVolume(self, 0.5f);
        Console.WriteLine($"  own volume 50%:  device loopback peak {Peak():F6}");
    }
    finally { audio.SetSessionVolume(self, 1f); child.Kill(); }
}

// Earlier test runs could leave powershell.exe's remembered per-app volume low; put it back to 100%.
static void ResetPowerShellVolume()
{
    var wav = Path.Combine(Path.GetTempPath(), "dismusic-silence.wav");
    WriteToneWav(wav, 0, 1);
    using var child = Process.Start(new ProcessStartInfo("powershell.exe",
        $"-NoProfile -Command \"$p = New-Object System.Media.SoundPlayer '{wav}'; $p.PlayLooping(); Start-Sleep 10\"") { UseShellExecute = false, CreateNoWindow = true })!;
    using var audio = new AudioSystem();
    var tree = new HashSet<uint> { (uint)child.Id };
    float? before = null;
    for (int i = 0; i < 80 && before is null; i++) { Thread.Sleep(100); before = audio.GetSessionVolume(tree); }
    audio.SetSessionVolume(tree, 1f);
    Console.WriteLine($"  powershell.exe volume: {before:F4} -> {audio.GetSessionVolume(tree):F4}");
    child.Kill();
}

static void DeviceState()
{
    using var e = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    foreach (var d in e.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
        using (d)
            Console.WriteLine($"  {d.FriendlyName}: master {d.AudioEndpointVolume.MasterVolumeLevelScalar:F2} muted={d.AudioEndpointVolume.Mute} peak={d.AudioMeterInformation.MasterPeakValue:F4}");
}

// Switches the Windows default output to another device, checks Windows reports it, then switches back.
static int DefaultDeviceTest()
{
    Console.WriteLine("== Default device switch self-test ==");
    using var audio = new AudioSystem();
    var devices = audio.GetRenderDevices();
    var original = devices.FirstOrDefault(d => d.IsDefault);
    var other = devices.FirstOrDefault(d => !d.IsDefault);
    if (original is null || other is null) { Console.WriteLine("  SKIP: needs two output devices"); return 0; }
    try
    {
        DefaultDevice.Set(other.Id);
        Thread.Sleep(300);
        bool switched = audio.GetRenderDevices().First(d => d.IsDefault).Id == other.Id;
        Console.WriteLine($"  {original.Name} -> {other.Name}: {(switched ? "ok" : "NOT switched")}");
        return switched ? Pass() : 1;
    }
    finally
    {
        DefaultDevice.Set(original.Id);
        Thread.Sleep(300);
        Console.WriteLine($"  restored default: {audio.GetRenderDevices().First(d => d.IsDefault).Name}");
    }
    static int Pass() { Console.WriteLine("  PASS"); return 0; }
}

// Nudges the default device's volume by 1%, reads it back, and restores the exact original value and mute.
static int DeviceVolumeTest()
{
    Console.WriteLine("== Device volume self-test ==");
    using var audio = new AudioSystem();
    var device = audio.GetRenderDevices().First(d => d.IsDefault);
    float target = device.Volume >= 0.5f ? device.Volume - 0.01f : device.Volume + 0.01f;
    try
    {
        audio.SetDeviceVolume(device.Id, target);
        audio.SetDeviceMute(device.Id, !device.IsMuted);
        var after = audio.GetRenderDevices().First(d => d.Id == device.Id);
        bool ok = Math.Abs(after.Volume - target) < 0.002f && after.IsMuted != device.IsMuted;
        Console.WriteLine($"  {device.Name}: {device.Volume:P0} -> {after.Volume:P1}, mute {device.IsMuted} -> {after.IsMuted}");
        Console.WriteLine(ok ? "  PASS" : "  FAIL");
        return ok ? 0 : 1;
    }
    finally
    {
        audio.SetDeviceVolume(device.Id, device.Volume);
        audio.SetDeviceMute(device.Id, device.IsMuted);
        var restored = audio.GetRenderDevices().First(d => d.Id == device.Id);
        Console.WriteLine($"  restored: {restored.Volume:P1}, muted={restored.IsMuted}");
    }
}
