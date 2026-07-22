using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OrionTV.Desktop.App;

/// <summary>空字符串 → Visible（用于 TextBox 水印占位）。</summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
