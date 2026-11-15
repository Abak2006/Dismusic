namespace Dismusic.Core.Mirror;

/// <summary>
/// Thread-safe FIFO of interleaved float frames between the capture thread (writer) and one render
/// thread (reader). When full, the oldest audio is dropped so latency can never grow without bound.
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly object _gate = new();
    private int _readPos;   // in samples
    private int _count;     // in samples

    public AudioRingBuffer(int channels, int capacityFrames)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (capacityFrames <= 0) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        Channels = channels;
        _buffer = new float[channels * capacityFrames];
    }

    public int Channels { get; }
    public int CapacityFrames => _buffer.Length / Channels;

    public int CountFrames
    {
        get { lock (_gate) return _count / Channels; }
    }

    /// <summary>Total frames discarded because the buffer was full.</summary>
    public long OverflowFrames { get; private set; }

    public void Write(ReadOnlySpan<float> samples)
    {
        samples = samples[..(samples.Length - samples.Length % Channels)];
        lock (_gate)
        {
            if (samples.Length > _buffer.Length)
            {
                OverflowFrames += (samples.Length - _buffer.Length) / Channels;
                samples = samples[^_buffer.Length..];
            }
            int free = _buffer.Length - _count;
            if (samples.Length > free)
            {
                int drop = samples.Length - free;
                _readPos = (_readPos + drop) % _buffer.Length;
                _count -= drop;
                OverflowFrames += drop / Channels;
            }
            int writePos = (_readPos + _count) % _buffer.Length;
            int first = Math.Min(samples.Length, _buffer.Length - writePos);
            samples[..first].CopyTo(_buffer.AsSpan(writePos));
            samples[first..].CopyTo(_buffer.AsSpan(0));
            _count += samples.Length;
        }
    }

    /// <summary>Reads up to <c>destination.Length / Channels</c> frames; returns frames read.</summary>
    public int Read(Span<float> destination)
    {
        lock (_gate)
        {
            int samples = Math.Min(destination.Length - destination.Length % Channels, _count);
            int first = Math.Min(samples, _buffer.Length - _readPos);
            _buffer.AsSpan(_readPos, first).CopyTo(destination);
            _buffer.AsSpan(0, samples - first).CopyTo(destination[first..]);
            _readPos = (_readPos + samples) % _buffer.Length;
            _count -= samples;
            return samples / Channels;
        }
    }

    /// <summary>Discards up to <paramref name="frames"/> of the oldest frames.</summary>
    public void Skip(int frames)
    {
        lock (_gate)
        {
            int samples = Math.Min(frames * Channels, _count);
            _readPos = (_readPos + samples) % _buffer.Length;
            _count -= samples;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _readPos = 0;
            _count = 0;
        }
    }
}
