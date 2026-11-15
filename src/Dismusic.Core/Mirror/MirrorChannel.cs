namespace Dismusic.Core.Mirror;

/// <summary>
/// One mirrored output: buffers captured stereo audio and renders it at the output device's rate and
/// channel count. The capture and render clocks are independent crystals that drift apart, so the
/// buffer level is held at a target (base latency + user delay) by nudging the resampling ratio by at
/// most ±0.5% — inaudible, but enough to absorb any real clock drift.
/// <para>Pure DSP with no device I/O, so it can be unit tested.</para>
/// </summary>
public sealed class MirrorChannel
{
    public const int BaseLatencyMs = 60;
    public const double MaxCorrection = 0.005;
    private const double CorrectionGainPerSecond = 0.05;
    private const double SmoothingFactor = 0.05;
    private const int ResyncThresholdMs = 250;

    private readonly int _inChannels;
    private readonly int _outChannels;
    private readonly int _inRate;
    private readonly double _baseStep;
    private readonly AudioRingBuffer _ring;
    private readonly float[] _frameA;
    private readonly float[] _frameB;
    private double _position;          // fraction between _frameA and _frameB
    private bool _havePair;
    private bool _priming = true;
    private double _smoothedFillFrames = -1;
    private readonly int _baseLatencyMs;
    private volatile int _delayMs;
    private volatile float _volume = 1f;

    /// <param name="baseLatencyMs">Buffer target before user delay. Lower = less lag, more risk of dropouts.</param>
    public MirrorChannel(int inRate, int inChannels, int outRate, int outChannels, int maxDelayMs = 1000, int baseLatencyMs = BaseLatencyMs)
    {
        if (baseLatencyMs < 10) throw new ArgumentOutOfRangeException(nameof(baseLatencyMs));
        _baseLatencyMs = baseLatencyMs;
        if (inRate <= 0 || outRate <= 0) throw new ArgumentOutOfRangeException(nameof(inRate));
        if (inChannels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(inChannels));
        if (outChannels < 1) throw new ArgumentOutOfRangeException(nameof(outChannels));
        _inRate = inRate;
        _inChannels = inChannels;
        _outChannels = outChannels;
        _baseStep = (double)inRate / outRate;
        _ring = new AudioRingBuffer(inChannels, inRate * (maxDelayMs + baseLatencyMs + 1000) / 1000);
        _frameA = new float[inChannels];
        _frameB = new float[inChannels];
    }

    public int DelayMs
    {
        get => _delayMs;
        set => _delayMs = Math.Max(0, value);
    }

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public double Correction { get; private set; }
    public long Underruns { get; private set; }
    public double BufferedMs => _ring.CountFrames * 1000.0 / _inRate;
    private int TargetFrames => (int)((long)(_baseLatencyMs + _delayMs) * _inRate / 1000);

    /// <summary>Called from the capture thread.</summary>
    public void Write(ReadOnlySpan<float> interleaved) => _ring.Write(interleaved);

    /// <summary>Called from the render thread. Always fills <paramref name="output"/> completely (silence on underrun).</summary>
    public void Render(Span<float> output)
    {
        int outFrames = output.Length / _outChannels;
        int fill = _ring.CountFrames;
        int target = TargetFrames;

        if (_priming)
        {
            if (fill < target)
            {
                output.Clear();
                return;
            }
            _priming = false;
            _havePair = false;
            _smoothedFillFrames = fill;
        }

        // After a long stall (e.g. render thread starved) don't play stale audio: jump back to the target.
        if (fill > target + _inRate * ResyncThresholdMs / 1000)
        {
            _ring.Skip(fill - target);
            fill = target;
            _smoothedFillFrames = target;
        }

        _smoothedFillFrames = _smoothedFillFrames < 0 ? fill : _smoothedFillFrames + SmoothingFactor * (fill - _smoothedFillFrames);
        double errorSeconds = (_smoothedFillFrames - target) / _inRate;
        Correction = Math.Clamp(errorSeconds * CorrectionGainPerSecond, -MaxCorrection, MaxCorrection);
        double step = _baseStep * (1 + Correction);
        float volume = _volume;

        Span<float> one = stackalloc float[_inChannels];
        int produced = 0;
        for (; produced < outFrames; produced++)
        {
            if (!_havePair)
            {
                if (_ring.Read(_frameA) == 0 || _ring.Read(_frameB) == 0) break;
                _position = 0;
                _havePair = true;
            }
            while (_position >= 1.0)
            {
                if (_ring.Read(one) == 0) goto underrun;
                Array.Copy(_frameB, _frameA, _inChannels);
                one.CopyTo(_frameB);
                _position -= 1.0;
            }

            float t = (float)_position;
            float left = (_frameA[0] + (_frameB[0] - _frameA[0]) * t) * volume;
            float right = _inChannels == 2 ? (_frameA[1] + (_frameB[1] - _frameA[1]) * t) * volume : left;
            int o = produced * _outChannels;
            if (_outChannels == 1)
            {
                output[o] = 0.5f * (left + right);
            }
            else
            {
                output[o] = left;
                output[o + 1] = right;
                for (int c = 2; c < _outChannels; c++) output[o + c] = 0f;
            }
            _position += step;
        }

        if (produced == outFrames) return;

        underrun:
        output[(produced * _outChannels)..].Clear();
        Underruns++;
        _priming = true; // refill to the target before resuming, instead of stuttering
    }
}
