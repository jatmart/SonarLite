using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using NAudio.CoreAudioApi;

namespace SonarLite.Models;

public sealed class AppSession : INotifyPropertyChanged
{
    private float _userVolume;
    private bool _isMuted;
    private SessionClass _classification;
    private float _peak;

    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>
    /// Rebindable rather than init-only: routing a pid to a different device closes this session's
    /// underlying stream and opens a new COM session object on the new device. Without rebinding,
    /// the meter and volume writes silently target the dead original session forever.
    /// </summary>
    public required AudioSessionControl Control { get; set; }
    public ImageSource? Icon { get; init; }

    /// <summary>
    /// Extra pids sharing this tile's ProcessName (e.g. a multi-process app like Discord that opens
    /// more than one audio session under the same name) -- merged into one row instead of showing a
    /// duplicate tile per pid. Volume/mute/classification apply to all of them together; each still
    /// gets its own routing and tap-liveness tracking since WASAPI state is genuinely per-session.
    /// </summary>
    public Dictionary<int, AudioSessionControl> AliasControls { get; } = new();

    public IEnumerable<int> AllProcessIds => AliasControls.Count == 0
        ? [ProcessId]
        : [ProcessId, .. AliasControls.Keys];

    public float UserVolume
    {
        get => _userVolume;
        set
        {
            _userVolume = Math.Clamp(value, 0f, 1f);
            OnChanged();
            OnChanged(nameof(VolumePercent));
        }
    }

    public int VolumePercent
    {
        get => (int)Math.Round(_userVolume * 100);
        set => UserVolume = value / 100f;
    }

    public bool IsMuted
    {
        get => _isMuted;
        set { _isMuted = value; OnChanged(); }
    }

    public SessionClass Classification
    {
        get => _classification;
        set { _classification = value; OnChanged(); }
    }

    /// <summary>
    /// Live 0..1 peak from the session's meter. Assigning it must not look like a user edit, so it
    /// is deliberately kept off the PropertyChanged path that persists prefs and re-routes audio.
    /// </summary>
    public float Peak
    {
        get => _peak;
        set
        {
            if (Math.Abs(value - _peak) < 0.004f) return;
            _peak = value;
            PropertyChanged?.Invoke(this, PeakChangedArgs);
        }
    }

    private static readonly PropertyChangedEventArgs PeakChangedArgs = new(nameof(Peak));

    /// <summary>Reads the loudest of this tile's underlying sessions; 0 if all have gone away.</summary>
    public float ReadPeak()
    {
        float peak = ReadPeak(Control);
        foreach (var control in AliasControls.Values)
            peak = Math.Max(peak, ReadPeak(control));
        return peak;
    }

    private static float ReadPeak(AudioSessionControl control)
    {
        try { return control.AudioMeterInformation.MasterPeakValue; }
        catch { return 0f; }
    }

    /// <summary>Applies this tile's volume/mute to one underlying session. externalFactor is
    /// per-pid (tapped pids bypass the bus gain here since the engine applies it instead), so the
    /// caller applies it once per control rather than once per tile.</summary>
    public void ApplyVolume(AudioSessionControl control, float externalFactor)
    {
        try
        {
            control.SimpleAudioVolume.Volume = IsMuted ? 0f : UserVolume * externalFactor;
        }
        catch
        {
            // Session may have gone away between enumeration and apply; pruned next refresh.
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
