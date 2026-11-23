using System.Globalization;
using System.Windows.Data;
using UniBackup.Core.Common;

namespace UniBackup.UI.Converters;

public class BytesToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return FormattingUtils.FormatBytes(bytes);
        }
        if (value is int intBytes)
        {
            return FormattingUtils.FormatBytes(intBytes);
        }
        if (value is double dBytes)
        {
            return FormattingUtils.FormatBytes((long)dBytes);
        }
        return "-";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw freshNotSupportedException();

    private static NotSupportedException freshNotSupportedException() => new();
}
