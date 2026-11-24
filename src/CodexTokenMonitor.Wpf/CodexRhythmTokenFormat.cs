using System.Globalization;
using System.Windows.Data;

namespace CodexTokenMonitor;

internal static class CodexRhythmTokenFormat
{
    internal const string Missing = "—";

    internal static string Format(double value, IFormatProvider? formatProvider = null)
    {
        if (!double.IsFinite(value)) return Missing;

        // Advance the unit when rounding in the smaller unit would display 1000.
        var (scale, suffix) = Math.Abs(value) switch
        {
            >= 999_995_000 => (1_000_000_000d, "B"),
            >= 999_995 => (1_000_000d, "M"),
            >= 999.995 => (1_000d, "K"),
            _ => (1d, "")
        };
        return (value / scale).ToString("#,0.##", formatProvider) + suffix;
    }
}

public sealed class CodexRhythmTokenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double? tokens = value switch
        {
            long count => count,
            int count => count,
            double count => count,
            decimal count => (double)count,
            _ => null
        };
        return tokens is { } total
            ? CodexRhythmTokenFormat.Format(total, culture)
            : CodexRhythmTokenFormat.Missing;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
