namespace CodexTokenMonitor;

internal sealed record QuotaConsumptionSpeedRow(
    QuotaCycleAnalysisBand Band,
    string QuotaRange,
    string TimeRange,
    string Duration,
    string Drop,
    string Rate,
    decimal? RateValue,
    string Model)
{
    public TimeSpan Elapsed => Band.EndLocal - Band.StartLocal;

    internal static QuotaConsumptionSpeedRow From(QuotaCycleAnalysisBand band)
    {
        var duration = band.EndLocal - band.StartLocal;
        decimal? rate = duration.Ticks > 0 ? band.QuotaDropPercent * TimeSpan.TicksPerHour / duration.Ticks : null;
        return new(band,
            $"{band.RemainingFromPercent:N1}% → {band.RemainingToPercent:N1}%",
            $"{band.StartLocal:MM-dd HH:mm:ss} → {band.EndLocal:MM-dd HH:mm:ss}",
            duration.Ticks > 0 ? FormatDuration(duration) : "时刻重合",
            $"{band.QuotaDropPercent:N2}", rate is { } value ? $"{value:N2}" : "—", rate,
            QuotaCycleModelPalette.ShortName(band.DominantModel));
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "0 秒";
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays} 天 {duration.Hours} 小时 {duration.Minutes} 分";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒";
        return duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:N0} 秒" : "不足 1 秒";
    }
}
