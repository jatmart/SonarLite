using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SonarLite.Models;

public sealed class EqBand(string label, int frequencyHz) : INotifyPropertyChanged
{
    private double _gainDb;

    public string Label { get; } = label;
    public int FrequencyHz { get; } = frequencyHz;

    public double GainDb
    {
        get => _gainDb;
        set { _gainDb = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
