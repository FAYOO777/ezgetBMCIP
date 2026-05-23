using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace EzGetBmcIp;

public sealed class StepStateToCircleFillConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = (StepState)value;
        return state switch
        {
            StepState.Done => new SolidColorBrush(Color.FromRgb(56, 138, 52)),
            StepState.Active => new SolidColorBrush(Color.FromRgb(0, 122, 204)),
            StepState.Failed => new SolidColorBrush(Color.FromRgb(161, 38, 13)),
            _ => new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StepStateToCircleStrokeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = (StepState)value;
        return state == StepState.Pending
            ? new SolidColorBrush(Color.FromRgb(180, 180, 180))
            : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StepStateToCheckVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = (StepState)value;
        return state == StepState.Done ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StepStateToCrossVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = (StepState)value;
        return state == StepState.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StepStateToLineFillConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = (StepState)value;
        return state == StepState.Done
            ? new SolidColorBrush(Color.FromRgb(56, 138, 52))
            : new SolidColorBrush(Color.FromRgb(200, 200, 200));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StepStateToBadgeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = (StepState)value;
        return state switch
        {
            StepState.Done => new SolidColorBrush(Color.FromRgb(56, 138, 52)),
            StepState.Active => new SolidColorBrush(Color.FromRgb(0, 122, 204)),
            StepState.Failed => new SolidColorBrush(Color.FromRgb(161, 38, 13)),
            _ => new SolidColorBrush(Color.FromRgb(200, 200, 200))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StepStateToBadgeTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = (StepState)value;
        return state switch
        {
            StepState.Done => "✓ 已完成",
            StepState.Active => "处理中",
            StepState.Failed => "! 失败",
            _ => "等待中"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StepStateToTitleForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = (StepState)value;
        return state switch
        {
            StepState.Done => new SolidColorBrush(Color.FromRgb(56, 138, 52)),
            StepState.Failed => new SolidColorBrush(Color.FromRgb(161, 38, 13)),
            _ => new SolidColorBrush(Color.FromRgb(31, 31, 31))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

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

/// <summary>Returns white text for dark badge backgrounds, dark text for light badge backgrounds.</summary>
public sealed class BadgeForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = (StepState)value;
        return state == StepState.Pending
            ? new SolidColorBrush(Color.FromRgb(97, 97, 97))
            : new SolidColorBrush(Colors.White);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
