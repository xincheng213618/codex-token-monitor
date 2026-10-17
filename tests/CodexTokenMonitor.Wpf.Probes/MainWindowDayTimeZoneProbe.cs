using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CodexTokenMonitor;

namespace CodexTokenMonitor.Wpf.Probes;

internal static partial class MainWindowProbe
{
    private static async Task CheckDayTimeZoneAsync(MainWindow window, UsageSourceModule module)
    {
        Select(window, module, RangeMode.Day);
        var box = Get<ComboBox>(window, "DayTimeZoneBox");
        Require((int)box.SelectedValue == 480, "day timezone defaults to UTC+8");
        await RefreshAndDrainAsync(window);
        Require(module.LastResult?.Summary.TotalTokens == 660, "UTC+8 preserves the previous daily total");
        // Exercise the real selection event and normal query, including both
        // sides of the cache's midnight. No live quota source is needed.
        SetSuppressed(window, false);
        box.SelectedValue = -720;
        await Get<Task>(GetObject(window, "usageRefreshRunner")!, "Completion");
        SetSuppressed(window, true);
        await DrainBindingsAsync(window);
        Require(module.LastRange?.Start.Offset == TimeSpan.FromHours(-12) &&
                module.LastResult?.Summary.TotalTokens == 1100 && module.LastResult.BreakdownRows.Count == 1,
            "shifted day includes next storage day's event without double counting");
        Require(Get<TextBlock>(window, "PeriodValue").Text.Contains("UTC-12"), "daily period labels its configured timezone");
        var row = (BreakdownRow)Get<DataGrid>(window, "BreakdownGrid").Items[0];
        Require(row.Label == "13:00:00", "event time follows UTC-12");
        var summary = (string)Invoke(window, "BuildClipboardSummary", module, module.LastRange, module.LastResult)!;
        Require(summary.Contains("2000-01-18 00:00:00") && summary.Contains("UTC-12"), "copy summary follows reporting day");
        var csv = (string)typeof(MainWindow).GetMethod("BuildCurrentCsv",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object?[] { module, module.LastRange, module.LastResult })!;
        Require(csv.Contains("分桶开始(UTC-12)") && csv.Contains("13:00:00-12:00"), "CSV follows daily timezone");
        await LastDisplayStore.FlushAsync();
        Require(LastDisplayStore.Load()?.Range.Start.Offset == TimeSpan.FromHours(-12), "restore snapshot retains daily offset");
        var restored = CreateWindow();
        Require((int)Get<ComboBox>(restored, "DayTimeZoneBox").SelectedValue == -720 &&
                (bool)Invoke(restored, "TryRestoreLastDisplay")!, "restart restores timezone selection and display");
        await CloseWindowAsync(restored);
        await RenderContentAsync(window, Path.Combine(outputRoot, "daily-time-zone-minus12.png"));
        foreach (var mode in new[] { RangeMode.Week, RangeMode.Month })
        {
            Select(window, module, mode);
            Require(Get<FrameworkElement>(window, "DayTimeZonePanel").Visibility == Visibility.Collapsed,
                "timezone setting is hidden outside daily mode");
            var configured = (SelectedRange)Invoke(window, "GetSelectedRange")!;
            Require(configured.Start.Offset == CodexUsageReader.BeijingOffset, "other modes preserve UTC+8");
        }
        Select(window, module, RangeMode.Day, custom: true);
        Require(!box.IsEnabled && ((SelectedRange)Invoke(window, "GetSelectedRange")!).Start.Offset == CodexUsageReader.BeijingOffset,
            "custom start keeps its previous clock convention");
        Set(window, "dayTimeZone", new DayTimeZoneSettings());
        box.SelectedValue = 480;
        new DayTimeZoneSettings().Save();
        Select(window, module, RangeMode.Day);
        await RefreshAndDrainAsync(window);
        Results.Add(new { check = "daily-time-zone", defaultMinutes = 480, selectedMinutes = -720,
            defaultTokens = 660, shiftedTokens = 1100, eventTime = "13:00:00", restart = true,
            csv = true, copySummary = true, otherModesUnchanged = true });
        await Task.Run(CheckHourlySwitchPerformance);
    }

    private static void CheckHourlySwitchPerformance()
    {
        var folder = "HourlyPerformanceProbe-" + Guid.NewGuid().ToString("N");
        var cache = UsageCacheStore.Load(folder);
        var start = SeedDay;
        // Three storage days with 72,000 events; their hourly aggregates are
        // reused across all 27 integer UTC offsets.
        for (var dayIndex = 0; dayIndex < 3; dayIndex++)
        {
            var day = start.AddDays(dayIndex - 1);
            var events = Enumerable.Range(0, 24_000).Select(index => new TokenUsageEvent(
                day.AddSeconds(index * 3.5), 100, 80, 10, 3, 110,
                $"hourly-perf-{dayIndex}-{index}", ModelId: "gpt-6.1-sol", ServiceTier: "standard")).ToArray();
            var bucket = new TokenUsageBucket { StartLocal = day };
            foreach (var item in events) bucket.Add(item);
            cache.Put(bucket, detailEvents: events);
        }
        var timings = new List<double>();
        for (var pass = 0; pass < 2; pass++)
        for (var offset = -12; offset <= 14; offset++)
        {
            var day = new DateTimeOffset(start.Date, TimeSpan.FromHours(offset));
            var watch = Stopwatch.StartNew();
            var summary = cache.ReadRange(day, day.AddDays(1));
            watch.Stop();
            Require(summary.Events > 20_000, "hourly performance query retains daily usage");
            if (pass == 1) timings.Add(watch.Elapsed.TotalMilliseconds);
        }
        Results.Add(new { check = "hourly-timezone-switch-performance", storageEvents = 72_000, offsets = 27,
            medianMs = timings.Order().ElementAt(timings.Count / 2), maxMs = timings.Max(),
            scope = "cached summary query; excludes grid and chart rendering" });
    }
}
