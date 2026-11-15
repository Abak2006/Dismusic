namespace Dismusic.Core.Effects;

public static class EqBands
{
    /// <summary>Centre frequencies of the 10-band graphic equalizer (octave spacing).</summary>
    public static readonly double[] Frequencies = [31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public static readonly string[] Labels = ["31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];

    public const int Count = 10;
    public const double MaxGainDb = 12;
    public const int MaxVolumePercent = 400;
}

/// <summary>Effect parameters for one processed stream. Immutable; replace it to change settings.</summary>
/// <param name="VolumePercent">0–400. Above 100 boosts; the limiter keeps peaks below clipping.</param>
/// <param name="BandsDb">10 gains in dB (±12), see <see cref="EqBands.Frequencies"/>.</param>
public sealed record EffectSettings(int VolumePercent, IReadOnlyList<double> BandsDb)
{
    public static readonly EffectSettings Flat = new(100, new double[EqBands.Count]);

    public EffectSettings Sanitized() => new(
        Math.Clamp(VolumePercent, 0, EqBands.MaxVolumePercent),
        Enumerable.Range(0, EqBands.Count)
            .Select(i => i < BandsDb.Count && double.IsFinite(BandsDb[i]) ? Math.Clamp(BandsDb[i], -EqBands.MaxGainDb, EqBands.MaxGainDb) : 0)
            .ToArray());
}

public sealed record EffectPreset(string Name, int VolumePercent, IReadOnlyList<double> BandsDb, bool IsBuiltIn)
{
    public EffectSettings ToSettings() => new EffectSettings(VolumePercent, BandsDb).Sanitized();

    public bool Matches(EffectSettings s) =>
        s.VolumePercent == VolumePercent && s.BandsDb.Zip(BandsDb).All(p => Math.Abs(p.First - p.Second) < 0.05);
}

public static class BuiltInPresets
{
    public static readonly IReadOnlyList<EffectPreset> All =
    [
        new("Flat", 100, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0], true),
        new("Bass boost", 100, [6, 5, 4, 2, 0, 0, 0, 0, 0, 0], true),
        new("Music", 100, [4, 3, 1, 0, -1, -1, 0, 2, 3, 3], true),
        new("Movie", 110, [4, 3, 1, 0, -1, 1, 2, 2, 1, 0], true),
        new("Voice / podcast", 110, [-6, -4, -2, 0, 1, 3, 4, 3, 1, -2], true),
        new("Gaming", 100, [-2, -1, 0, 0, 1, 2, 4, 4, 2, 0], true),
        new("Treble boost", 100, [0, 0, 0, 0, 0, 1, 2, 4, 5, 5], true),
        new("Loud", 200, [2, 1, 0, 0, 0, 0, 0, 1, 1, 0], true),
        new("Max boost", 300, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0], true),
        new("Quiet", 50, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0], true),
    ];

    public static bool IsBuiltInName(string name) =>
        All.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
