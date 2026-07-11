using System.ComponentModel;
using System.Runtime.CompilerServices;
using Brush = System.Windows.Media.Brush;

namespace SonarLite.Models;

/// <summary>A mixer channel: one of the three buses, with its own fader, mute and level meter.</summary>
public sealed class BusChannel : INotifyPropertyChanged
{
    private float _volume = 1f;
    private bool _isMuted;
    private float _peak;

    public required SessionClass Class { get; init; }
    public required string Name { get; init; }
    public required Brush Accent { get; init; }

    public float Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(clamped - _volume) < 0.0001f) return;
            _volume = clamped;
            OnChanged();
            OnChanged(nameof(VolumePercent));
        }
    }

    public int VolumePercent
    {
        get => (int)Math.Round(_volume * 100);
        set => Volume = value / 100f;
    }

    public bool IsMuted
    {
        get => _isMuted;
        set { if (_isMuted == value) return; _isMuted = value; OnChanged(); }
    }

    /// <summary>Smoothed 0..1 peak level driving the channel meter.</summary>
    public float Peak
    {
        get => _peak;
        set
        {
            if (Math.Abs(value - _peak) < 0.001f) return;
            _peak = value;
            // Ticks up to 20x/sec per bus off the meter timer; a cached args instance avoids
            // allocating one every tick the same way AppSession.Peak already does.
            PropertyChanged?.Invoke(this, PeakChangedArgs);
        }
    }

    private static readonly PropertyChangedEventArgs PeakChangedArgs = new(nameof(Peak));

    /// <summary>What the bus actually contributes to the mix, before the ChatMix dial.</summary>
    public float EffectiveGain => IsMuted ? 0f : _volume;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
