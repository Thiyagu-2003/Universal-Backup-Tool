using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace UniBackup.UI.Converters;

public class KindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value?.ToString()?.ToUpperInvariant() ?? "";
        var isSoft = parameter?.ToString()?.Equals("soft", StringComparison.OrdinalIgnoreCase) == true;

        if (kind.Contains("SQL"))
        {
            return (Application.Current?.TryFindResource(isSoft ? "BrushOrchidSoft" : "BrushOrchid") as Brush)
                ?? new SolidColorBrush(Color.FromRgb(0x7C, 0x4D, 0xFF));
        }
        if (kind.Contains("IIS") || kind.Contains("SITE"))
        {
            return (Application.Current?.TryFindResource(isSoft ? "BrushTealSoft" : "BrushTeal") as Brush)
                ?? new SolidColorBrush(Color.FromRgb(0x00, 0xA0, 0xAE));
        }

        // Folder is default (Cobalt)
        return (Application.Current?.TryFindResource(isSoft ? "BrushCobaltSoft" : "BrushCobalt") as Brush)
            ?? new SolidColorBrush(Color.FromRgb(0x2D, 0x5B, 0xFF));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
