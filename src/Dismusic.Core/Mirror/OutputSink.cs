using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Dismusic.Core.Mirror;

/// <summary>
/// A WASAPI shared-mode render stream on one device, fed by a <see cref="MirrorChannel"/> that converts
/// rate/channels and absorbs clock drift. Takes ownership of <paramref name="device"/>.
/// </summary>
internal sealed class OutputSink : IWaveProvider, IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _out;
    private bool _disposed;

    public OutputSink(MMDevice device, int inputSampleRate, int delayMs, float volume,
        int baseLatencyMs = MirrorChannel.BaseLatencyMs, int deviceLatencyMs = 30)
    {
        _device = device;
        DeviceId = device.ID;
        var mix = device.AudioClient.MixFormat;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
        Channel = new MirrorChannel(inputSampleRate, 2, mix.SampleRate, mix.Channels, baseLatencyMs: baseLatencyMs)
        {
            DelayMs = delayMs,
            Volume = volume,
        };
        _out = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: deviceLatencyMs);
        _out.PlaybackStopped += (_, e) =>
        {
            if (!_disposed && e.Exception is not null)
            {
                Log.Error($"Output {DeviceId} stopped", e.Exception);
                Failed?.Invoke(MirrorEngine.DescribeDeviceError(e.Exception));
            }
        };
        _out.Init(this);
    }

    public event Action<string>? Failed;
    public string DeviceId { get; }
    public MirrorChannel Channel { get; }
    public WaveFormat WaveFormat { get; }

    public void Play() => _out.Play();

    public int Read(byte[] buffer, int offset, int count)
    {
        count -= count % WaveFormat.BlockAlign;
        Channel.Render(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count)));
        return count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _out.Stop(); } catch (COMException) { /* device already gone */ }
        _out.Dispose();
        _device.Dispose();
    }
}
