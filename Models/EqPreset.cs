namespace SonarLite.Models;

/// <summary>Gains are ordered to match the band list: 31,62,125,250,500,1k,2k,4k,8k,16k Hz.</summary>
public sealed record EqPreset(string Name, double[] Gains)
{
    public override string ToString() => Name;

    public static readonly EqPreset Flat = new("Flat", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    // Bass-heavy V-curve in the spirit of SteelSeries GG's "Immersive": big low-shelf lift,
    // scooped low-mids, sparkle on top. Preamp compensation happens at apply time.
    public static readonly EqPreset Immersive = new("Immersive", [8, 7, 5, 2, -1, -1.5, 1, 3.5, 5, 6]);

    public static readonly EqPreset BassBoost = new("Bass Boost", [6, 5, 3.5, 1.5, 0, 0, 0, 0, 0, 0]);

    public static readonly EqPreset TrebleBoost = new("Treble Boost", [0, 0, 0, 0, 0, 1, 2.5, 4, 5, 5.5]);

    public static readonly EqPreset VocalClarity = new("Vocal Clarity", [-2, -1, 0, 1.5, 3, 3, 2, 0, -1, -1]);

    public static readonly EqPreset Footsteps = new("Gaming (Footsteps)", [-3, -2, -1, 0, 1, 3, 4.5, 4, 2, 1]);

    public static readonly EqPreset Cinema = new("Cinema", [4, 3, 1, 0, -1, -1, 0, 1.5, 3, 3.5]);

    public static readonly IReadOnlyList<EqPreset> All =
        [Flat, Immersive, BassBoost, TrebleBoost, VocalClarity, Footsteps, Cinema];
}
