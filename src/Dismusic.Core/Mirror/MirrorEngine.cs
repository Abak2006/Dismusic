using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Dismusic.Core.Mirror;

public enum MirrorSourceKind
{
    /// <summary>Everything playing on one output device (endpoint loopback).</summary>
    Device,
    /// <summary>One app's process tree (process loopback, include mode).</summary>
    Process,
    /// <summary>All processes except one tree (process loopback, exclude mode).</summary>
    AllExceptProcess,
}

/// <param name="InputGain">Applied to captured audio, e.g. to undo the attenuation of an enhanced app.</param>
public sealed record MirrorSource(MirrorSourceKind Kind, uint ProcessId = 0, string? DeviceId = null, float InputGain = 1f)
{
    public static MirrorSource FromDevice(string deviceId) => new(MirrorSourceKind.Device, DeviceId: deviceId);
    public static MirrorSource FromProcessTree(uint pid) => new(MirrorSourceKind.Process, pid);
    public static MirrorSource AllExcept(uint pid) => new(MirrorSourceKind.AllExceptProcess, pid);

    public override string ToString() => Kind switch
    {
        MirrorSourceKind.Device => $"device {DeviceId}",
        MirrorSourceKind.Process => $"process tree {ProcessId}",
        _ => $"all except process tree {ProcessId}",
    };
}

public sealed record MirrorTarget(string DeviceId, int DelayMs, float Volume);

public sealed record MirrorTargetStats(string DeviceId, double BufferedMs, double Correction, long Underruns);

/// <summary>
/// Captures one source and plays copies of it on several output devices at once,
/// each with its own drift correction, delay and volume.
/// </summary>
public sealed class MirrorEngine : IDisposable
{
    private readonly object _gate = new();
    private ICaptureSource? _capture;
    private volatile OutputSink[] _sinks = [];
    private float _sourceGain = 1f;
    private float _ownSessionVolume = 1f;
    private volatile float _inputGain = 1f;

    /// <summary>(deviceId, error or null when running). Raised from background threads.</summary>
    public event Action<string, string?>? TargetStatusChanged;

    /// <summary>Raised when capture stops unexpectedly. Raised from a background thread.</summary>
    public event Action<string>? Faulted;

    public bool IsRunning => _capture is not null;

    /// <summary>
    /// Starts capture and all targets. Throws if capture can't start; a target that fails is reported via
    /// <see cref="TargetStatusChanged"/> and skipped so the others still play. Call from an MTA thread
    /// (e.g. <c>Task.Run</c>), not the UI thread.
    /// </summary>
    public void Start(MirrorSource source, IReadOnlyList<MirrorTarget> targets)
    {
        lock (_gate)
        {
            StopCore();
            using var enumerator = new MMDeviceEnumerator();
            ICaptureSource capture = source.Kind == MirrorSourceKind.Device
                ? new DeviceLoopbackCapture(enumerator.GetDevice(source.DeviceId))
                : new ProcessLoopbackCapture(source.ProcessId, source.Kind == MirrorSourceKind.Process);

            var sinks = new List<OutputSink>();
            foreach (var target in targets)
            {
                if (source.Kind == MirrorSourceKind.Device &&
                    string.Equals(target.DeviceId, source.DeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    TargetStatusChanged?.Invoke(target.DeviceId, "Skipped: this is the source device.");
                    continue;
                }
                try
                {
                    var sink = new OutputSink(enumerator.GetDevice(target.DeviceId), capture.SampleRate, target.DelayMs, target.Volume);
                    sink.Failed += error => TargetStatusChanged?.Invoke(target.DeviceId, error);
                    sinks.Add(sink);
                    TargetStatusChanged?.Invoke(target.DeviceId, null);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    Log.Error($"Mirror target {target.DeviceId} failed to start", ex);
                    TargetStatusChanged?.Invoke(target.DeviceId, DescribeDeviceError(ex));
                }
            }
            if (sinks.Count == 0)
            {
                capture.Dispose();
                throw new InvalidOperationException("None of the selected output devices could be opened.");
            }

            _sinks = sinks.ToArray();
            _sourceGain = source.InputGain;
            _inputGain = EffectiveGain();
            capture.DataAvailable += OnData;
            capture.Stopped += error =>
            {
                if (error is not null) Faulted?.Invoke("Audio capture stopped: " + error.Message);
            };
            try
            {
                capture.Start();
            }
            catch
            {
                capture.Dispose();
                foreach (var s in _sinks) s.Dispose();
                _sinks = [];
                throw;
            }
            _capture = capture;
            foreach (var s in _sinks) s.Play();
            Log.Info($"Mirror started: {source} → {_sinks.Length} device(s)");
        }
    }

    /// <summary>
    /// Dismusic's own Windows per-app volume. Windows also applies it to loopback capture (measured), so it is
    /// divided back out here; the user's slider for Dismusic then affects the copies once, on output.
    /// </summary>
    public void SetOwnSessionVolume(float volume)
    {
        lock (_gate)
        {
            _ownSessionVolume = volume;
            _inputGain = EffectiveGain();
        }
    }

    private float EffectiveGain() => _ownSessionVolume < 0.01f ? 0f : _sourceGain / _ownSessionVolume;

    public void UpdateTarget(string deviceId, int delayMs, float volume)
    {
        foreach (var s in _sinks)
        {
            if (s.DeviceId != deviceId) continue;
            s.Channel.DelayMs = delayMs;
            s.Channel.Volume = volume;
        }
    }

    public IReadOnlyList<MirrorTargetStats> GetStats() =>
        _sinks.Select(s => new MirrorTargetStats(s.DeviceId, s.Channel.BufferedMs, s.Channel.Correction, s.Channel.Underruns)).ToList();

    public void Stop()
    {
        lock (_gate) StopCore();
    }

    private void StopCore()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnData;
            _capture.Stop();
            _capture.Dispose();
            _capture = null;
            Log.Info("Mirror stopped");
        }
        foreach (var s in _sinks) s.Dispose();
        _sinks = [];
    }

    private void OnData(float[] buffer, int count)
    {
        var span = buffer.AsSpan(0, count);
        float gain = _inputGain;
        if (gain != 1f)
            for (int i = 0; i < span.Length; i++) span[i] *= gain;
        foreach (var s in _sinks) s.Channel.Write(span);
    }

    public static string DescribeDeviceError(Exception ex) => ex switch
    {
        COMException { HResult: unchecked((int)0x88890004) } => "Device was disconnected.",
        COMException { HResult: unchecked((int)0x8889000A) } => "Device is in use exclusively by another app.",
        COMException { HResult: unchecked((int)0x80070490) } => "Device not found.",
        _ => "Couldn't open this device.",
    };

    public void Dispose() => Stop();
}
