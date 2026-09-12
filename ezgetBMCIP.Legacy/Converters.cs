using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EzGetBmcIp;

namespace EzGetBmcIp.Legacy
{
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Visible;
    }

    public sealed class InvertBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => !(bool)value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => !(bool)value;
    }

    // Presentation-only conversion: the shared SessionPageKind selects a Legacy
    // page without exposing workflow, failure, or network-lifecycle tests to XAML.
    public sealed class SessionPageKindToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var expectedPage = parameter as string;
            return value is SessionPageKind page
                && string.Equals(page.ToString(), expectedPage, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public sealed class PresentationSeverityToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            switch (value is PresentationSeverity severity ? severity : PresentationSeverity.Normal)
            {
                case PresentationSeverity.Progress:
                    return Brushes.DodgerBlue;
                case PresentationSeverity.Warning:
                    return Brushes.DarkGoldenrod;
                case PresentationSeverity.Error:
                    return Brushes.Firebrick;
                case PresentationSeverity.Success:
                    return Brushes.ForestGreen;
                case PresentationSeverity.Normal:
                default:
                    return Brushes.DimGray;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public sealed class LegacyRuntimeStageBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var state = value is ModernStageState ? (ModernStageState)value : ModernStageState.Pending;
            switch (state)
            {
                case ModernStageState.Done: return Brushes.ForestGreen;
                case ModernStageState.Active: return Brushes.DodgerBlue;
                case ModernStageState.Attention: return Brushes.DarkGoldenrod;
                case ModernStageState.Failed: return Brushes.Firebrick;
                default: return Brushes.Gray;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public sealed class LegacyRuntimeActionVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            ModernActionKind action;
            if (value is ModernActionKind)
                action = (ModernActionKind)value;
            else
                action = ModernActionKind.None;
            ModernActionKind expected;
            return Enum.TryParse(parameter as string, out expected) && action == expected
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
