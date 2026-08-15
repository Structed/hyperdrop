using System.Globalization;

namespace HyperVDrop.App;

/// <summary>
/// Formatting helpers for sizes, rates and durations shown in the transfer list.
/// </summary>
internal static class Humanize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long value)
    {
        if (value < 0)
        {
            return "-";
        }

        double size = value;
        var unit = 0;

        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Bytes and kilobytes rarely benefit from decimals; larger units do.
        var format = unit switch
        {
            0 => "0",
            1 => "0",
            _ => size >= 100 ? "0" : "0.0",
        };

        return string.Create(CultureInfo.CurrentCulture, $"{size.ToString(format, CultureInfo.CurrentCulture)} {Units[unit]}");
    }

    public static string Rate(double? bytesPerSecond) =>
        bytesPerSecond is null or <= 0
            ? string.Empty
            : $"{Bytes((long)bytesPerSecond.Value)}/s";

    public static string Duration(TimeSpan? span)
    {
        if (span is null)
        {
            return string.Empty;
        }

        var value = span.Value;

        if (value < TimeSpan.Zero)
        {
            return string.Empty;
        }

        if (value.TotalHours >= 1)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}");
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value.Minutes}:{value.Seconds:00}");
    }
}
