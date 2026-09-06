using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace CodexTokenMonitor;

internal static class QuotaCycleModelPalette
{
    private static readonly Color[] FallbackColors =
    [
        Color.FromRgb(14, 116, 144),
        Color.FromRgb(79, 70, 229),
        Color.FromRgb(190, 24, 93),
        Color.FromRgb(21, 128, 61),
        Color.FromRgb(180, 83, 9),
        Color.FromRgb(124, 58, 237),
        Color.FromRgb(3, 105, 161),
        Color.FromRgb(194, 65, 12)
    ];

    public static Color GetColor(string? modelId)
    {
        var normalized = CodexModelCost.NormalizeModelId(modelId);
        if (normalized.Contains("astra", StringComparison.Ordinal)) return Color.FromRgb(109, 76, 219);
        if (normalized.Contains("sol", StringComparison.Ordinal)) return Color.FromRgb(13, 148, 136);
        if (normalized.Contains("terra", StringComparison.Ordinal)) return Color.FromRgb(37, 99, 235);
        if (normalized.Contains("luna", StringComparison.Ordinal)) return Color.FromRgb(217, 119, 6);
        if (normalized.Contains("spark", StringComparison.Ordinal)) return Color.FromRgb(225, 29, 72);
        if (normalized.Contains("mini", StringComparison.Ordinal)) return Color.FromRgb(8, 145, 178);
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "未识别") return Color.FromRgb(107, 114, 128);

        var hash = 17;
        foreach (var character in normalized)
        {
            hash = unchecked(hash * 31 + character);
        }

        return FallbackColors[(hash & int.MaxValue) % FallbackColors.Length];
    }

    public static SolidColorBrush GetBrush(string? modelId, byte alpha = 255)
    {
        var color = GetColor(modelId);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public static string ShortName(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || modelId == "未识别")
        {
            return "未识别";
        }

        var normalized = CodexModelCost.NormalizeModelId(modelId);
        foreach (var suffix in new[] { "astra", "sol", "terra", "luna", "spark" })
        {
            if (normalized.Contains(suffix, StringComparison.Ordinal))
            {
                return suffix;
            }
        }

        return modelId;
    }
}
