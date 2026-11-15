using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Dismusic.Core.Mirror;

/// <summary>A running audio capture delivering interleaved stereo float samples.</summary>
public interface ICaptureSource : IDisposable
{
    int SampleRate { get; }

    /// <summary>Raised on the capture thread with interleaved stereo samples. The buffer is reused.</summary>
    event Action<float[], int>? DataAvailable;

    /// <summary>Raised when capture ends; the exception is non-null if it ended because of an error.</summary>
    event Action<Exception?>? Stopped;

    void Start();
    void Stop();
}

/// <summary>
/// Endpoint loopback: everything that is playing on one output device (after Windows' mixing),
/// delivered as stereo float. Multichannel devices are reduced to their front left/right channels.
/// </summary>
public sealed class DeviceLoopbackCapture : ICaptureSource
{
    private readonly MMDevice _device;
    private readonly WasapiLoopbackCapture _capture;
    private readonly int _channels;
    private float[] _stereo = new float[9600];

    public DeviceLoopbackCapture(MMDevice device)
    {
        _device = device;
        _capture = new WasapiLoopbackCapture(device);
        var format = _capture.WaveFormat;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        if (!isFloat || format.BitsPerSample != 32)
            throw new NotSupportedException($"Device mix format {format} is not 32-bit float.");
        _channels = format.Channels;
        SampleRate = format.SampleRate;
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);
    }

    public int SampleRate { get; }
    public event Action<float[], int>? DataAvailable;
    public event Action<Exception?>? Stopped;

    public void Start() => _capture.StartRecording();

    public void Stop() => _capture.StopRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int frames = e.BytesRecorded / (4 * _channels);
        if (frames == 0) return;
        if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
        var input = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, frames * 4 * _channels));
        for (int f = 0; f < frames; f++)
        {
            float l = input[f * _channels];
            _stereo[f * 2] = l;
            _stereo[f * 2 + 1] = _channels > 1 ? input[f * _channels + 1] : l;
        }
        DataAvailable?.Invoke(_stereo, frames * 2);
    }

    public void Dispose()
    {
        _capture.Dispose();
        _device.Dispose();
    }
}
