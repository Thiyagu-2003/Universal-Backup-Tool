using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace UniBackup.UI.Converters;

public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value?.ToString()?.ToUpperInvariant() ?? "";
        var isSoft = parameter?.ToString()?.Equals("soft", StringComparison.OrdinalIgnoreCase) == true;

        if (status.Contains("SUCCESS"))
        {
            return (Application.Current?.TryFindResource(isSoft ? "BrushJadeSoft" : "BrushJade") as Brush)
                ?? new SolidColorBrush(Color.FromRgb(0x00, 0x94, 0x6A));
        }
        if (status.Contains("PARTIAL") || status.Contains("WARN"))
        {
            return (Application.Current?.TryFindResource(isSoft ? "BrushSaffronSoft" : "BrushSaffron") as Brush)
                ?? new SolidColorBrush(Color.FromRgb(0xC9, 0x7A, 0x00));
        }
        if (status.Contains("FAIL") || status.Contains("ERROR"))
        {
            return (Application.Current?.TryFindResource(isSoft ? "BrushCoralSoft" : "BrushCoral") as Brush)
                ?? new SolidColorBrush(Color.FromRgb(0xE0, 0x29, 0x3F));
        }

        return (Application.Current?.TryFindResource(isSoft ? "BrushSunken" : "BrushInkMuted") as Brush)
            ?? new SolidColorBrush(Color.FromRgb(0x4A, 0x5A, 0x70));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
