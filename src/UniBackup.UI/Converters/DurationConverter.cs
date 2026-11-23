using System.Globalization;
using System.Windows.Data;
using UniBackup.Core.Common;

namespace UniBackup.UI.Converters;

public class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double sec)
        {
            return FormattingUtils.FormatDuration(TimeSpan.FromSeconds(sec));
        }
        if (value is long lSec)
        {
            return FormattingUtils.FormatDuration(TimeSpan.FromSeconds(lSec));
        }
        if (value is int iSec)
        {
            return FormattingUtils.FormatDuration(TimeSpan.FromSeconds(iSec));
        }
        if (value is TimeSpan ts)
        {
            return FormattingUtils.FormatDuration(ts);
        }
        return "-";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
