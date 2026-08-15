using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HyperVDrop.App.Converters;

/// <summary>
/// Maps a boolean to <see cref="Visibility"/>, optionally inverted and optionally collapsing to
/// <see cref="Visibility.Hidden"/> instead of <see cref="Visibility.Collapsed"/>.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public bool UseHidden { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;

        if (Invert)
        {
            flag = !flag;
        }

        return flag
            ? Visibility.Visible
            : UseHidden ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only when a string has content.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasContent = !string.IsNullOrWhiteSpace(value as string);

        if (Invert)
        {
            hasContent = !hasContent;
        }

        return hasContent ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
