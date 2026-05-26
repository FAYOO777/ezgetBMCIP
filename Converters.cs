using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EzGetBmcIp;

// ════════════════════════════════════════════════════════════════════
//  Generic converters (kept — no theme coupling)
// ════════════════════════════════════════════════════════════════════

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => !(bool)value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => !(bool)value;
}

public sealed class BoolToVisibilityInvertedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v != Visibility.Visible;
}

public sealed class AppPhaseToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is AppPhase phase && parameter is string target)
            return phase.ToString() == target ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
