using System.Globalization;
using System.Windows.Data;

namespace UniBackup.UI.Converters;

public class RatioToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length >= 2 &&
            values[0] is double current &&
            values[1] is double total &&
            total > 0)
        {
            var maxWidth = 100.0;
            if (values.Length >= 3 && values[2] is double containerWidth && containerWidth > 0)
            {
                maxWidth = containerWidth;
            }
            else if (parameter is double pWidth)
            {
                maxWidth = pWidth;
            }

            var ratio = Math.Clamp(current / total, 0.0, 1.0);
            return ratio * maxWidth;
        }

        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
