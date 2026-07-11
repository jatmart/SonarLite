namespace SonarLite.Models;

/// <summary>What kind of output a preset is voiced for. <see cref="DeviceKind.Any"/> presets are
/// device-agnostic and never move on their own.</summary>
public enum DeviceKind { Any, Speakers, Headset }

/// <summary>
/// Gains are ordered to match the band list: 31,62,125,250,500,1k,2k,4k,8k,16k Hz.
///
/// <paramref name="PreampDb"/> is added back on top of the automatic makeup gain, which otherwise
/// normalizes every curve's loudest point to exactly unity. Anything above 0 here deliberately runs
/// the bus hot -- the SafetyLimiter downstream rides the peaks back down, which is a soft gain
/// reduction, not the hard clipping this used to do. Keep it small.
///
/// <paramref name="For"/> opts a preset into device tracking: a bus set to a Speakers- or
/// Headset-voiced preset follows the live render device, swapping to its counterpart whenever the
/// output changes. The two are a matched pair -- same Immersive character, different corrections --
/// so picking either one really means "voice this bus for whatever I'm listening on".
/// </summary>
public sealed record EqPreset(string Name, double[] Gains, double PreampDb = 0, DeviceKind For = DeviceKind.Any)
{
    public override string ToString() => Name;

    /// <summary>Preamp for a saved preset name, or 0 for a hand-edited curve that matches no preset.</summary>
    public static double PreampFor(string? presetName) =>
        All.FirstOrDefault(p => p.Name == presetName)?.PreampDb ?? 0;

    /// <summary>
    /// The preset a bus currently on <paramref name="presetName"/> should switch to now that the
    /// output is <paramref name="kind"/>, or null to leave it alone. Null covers everything that
    /// isn't a device-tracking preset: a hand-edited curve, Flat, Immersive, and the rest all stay
    /// exactly where the user put them.
    /// </summary>
    public static EqPreset? ForDevice(string? presetName, DeviceKind kind)
    {
        var current = All.FirstOrDefault(p => p.Name == presetName);
        if (current is null || current.For == DeviceKind.Any || current.For == kind) return null;
        return All.FirstOrDefault(p => p.For == kind);
    }

    public static readonly EqPreset Flat = new("Flat", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    // Bass-heavy V-curve in the spirit of SteelSeries GG's "Immersive": big low-shelf lift,
    // scooped low-mids, sparkle on top. Its stacked bass bands make this the peakiest curve we
    // ship (+11.2 dB at 59 Hz), so strict normalization leaves it noticeably quieter than the
    // others -- the preamp buys a little of that back.
    public static readonly EqPreset Immersive = new("Immersive", [8, 7, 5, 2, -1, -1.5, 1, 3.5, 5, 6], PreampDb: 2);

    // Immersive, corrected for the Arctis Nova Pro's measured response. Same V-curve character;
    // three independent measurements (SoundGuys wired + wireless, Igor's Lab) agree on the shape
    // that drives the differences from Immersive:
    //   - Sub-bass genuinely rolls off stock, with a dip near 70 Hz -- and 40mm closed-back drivers
    //     with a seal CAN deliver deep bass, so the big 31/62 lift is real correction, kept nearly
    //     intact. This is the opposite of the speaker case.
    //   - 150-400 Hz is already slightly overemphasized stock, so Immersive's +2 at 250 becomes a
    //     small cut rather than compounding a known muddiness.
    //   - The 4 kHz notch every source measured lines up exactly with Immersive's sparkle lift, so
    //     that band is both correction and character at once.
    //   - 8 kHz is a driver resonance peak (metallic, sibilant); Immersive's +5 would stack straight
    //     onto it, so it's flipped to a cut. Sparkle is bought back at 16 kHz instead, where the
    //     headset genuinely rolls off above ~10 kHz.
    // True composite peak +9.2 dB at 34 Hz -- gentler than Immersive's +11.2.
    public static readonly EqPreset NovaPro =
        new("Nova Pro", [7, 6, 2.5, -0.5, -1, -1.5, 1, 3.5, -2, 5], PreampDb: 2, For: DeviceKind.Headset);

    // Immersive, reallocated for what a 2" full-range driver + passive radiator can physically do.
    // The Pebble V2 is spec'd from 100 Hz, and its published response is identical to the V1's --
    // Creative doubled the amplifier (8W vs 4.4W) but did not revoice the acoustics.
    //   - 31 Hz is cut, not boosted. It's below the radiator's tuning, where the PR unloads the cone:
    //     excursion climbs steeply for no acoustic output. The extra watts buy nothing down there
    //     except the ability to slam an unloaded cone into its limit, so this is the one band where
    //     more power makes boosting *more* hazardous. Nothing audible is lost -- the driver was never
    //     turning that energy into sound.
    //   - That energy moves to 125 Hz, which becomes the curve's peak. It's inside the driver's real
    //     passband, so this is where the speaker can actually deliver perceived punch and warmth.
    //   - Treble stays under Immersive's: this speaker's full-range cone is reported to break up in
    //     the high end before it does anything else, so 8k/16k are the last place to spend headroom.
    // True composite peak +7.7 dB at 124 Hz -- the loudest point now sits where the driver works,
    // instead of at 59 Hz where Immersive put it.
    public static readonly EqPreset PebbleV2 =
        new("Pebble V2", [-4, 4, 6, 3, -2, -2.5, 0.5, 3, 3.5, 4.5], PreampDb: 2, For: DeviceKind.Speakers);

    public static readonly EqPreset BassBoost = new("Bass Boost", [6, 5, 3.5, 1.5, 0, 0, 0, 0, 0, 0]);

    public static readonly EqPreset TrebleBoost = new("Treble Boost", [0, 0, 0, 0, 0, 1, 2.5, 4, 5, 5.5]);

    public static readonly EqPreset VocalClarity = new("Vocal Clarity", [-2, -1, 0, 1.5, 3, 3, 2, 0, -1, -1]);

    public static readonly EqPreset Footsteps = new("Gaming (Footsteps)", [-3, -2, -1, 0, 1, 3, 4.5, 4, 2, 1]);

    public static readonly EqPreset Cinema = new("Cinema", [4, 3, 1, 0, -1, -1, 0, 1.5, 3, 3.5]);

    public static readonly IReadOnlyList<EqPreset> All =
        [Flat, Immersive, NovaPro, PebbleV2, BassBoost, TrebleBoost, VocalClarity, Footsteps, Cinema];
}
