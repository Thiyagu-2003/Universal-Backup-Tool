using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace UniBackup.UI.Converters;

public class StatusToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value?.ToString()?.ToUpperInvariant() ?? "";

        if (status.Contains("SUCCESS"))
        {
            return (Application.Current?.TryFindResource("IconCheck") as Geometry) ?? Geometry.Empty;
        }
        if (status.Contains("PARTIAL") || status.Contains("WARN"))
        {
            return (Application.Current?.TryFindResource("IconWarningCircle") as Geometry) ?? Geometry.Empty;
        }
        if (status.Contains("FAIL") || status.Contains("ERROR"))
        {
            return (Application.Current?.TryFindResource("IconClose") as Geometry) ?? Geometry.Empty;
        }
        if (status.Contains("PAUSE"))
        {
            return (Application.Current?.TryFindResource("IconPauseCircle") as Geometry) ?? Geometry.Empty;
        }
        if (status.Contains("RUN") || status.Contains("SPIN"))
        {
            return (Application.Current?.TryFindResource("IconSpinner") as Geometry) ?? Geometry.Empty;
        }

        return (Application.Current?.TryFindResource("IconClock") as Geometry) ?? Geometry.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
