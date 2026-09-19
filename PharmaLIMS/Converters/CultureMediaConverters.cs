using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PharmaLIMS;

public sealed class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string status = value?.ToString()?.Trim() ?? string.Empty;

        return status.ToLowerInvariant() switch
        {
            "accepted" or "released" or "pass" => new SolidColorBrush(Color.FromRgb(22, 163, 74)),
            "quarantine" or "pending" => new SolidColorBrush(Color.FromRgb(234, 179, 8)),
            "under release" => new SolidColorBrush(Color.FromRgb(251, 146, 60)),
            "rejected" or "fail" or "failed" => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class ResultToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string result = value?.ToString()?.Trim() ?? string.Empty;

        return result.ToLowerInvariant() switch
        {
            "pass" or "passed" => new SolidColorBrush(Color.FromRgb(22, 163, 74)),
            "fail" or "failed" => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            "pending" => new SolidColorBrush(Color.FromRgb(234, 179, 8)),
            _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class ExpiryWarningConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DateTime expiryDate && (expiryDate - DateTime.Today).Days <= 7)
            return Visibility.Visible;

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool boolValue ? !boolValue : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
