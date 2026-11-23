using System.Globalization;
using System.Windows.Data;

namespace UniBackup.UI.Converters;

public class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime dt)
        {
            return "Never run";
        }

        var diff = DateTime.UtcNow - dt.ToUniversalTime();
        if (diff.TotalSeconds < 60) return "Just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} h ago";
        if (diff.TotalDays < 30) return $"{(int)diff.TotalDays} d ago";
        return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
