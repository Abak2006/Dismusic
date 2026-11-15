using System.Runtime.InteropServices;
using Dismusic.Core.Effects;
using Dismusic.Core.Mirror;
using NAudio.CoreAudioApi;

namespace Dismusic.Core.Enhance;

/// <param name="RootProcessId">Root of the app's process tree; the whole tree is captured.</param>
/// <param name="DeviceId">Device the processed audio is played on (where the app plays).</param>
public sealed record EnhanceTarget(string ExeKey, uint RootProcessId, string DeviceId, EffectSettings Effects);

public sealed record EnhanceStats(string ExeKey, double BufferedMs, long Underruns, long GuardTrips, float InputPeak);

/// <summary>
/// Applies effects (boost, EQ, limiter) to individual apps without a virtual audio driver.
/// <para>
/// How: the caller turns the app's Windows per-app volume down to <see cref="AttenuatedSessionVolume"/> (1/256).
/// Windows applies that as an exact linear gain in its 32-bit float mixer (measured: capture gain equals the
/// scalar for other processes; <c>Dismusic.Diag taper</c> re-checks it), which leaves the
/// original playing at -48 dB (inaudible) while process-loopback capture receives the same samples scaled by
/// 1/256. Multiplying by 256 (a power of two) restores them bit-exactly; they are then processed and played
/// on the app's device. Capture happens before the device's master volume, so master volume still works.
/// </para>
/// <para>
/// Capture is also scaled by Dismusic's own per-app volume (measured), so <see cref="SetOwnSessionVolume"/>
/// must be kept up to date; the engine divides it back out so the user's Dismusic slider applies once, on output.
/// </para>
/// <para>
/// Safety: properly attenuated input can't exceed ~1/256, so anything louder means a session wasn't turned
/// down (new session, user moved the slider). The processor's guard then mutes output instead of amplifying
/// it 256×, and <see cref="GuardTripped"/> asks the caller to re-apply the volume.
/// </para>
/// </summary>
public sealed class EnhancerEngine : IDisposable
{
    /// <summary>Per-app volume scalar Windows is set to (shows as 0% in the volume mixer). Gain = 1/256 = -48 dB.</summary>
    public const float AttenuatedSessionVolume = 1f / 256f;
    public const float RecoveryGain = 256f;
    /// <summary>Twice the largest legitimate attenuated sample, leaving room for float overshoot.</summary>
    public const float GuardThreshold = 2f / RecoveryGain;
    private const int BufferMs = 30;
    private const int DeviceLatencyMs = 20;

    private readonly object _gate = new();
    private readonly Dictionary<string, Pipeline> _pipelines = new(StringComparer.OrdinalIgnoreCase);
    private float _ownSessionVolume = 1f;

    /// <summary>(exe, error or null when running). Raised from background threads.</summary>
    public event Action<string, string?>? StatusChanged;

    /// <summary>Un-attenuated audio detected for an app; re-apply its session volume. Raised on the audio thread.</summary>
    public event Action<string>? GuardTripped;

    public bool IsRunning(string exe)
    {
        lock (_gate) return _pipelines.ContainsKey(exe);
    }

    public EnhanceTarget? TargetOf(string exe)
    {
        lock (_gate) return _pipelines.GetValueOrDefault(exe)?.Target;
    }

    public IReadOnlyCollection<string> RunningApps
    {
        get { lock (_gate) return _pipelines.Keys.ToList(); }
    }

    /// <summary>
    /// Starts processing one app. The app's sessions must already be at <see cref="AttenuatedSessionVolume"/>.
    /// Call from an MTA thread (e.g. Task.Run). Throws if capture or the output device can't be opened.
    /// </summary>
    public void Start(EnhanceTarget target)
    {
        lock (_gate)
        {
            StopCore(target.ExeKey);
            var pipeline = new Pipeline(target, InputGainFor(_ownSessionVolume));
            pipeline.Processor.GuardTripped += () => GuardTripped?.Invoke(target.ExeKey);
            pipeline.Faulted += error =>
            {
                Log.Warn($"Enhancer for {target.ExeKey} faulted: {error}");
                StatusChanged?.Invoke(target.ExeKey, error);
            };
            pipeline.Start();
            _pipelines[target.ExeKey] = pipeline;
            Log.Info($"Enhancer started for {target.ExeKey} (pid tree {target.RootProcessId}) on {target.DeviceId}");
        }
        StatusChanged?.Invoke(target.ExeKey, null);
    }

    /// <summary>Dismusic's own Windows per-app volume (0–1), which Windows also applies to captured audio.</summary>
    public void SetOwnSessionVolume(float volume)
    {
        lock (_gate)
        {
            _ownSessionVolume = volume;
            foreach (var p in _pipelines.Values) p.Processor.InputGain = InputGainFor(volume);
        }
    }

    internal static float InputGainFor(float ownSessionVolume) =>
        ownSessionVolume < 0.01f ? 0f : RecoveryGain / ownSessionVolume;

    public void Update(string exe, EffectSettings effects)
    {
        lock (_gate)
        {
            if (_pipelines.TryGetValue(exe, out var p))
            {
                p.Processor.Configure(effects);
                p.Target = p.Target with { Effects = effects };
            }
        }
    }

    public void Stop(string exe)
    {
        lock (_gate) StopCore(exe);
    }

    public void StopAll()
    {
        lock (_gate)
            foreach (var exe in _pipelines.Keys.ToList()) StopCore(exe);
    }

    public IReadOnlyList<EnhanceStats> GetStats()
    {
        lock (_gate)
            return _pipelines.Values.Select(p => new EnhanceStats(p.Target.ExeKey, p.Sink.Channel.BufferedMs,
                p.Sink.Channel.Underruns, p.Processor.GuardTrips, p.Processor.LastInputPeak)).ToList();
    }

    private void StopCore(string exe)
    {
        if (!_pipelines.Remove(exe, out var p)) return;
        p.Dispose();
        Log.Info($"Enhancer stopped for {exe}");
    }

    public void Dispose() => StopAll();

    private sealed class Pipeline : IDisposable
    {
        private readonly ProcessLoopbackCapture _capture;

        public Pipeline(EnhanceTarget target, float inputGain)
        {
            Target = target;
            Processor = new AudioProcessor(ProcessLoopbackCapture.SampleRate, inputGain, GuardThreshold);
            Processor.Configure(target.Effects);
            using var enumerator = new MMDeviceEnumerator();
            Sink = new OutputSink(enumerator.GetDevice(target.DeviceId), ProcessLoopbackCapture.SampleRate,
                delayMs: 0, volume: 1f, baseLatencyMs: BufferMs, deviceLatencyMs: DeviceLatencyMs);
            Sink.Failed += error => Faulted?.Invoke(error);
            _capture = new ProcessLoopbackCapture(target.RootProcessId, includeProcessTree: true);
            _capture.DataAvailable += (buffer, count) =>
            {
                var span = buffer.AsSpan(0, count);
                Processor.Process(span);
                Sink.Channel.Write(span);
            };
            _capture.Stopped += error =>
            {
                if (error is not null) Faulted?.Invoke("Audio capture stopped.");
            };
        }

        public EnhanceTarget Target { get; set; }
        public AudioProcessor Processor { get; }
        public OutputSink Sink { get; }
        public event Action<string>? Faulted;

        public void Start()
        {
            try
            {
                _capture.Start();
                Sink.Play();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _capture.Stop();
            Sink.Dispose();
        }
    }
}
