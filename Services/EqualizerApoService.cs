using System.IO;

namespace SonarLite.Services;

/// <summary>
/// SonarLite no longer uses Equalizer APO: all three bus EQs run in our own DSP chain, and an APO
/// on the headset endpoint would re-apply one bus's curve to every other bus's mixed-back audio.
/// This exists only to defuse a curve an older build of SonarLite left behind in APO's config.
/// </summary>
public sealed class EqualizerApoService
{
    private const string ConfigPath = @"C:\Program Files\EqualizerAPO\config\config.txt";

    public bool IsInstalled => File.Exists(ConfigPath);

    /// <summary>True when the config still carries a curve that would stack on top of our DSP.</summary>
    public bool HasStaleCurve()
    {
        try
        {
            return IsInstalled && File.ReadAllText(ConfigPath).Contains("GraphicEQ:", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Flatten a curve we previously wrote. Only touches configs that look like ours (a GraphicEQ
    /// line), so a hand-written Equalizer APO setup is left alone.
    /// </summary>
    public bool NeutralizeIfOurs()
    {
        if (!HasStaleCurve()) return false;
        try
        {
            File.WriteAllText(ConfigPath, "Preamp: 0 dB\n");
            return true;
        }
        catch
        {
            // Program Files write can fail without admin; surfaced via the status label.
            return false;
        }
    }
}
