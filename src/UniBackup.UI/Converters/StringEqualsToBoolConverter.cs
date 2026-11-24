using System.Globalization;
using System.Windows.Data;

namespace UniBackup.UI.Converters;

public class StringEqualsToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var str = value?.ToString();
        var target = parameter?.ToString();
        return string.Equals(str, target, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b)
        {
            return parameter?.ToString() ?? "";
        }
        return Binding.DoNothing;
    }
}
