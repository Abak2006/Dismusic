namespace Dismusic.Core.Effects;

/// <summary>
/// Stereo effect chain at a fixed sample rate: input gain → 10-band EQ → volume/boost → peak limiter.
/// <para>
/// <see cref="Process"/> runs on the audio thread; <see cref="Configure"/> may be called from any thread
/// (coefficients are swapped atomically). Volume changes are smoothed to avoid clicks.
/// </para>
/// <para>
/// Optional input guard: when the input is expected to be attenuated (see <c>EnhancerEngine</c>), any
/// sample above <see cref="GuardThreshold"/> means un-attenuated audio slipped through. The block is then
/// silenced and output stays muted for <see cref="GuardHoldMs"/> instead of being amplified.
/// </para>
/// </summary>
public sealed class AudioProcessor
{
    public const int GuardHoldMs = 300;
    public const float LimiterCeiling = 0.891f; // -1 dBFS
    private const double Q = 1.41;               // ~1 octave bandwidth
    private const double ReleaseSeconds = 0.08;
    private const double VolumeSmoothingSeconds = 0.01;

    private readonly int _sampleRate;
    private readonly double _releaseCoeff;
    private readonly double _volumeCoeff;
    private readonly double[] _z1 = new double[EqBands.Count * 2];
    private readonly double[] _z2 = new double[EqBands.Count * 2];
    private volatile Chain _chain;
    private double _limiterGain = 1;
    private double _volume;
    private int _guardHoldFrames;
    private long _guardTrips;

    public AudioProcessor(int sampleRate, float inputGain = 1f, float? guardThreshold = null)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        _inputGain = inputGain;
        GuardThreshold = guardThreshold;
        _releaseCoeff = 1 - Math.Exp(-1.0 / (ReleaseSeconds * sampleRate));
        _volumeCoeff = 1 - Math.Exp(-1.0 / (VolumeSmoothingSeconds * sampleRate));
        _chain = BuildChain(EffectSettings.Flat);
        _volume = _chain.Volume;
    }

    private volatile float _inputGain;

    /// <summary>Gain applied to the input before effects. Can be changed while running.</summary>
    public float InputGain
    {
        get => _inputGain;
        set => _inputGain = value;
    }
    public float? GuardThreshold { get; }
    public long GuardTrips => Interlocked.Read(ref _guardTrips);
    public bool IsGuardHolding => _guardHoldFrames > 0;

    /// <summary>Peak of the most recent block after input gain (before effects), for diagnostics.</summary>
    public float LastInputPeak { get; private set; }

    /// <summary>Raised on the audio thread when the guard trips.</summary>
    public event Action? GuardTripped;

    public void Configure(EffectSettings settings) => _chain = BuildChain(settings.Sanitized());

    /// <summary>Processes interleaved stereo samples in place.</summary>
    public void Process(Span<float> samples)
    {
        int frames = samples.Length / 2;
        if (frames == 0) return;

        if (GuardThreshold is { } threshold)
        {
            float rawPeak = 0;
            for (int i = 0; i < frames * 2; i++)
            {
                float a = Math.Abs(samples[i]);
                if (a > rawPeak || float.IsNaN(a)) rawPeak = float.IsNaN(a) ? float.PositiveInfinity : a;
            }
            if (rawPeak > threshold)
            {
                _guardHoldFrames = _sampleRate * GuardHoldMs / 1000;
                Interlocked.Increment(ref _guardTrips);
                ResetState();
                GuardTripped?.Invoke();
            }
            if (_guardHoldFrames > 0)
            {
                _guardHoldFrames = Math.Max(0, _guardHoldFrames - frames);
                samples[..(frames * 2)].Clear();
                LastInputPeak = 0;
                return;
            }
        }

        var chain = _chain;
        var bands = chain.Bands;
        double targetVolume = chain.Volume;
        double inputGain = InputGain;
        float inputPeak = 0;

        for (int f = 0; f < frames; f++)
        {
            double l = samples[2 * f] * inputGain;
            double r = samples[2 * f + 1] * inputGain;
            inputPeak = Math.Max(inputPeak, (float)Math.Max(Math.Abs(l), Math.Abs(r)));

            for (int b = 0; b < bands.Length; b++)
            {
                var c = bands[b];
                if (c.IsIdentity) continue;
                int il = b * 2, ir = il + 1;
                double yl = c.B0 * l + _z1[il];
                _z1[il] = c.B1 * l - c.A1 * yl + _z2[il];
                _z2[il] = c.B2 * l - c.A2 * yl;
                l = yl;
                double yr = c.B0 * r + _z1[ir];
                _z1[ir] = c.B1 * r - c.A1 * yr + _z2[ir];
                _z2[ir] = c.B2 * r - c.A2 * yr;
                r = yr;
            }

            _volume += (targetVolume - _volume) * _volumeCoeff;
            l *= _volume;
            r *= _volume;

            // Peak limiter: instant attack, smooth release.
            double peak = Math.Max(Math.Abs(l), Math.Abs(r));
            // The gain only ever moves up to `wanted`, so peak × gain can't exceed the ceiling.
            double wanted = peak > LimiterCeiling ? LimiterCeiling / peak : 1.0;
            _limiterGain = wanted < _limiterGain ? wanted : _limiterGain + (wanted - _limiterGain) * _releaseCoeff;
            l *= _limiterGain;
            r *= _limiterGain;

            if (!double.IsFinite(l) || !double.IsFinite(r))
            {
                ResetState();
                l = r = 0;
            }
            samples[2 * f] = (float)Math.Clamp(l, -1.0, 1.0);
            samples[2 * f + 1] = (float)Math.Clamp(r, -1.0, 1.0);
        }
        LastInputPeak = inputPeak;
    }

    private void ResetState()
    {
        Array.Clear(_z1);
        Array.Clear(_z2);
        _limiterGain = 1;
    }

    private Chain BuildChain(EffectSettings s)
    {
        var bands = new Biquad[EqBands.Count];
        for (int i = 0; i < EqBands.Count; i++)
            bands[i] = Biquad.Peaking(EqBands.Frequencies[i], s.BandsDb[i], Q, _sampleRate);
        return new Chain(bands, s.VolumePercent / 100.0);
    }

    private sealed record Chain(Biquad[] Bands, double Volume);

    /// <summary>RBJ audio-EQ-cookbook peaking filter, normalized (a0 = 1).</summary>
    internal readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2, bool IsIdentity)
    {
        public static Biquad Peaking(double frequency, double gainDb, double q, int sampleRate)
        {
            if (Math.Abs(gainDb) < 0.05 || frequency >= sampleRate / 2.0)
                return new Biquad(1, 0, 0, 0, 0, true);
            double a = Math.Pow(10, gainDb / 40);
            double w0 = 2 * Math.PI * frequency / sampleRate;
            double alpha = Math.Sin(w0) / (2 * q);
            double cos = Math.Cos(w0);
            double a0 = 1 + alpha / a;
            return new Biquad(
                (1 + alpha * a) / a0,
                -2 * cos / a0,
                (1 - alpha * a) / a0,
                -2 * cos / a0,
                (1 - alpha / a) / a0,
                false);
        }
    }
}
