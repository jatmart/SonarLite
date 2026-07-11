using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SonarLite.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace SonarLite;

/// <summary>
/// Maps a linear 0..1 peak onto the meter's horizontal scale. Raw amplitude looks dead for most
/// real program material, so the curve is bent toward the loud end the way a dB-ish meter reads.
/// </summary>
public sealed class PeakToScaleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var peak = value is float f ? f : 0f;
        return (double)Math.Clamp(MathF.Pow(peak, 0.55f), 0f, 1f);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element while the bound flag is true (used to hide the empty-channel hint).</summary>
public sealed class BoolToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colours a tile's meter with the accent of whichever bus the app currently sits on.</summary>
public sealed class ClassToBrushConverter : IValueConverter
{
    public Brush Game { get; set; } = Brushes.LimeGreen;
    public Brush Chat { get; set; } = Brushes.DodgerBlue;
    public Brush Media { get; set; } = Brushes.HotPink;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SessionClass cls
            ? cls switch { SessionClass.Game => Game, SessionClass.Chat => Chat, _ => Media }
            : Media;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
