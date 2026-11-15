using Dismusic.Core.Effects;
using Dismusic.Core.Enhance;
using Dismusic.Core.Sessions;
using Dismusic.Core.Settings;

namespace Dismusic.Tests;

public class AudioProcessorTests
{
    private const int Rate = 48000;

    private static float[] Sine(double freq, double amplitude, int frames, int startFrame = 0)
    {
        var b = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amplitude * Math.Sin(2 * Math.PI * freq * (i + startFrame) / Rate));
            b[2 * i] = v;
            b[2 * i + 1] = v;
        }
        return b;
    }

    private static float Peak(ReadOnlySpan<float> s)
    {
        float p = 0;
        foreach (var x in s) p = Math.Max(p, Math.Abs(x));
        return p;
    }

    /// <summary>Runs a sine through in 10 ms blocks for a second and returns the peak of the last half.</summary>
    private static float SteadyPeak(AudioProcessor p, double freq, double amplitude, float inputScale = 1f)
    {
        float peak = 0;
        for (int block = 0; block < 100; block++)
        {
            var b = Sine(freq, amplitude * inputScale, 480, block * 480);
            p.Process(b);
            if (block >= 50) peak = Math.Max(peak, Peak(b));
        }
        return peak;
    }

    [Fact]
    public void FlatSettingsPassAudioThroughUnchanged()
    {
        var p = new AudioProcessor(Rate);
        var input = Sine(440, 0.5, 4800);
        var output = (float[])input.Clone();
        p.Process(output);
        for (int i = 0; i < input.Length; i++) Assert.Equal(input[i], output[i], 5);
    }

    [Fact]
    public void RecoversAttenuatedInputExactly()
    {
        // The enhancer path: Windows scaled the app by 1/256, the processor multiplies by 256.
        var p = new AudioProcessor(Rate, EnhancerEngine.RecoveryGain, EnhancerEngine.GuardThreshold);
        var original = Sine(1000, 0.7, 4800);
        var attenuated = original.Select(x => x / 256f).ToArray();
        p.Process(attenuated);
        for (int i = 0; i < original.Length; i++) Assert.Equal(original[i], attenuated[i], 6);
        Assert.Equal(0, p.GuardTrips);
    }

    [Fact]
    public void VolumeAbove100BoostsQuietAudio()
    {
        var p = new AudioProcessor(Rate);
        p.Configure(new EffectSettings(200, new double[EqBands.Count]));
        Assert.Equal(0.2f, SteadyPeak(p, 440, 0.1), 2);
    }

    [Fact]
    public void LimiterKeepsBoostedAudioBelowCeiling()
    {
        var p = new AudioProcessor(Rate);
        p.Configure(new EffectSettings(400, [12, 12, 12, 12, 12, 12, 12, 12, 12, 12]));
        float peak = 0;
        for (int block = 0; block < 100; block++)
        {
            var b = Sine(100, 0.9, 480, block * 480);
            p.Process(b);
            peak = Math.Max(peak, Peak(b));
        }
        Assert.True(peak <= AudioProcessor.LimiterCeiling + 1e-4f, $"peak {peak}");
    }

    [Fact]
    public void GuardMutesUnattenuatedAudioInsteadOfAmplifyingIt()
    {
        var p = new AudioProcessor(Rate, EnhancerEngine.RecoveryGain, EnhancerEngine.GuardThreshold);
        int events = 0;
        p.GuardTripped += () => events++;

        var loud = Sine(440, 0.5, 480); // full-level audio that should have been at 1/256
        p.Process(loud);
        Assert.All(loud, x => Assert.Equal(0f, x));
        Assert.Equal(1, p.GuardTrips);
        Assert.Equal(1, events);

        // Correctly attenuated audio right after is still held muted for the hold time...
        var quiet = Sine(440, 0.5 / 256, 480);
        p.Process(quiet);
        Assert.All(quiet, x => Assert.Equal(0f, x));

        // ...then resumes.
        for (int i = 0; i < AudioProcessor.GuardHoldMs / 10; i++) p.Process(Sine(440, 0.5 / 256, 480));
        var after = Sine(440, 0.5 / 256, 480, 480);
        p.Process(after);
        Assert.True(Peak(after) > 0.4f);
    }

    [Theory]
    [InlineData(1000, 6, 1000, 6)]   // boosted band at its centre
    [InlineData(1000, -6, 1000, -6)] // cut
    [InlineData(1000, 6, 62.5, 0)]   // far-away frequency barely affected
    public void EqualizerBandHasExpectedGain(double bandFreq, double gainDb, double testFreq, double expectedDb)
    {
        var bands = new double[EqBands.Count];
        bands[Array.IndexOf(EqBands.Frequencies, bandFreq)] = gainDb;
        var p = new AudioProcessor(Rate);
        p.Configure(new EffectSettings(100, bands));
        double measuredDb = 20 * Math.Log10(SteadyPeak(p, testFreq, 0.1) / 0.1);
        Assert.InRange(measuredDb, expectedDb - 0.5, expectedDb + 0.5);
    }

    [Fact]
    public void NonFiniteInputNeverReachesTheOutput()
    {
        var p = new AudioProcessor(Rate);
        p.Configure(new EffectSettings(100, [3, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        var b = Sine(440, 0.5, 480);
        b[10] = float.NaN;
        b[11] = float.PositiveInfinity;
        p.Process(b);
        Assert.All(b, x => Assert.True(float.IsFinite(x)));
        var next = Sine(440, 0.5, 480, 480);
        p.Process(next);
        Assert.All(next, x => Assert.True(float.IsFinite(x)));
    }
}

public class PresetTests
{
    [Fact]
    public void BuiltInPresetsAreValidAndUniquelyNamed()
    {
        Assert.Equal(BuiltInPresets.All.Count, BuiltInPresets.All.Select(p => p.Name.ToLowerInvariant()).Distinct().Count());
        foreach (var p in BuiltInPresets.All)
        {
            Assert.Equal(EqBands.Count, p.BandsDb.Count);
            Assert.All(p.BandsDb, g => Assert.InRange(g, -EqBands.MaxGainDb, EqBands.MaxGainDb));
            Assert.InRange(p.VolumePercent, 0, EqBands.MaxVolumePercent);
            Assert.True(p.Matches(p.ToSettings()));
        }
    }

    [Fact]
    public void SanitizeClampsAndPadsBands()
    {
        var s = new EffectSettings(9999, [50, -50, double.NaN]).Sanitized();
        Assert.Equal(EqBands.MaxVolumePercent, s.VolumePercent);
        Assert.Equal(EqBands.Count, s.BandsDb.Count);
        Assert.Equal(EqBands.MaxGainDb, s.BandsDb[0]);
        Assert.Equal(-EqBands.MaxGainDb, s.BandsDb[1]);
        Assert.Equal(0, s.BandsDb[2]);
    }
}

public sealed class EnhanceSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dismusic-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NormalizesEnhanceSectionFromDisk()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, """
        {
          "Enhance": { "Spotify.EXE": { "Enabled": true, "VolumePercent": 900, "BandsDb": [ 20 ] } },
          "CustomPresets": [ { "Name": "Flat", "BandsDb": [] }, { "Name": " Mine ", "VolumePercent": 150, "BandsDb": [1,2,3,4,5,6,7,8,9,10,11] } ],
          "SavedAppVolumes": { "Brave.exe": 1.7 }
        }
        """);
        var s = new SettingsStore(file).Load(out _);
        var e = s.Enhance["spotify.exe"];
        Assert.Equal(EqBands.MaxVolumePercent, e.VolumePercent);
        Assert.Equal(EqBands.Count, e.BandsDb.Length);
        Assert.Equal(EqBands.MaxGainDb, e.BandsDb[0]);
        var preset = Assert.Single(s.CustomPresets); // the one named like a built-in is dropped
        Assert.Equal("Mine", preset.Name);
        Assert.Equal(EqBands.Count, preset.BandsDb.Length);
        Assert.Equal(1f, s.SavedAppVolumes["brave.exe"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

public class ProcessTreeTests
{
    [Fact]
    public void TreeIncludesAllDescendantsWhateverTheirExe()
    {
        var table = new ProcessTable([new(1, 0, "steam.exe"), new(2, 1, "game.exe"), new(3, 2, "helper.exe"), new(4, 0, "other.exe")]);
        Assert.Equal(new HashSet<uint> { 1, 2, 3 }, table.TreeOf(1));
        Assert.Equal(new HashSet<uint> { 4 }, table.TreeOf(4));
    }
}
