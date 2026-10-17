using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using CodexTokenMonitor;

namespace CodexTokenMonitor.Wpf.Probes;

internal static partial class MainWindowProbe
{
    private static void CheckTimelinePlotValues(MainWindow window)
    {
        var timeline = Get<WpfTokenTimelineControl>(window, "Timeline");
        var plot = Get<ScottPlot.WPF.WpfPlot>(timeline, "wpfPlot").Plot;
        var previousStart = Get<DateTimeOffset>(timeline, "startLocal");
        var previousEnd = Get<DateTimeOffset>(timeline, "endLocal");
        var previousInterval = Get<TimeSpan?>(timeline, "fixedBucketInterval");
        var previousRows = Get<IReadOnlyList<TokenUsageBucket>>(timeline, "lastRows");
        static TokenUsageBucket Row(DateTimeOffset time, long total, long cached) => new()
        {
            StartLocal = time, Events = 1, InputTokens = total,
            TotalTokens = total, CachedInputTokens = cached
        };
        void Check(double[] positions, long[] totals, long[] cached)
        {
            var bars = plot.GetPlottables<ScottPlot.Plottables.BarPlot>().ToArray();
            Require(bars.Length == 2, "timeline retains total and cached bar series");
            var cumulative = plot.GetPlottables<ScottPlot.Plottables.Scatter>().Single().Data.GetScatterPoints();
            Require(bars.All(series => series.Bars.Count == totals.Length) && cumulative.Count == totals.Length,
                "timeline series contain only populated buckets");
            long running = 0;
            for (var index = 0; index < totals.Length; index++)
            {
                running += totals[index];
                Require(Math.Abs(bars[0].Bars[index].Position - positions[index]) < 1e-9 &&
                        Math.Abs(bars[1].Bars[index].Position - positions[index]) < 1e-9 &&
                        Math.Abs(cumulative[index].X - positions[index]) < 1e-9,
                    "all timeline series preserve the expected time coordinate");
                Require(Math.Abs(bars[0].Bars[index].Value - totals[index] / 1_000_000d) < 1e-12 &&
                        Math.Abs(bars[1].Bars[index].Value - cached[index] / 1_000_000d) < 1e-12 &&
                        Math.Abs(cumulative[index].Y - running / 1_000_000d) < 1e-12,
                    "plotted total, cached and cumulative values match independent expected counts");
            }
        }
        try
        {
            foreach (var minutes in new[] { 1, 10, 60 })
            foreach (var offset in new[] { TimeSpan.FromHours(8), TimeSpan.Zero })
            {
                var interval = TimeSpan.FromMinutes(minutes);
                var start = SeedDay.ToOffset(offset);
                var rows = new[]
                {
                    Row(SeedDay.AddMinutes(minutes * 2.8), 11, 4),
                    Row(SeedDay.AddMinutes(minutes), 5, 2),
                    Row(SeedDay.AddMinutes(minutes * 0.5).ToUniversalTime(), 7, 3),
                    Row(SeedDay.AddMinutes(-minutes * 0.2), 3, 1),
                    Row(SeedDay.AddMinutes(minutes * 2.2), 13, 5),
                    new TokenUsageBucket { StartLocal = SeedDay, TotalTokens = 99 }
                };
                timeline.SetData(start, start.AddMinutes(minutes * 2.5), rows, interval);
                var positions = new[] { start, start.Add(interval), start.Add(interval + interval) }
                    .Select(time => time.DateTime.ToOADate()).ToArray();
                Check(positions, new long[] { 10, 5, 24 }, new long[] { 4, 2, 9 });
                timeline.ResetView();
                Check(positions, new long[] { 10, 5, 24 }, new long[] { 4, 2, 9 });
            }

            var early = Row(SeedDay.AddMinutes(1), 7, 3);
            var late = Row(SeedDay.AddMinutes(2), 5, 2);
            timeline.SetData(SeedDay, SeedDay.AddHours(1), new[] { late, early });
            Check(new[] { early.StartLocal.DateTime.ToOADate(), late.StartLocal.DateTime.ToOADate() },
                new long[] { 7, 5 }, new long[] { 3, 2 });
            timeline.ClearData();
            Require(!plot.GetPlottables().Any(), "cleared timeline retains no stale bars or cumulative line");
            Results.Add(new { check = "timeline-plot-values", fixedIntervalCases = 6,
                eventMode = true, mixedOffsets = true, partialAndClippedBuckets = true,
                cumulativeCounts = true, resetView = true, clearData = true });
        }
        finally
        {
            Set(timeline, "startLocal", previousStart);
            Set(timeline, "endLocal", previousEnd);
            Set(timeline, "fixedBucketInterval", previousInterval);
            Set(timeline, "lastRows", previousRows);
            timeline.ResetView();
        }
    }

    private static async Task CheckBreakdownQuotaRefreshAsync(MainWindow window, CodexUsageModule module,
        UsageSourceModule otherModule)
    {
        var grid = Get<DataGrid>(window, "BreakdownGrid");
        var range = new SelectedRange(SeedDay, SeedDay.AddDays(1), "性能回归", "事件", RangeMode.Day);
        var buckets = CreatePerformanceBuckets(240);
        var snapshots = PerformanceSnapshots(buckets, 20m, 60m);
        var summary = new TokenUsageSummary { StartLocal = range.Start, EndLocal = range.End };
        foreach (var bucket in buckets) summary.MergeFrom(bucket);
        var result = new UsageQueryResult(summary, buckets, TimeSpan.Zero, null, snapshots);
        Select(window, module, RangeMode.Day);
        module.StoreDisplay(range, result);
        Invoke(window, "ApplySummary", range, result, module);
        await DrainBindingsAsync(window);
        var rows = ((IReadOnlyList<BreakdownRow>)grid.ItemsSource).ToArray();
        var originalSource = grid.ItemsSource;
        var originalPrices = rows.Select(row => row.Prices).ToArray();
        var originalCosts = rows.Select(row => row.ActualCost).ToArray();
        var originalTotals = rows.Select(row => row.Total).ToArray();
        var selected = rows[123];
        var notifications = 0;
        foreach (var row in rows)
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BreakdownRow.Quota)) notifications++; };
        grid.Items.SortDescriptions.Add(new SortDescription(nameof(BreakdownRow.Label), ListSortDirection.Descending));
        grid.SelectedItem = selected;
        grid.Columns[0].Width = 207;
        var boundQuota = new TextBlock();
        boundQuota.SetBinding(TextBlock.TextProperty, new Binding(nameof(BreakdownRow.Quota)) { Source = selected });

        try
        {
            var cachedQuota = PerformanceQuota(buckets[100].StartLocal, 45m);
            // The cached point must survive coalescing the two UI submissions.
            Invoke(window, "StoreQuotaDisplay", module, range, result, cachedQuota);
            Require(ReferenceEquals(grid.ItemsSource, originalSource), "cached quota merge does not rebuild table");
            Invoke(window, "ApplyQuotaToCurrentBreakdown", module, PerformanceQuota(buckets[^1].StartLocal, 80m));
            await DrainBindingsAsync(window);
            var acceptedSnapshots = module.LastResult!.QuotaSnapshots;
            Require(acceptedSnapshots.Any(item => item.SnapshotLocal == cachedQuota.SnapshotLocal && item.WeekUsedPercent == 45m),
                "coalesced refresh preserves intermediate cached quota point");
            Require(ReferenceEquals(grid.ItemsSource, originalSource) && ReferenceEquals(grid.SelectedItem, selected),
                "quota update preserves ItemsSource and selected row");
            Require(grid.Items.SortDescriptions.Single().Direction == ListSortDirection.Descending && grid.Columns[0].Width.Value == 207,
                "quota update preserves sort and user column width");
            Require(boundQuota.Text == selected.Quota && notifications > 0, "existing WPF quota binding receives change notifications");
            for (var index = 0; index < rows.Length; index++)
                Require(ReferenceEquals(originalPrices[index], rows[index].Prices) &&
                        ReferenceEquals(originalCosts[index], rows[index].ActualCost) &&
                        ReferenceEquals(originalTotals[index], rows[index].Total),
                    "quota update reuses all price and token strings");

            var patchedQuotas = rows.Select(row => row.Quota).ToArray();
            var presets = PriceSettingsStore.DisplayPresetsForSource(UsageSource.Codex, count: 0).ToList();
            // Compare with the established full calculation, including anomaly
            // filtering, interpolation and the two events sharing each second.
            grid.Items.SortDescriptions.Clear();
            Invoke(window, "ApplyBreakdownRows", range, buckets, acceptedSnapshots, UsageSource.Codex, presets);
            Require(((IReadOnlyList<BreakdownRow>)grid.ItemsSource).Select(row => row.Quota).SequenceEqual(patchedQuotas),
                "in-place quota values match full rebuild for every source row");

            rows = ((IReadOnlyList<BreakdownRow>)grid.ItemsSource).ToArray();
            originalSource = grid.ItemsSource;
            selected = rows[123];
            grid.SelectedItem = selected;
            notifications = 0;
            foreach (var row in rows)
                row.PropertyChanged += (_, _) => notifications++;
            Invoke(window, "ApplyQuotaToCurrentBreakdown", module, PerformanceQuota(buckets[^1].StartLocal, 80m));
            Require(notifications == 0 && ReferenceEquals(grid.ItemsSource, originalSource),
                "identical snapshots do not notify or rebuild rows");

            await RenderContentAsync(window, Path.Combine(outputRoot, "quota-in-place-refresh.png"), afterLayout: () =>
            {
                // Exercise sorting and realized cells in the isolated host.
                grid.Items.SortDescriptions.Clear();
                grid.Items.SortDescriptions.Add(new SortDescription(nameof(BreakdownRow.Quota), ListSortDirection.Ascending));
                var beforeOrder = grid.Items.Cast<BreakdownRow>().Select(row => row.Quota).ToArray();
                Require(beforeOrder.SequenceEqual(beforeOrder.OrderBy(value => value, StringComparer.CurrentCulture)),
                    "quota sort is active before the in-place update");
                var descending = PerformanceSnapshots(buckets, 80m, 20m);
                Require((bool)Invoke(window, "TryUpdateBreakdownQuota", range, buckets, descending, UsageSource.Codex)!,
                    "quota-sorted rows accept in-place update");
                Require(ReferenceEquals(grid.SelectedItem, selected) && ReferenceEquals(grid.ItemsSource, originalSource),
                    "quota resort preserves row selection and ItemsSource");
                var actualOrder = grid.Items.Cast<BreakdownRow>().Select(row => row.Quota).ToArray();
                Require(actualOrder.SequenceEqual(actualOrder.OrderBy(value => value, StringComparer.CurrentCulture)),
                    "quota ordering remains correct after values change");
                grid.Items.SortDescriptions.Clear();
                grid.Items.SortDescriptions.Add(new SortDescription(nameof(BreakdownRow.Label), ListSortDirection.Descending));
                grid.ScrollIntoView(selected);
                grid.UpdateLayout();
                var adapter = Get<BreakdownGridAdapter>(window, "breakdownGridAdapter");
                var before = adapter.CaptureAnchor();
                Invoke(window, "TryUpdateBreakdownQuota", range, buckets, acceptedSnapshots, UsageSource.Codex);
                grid.UpdateLayout();
                Require(adapter.CaptureAnchor() == before, "quota patch preserves visible scroll anchor");
                var column = grid.Columns.Single(item => Equals(item.Header, "额度 (5h / 7d)"));
                Require(column.GetCellContent(selected) is TextBlock cell && cell.Text == selected.Quota,
                    "realized quota cell displays patched value");
                Get<WpfTokenTimelineControl>(window, "Timeline").ResetView();
            });

            grid.Items.SortDescriptions.Clear();
            Require(!(bool)Invoke(window, "TryUpdateBreakdownQuota", range, buckets.ToArray(), snapshots, UsageSource.Codex)!,
                "different bucket snapshot invalidates mapping while Codex remains visible");
            var originalSettings = PriceSettingsStore.Current.Clone();
            try
            {
                var changedSettings = originalSettings.Clone();
                changedSettings.CodexPresets.Single(item => item.Model == "GPT-6 Astra").UncachedInput += 10m;
                PriceSettingsStore.Save(changedSettings);
                Require(!(bool)Invoke(window, "TryUpdateBreakdownQuota", range, buckets, acceptedSnapshots, UsageSource.Codex)!,
                    "new price settings invalidate the fast path even for unchanged snapshots");
                var oldCost = rows[0].ActualCost;
                Invoke(window, "ApplyQuotaToCurrentBreakdown", module, PerformanceQuota(buckets[^1].StartLocal, 80m));
                Require(!ReferenceEquals(grid.ItemsSource, originalSource) &&
                        ((IReadOnlyList<BreakdownRow>)grid.ItemsSource)[0].ActualCost != oldCost,
                    "quota refresh rebuilds prices after another editor changes settings");
            }
            finally
            {
                PriceSettingsStore.Save(originalSettings);
            }
            Select(window, otherModule, RangeMode.Day);
            await RefreshAndDrainAsync(window);
            var otherSource = grid.ItemsSource;
            Invoke(window, "ApplyQuotaToCurrentBreakdown", module, PerformanceQuota(buckets[^1].StartLocal, 90m));
            Require(ReferenceEquals(grid.ItemsSource, otherSource), "late Codex quota cannot overwrite another source");
            Select(window, module, RangeMode.Day);
            module.ClearDisplay();
            Invoke(window, "ApplyQuotaToCurrentBreakdown", module, null);
            Require(ReferenceEquals(grid.ItemsSource, otherSource), "cleared display does not reuse old row mapping");
            var empty = new UsageQueryResult(new TokenUsageSummary { StartLocal = range.Start, EndLocal = range.End },
                Array.Empty<TokenUsageBucket>(), TimeSpan.Zero, null, Array.Empty<CodexQuotaSnapshot>());
            module.StoreDisplay(range, empty);
            Invoke(window, "ApplySummary", range, empty, module);
            var emptySource = grid.ItemsSource;
            Invoke(window, "ApplyQuotaToCurrentBreakdown", module, PerformanceQuota(buckets[0].StartLocal, 10m));
            Require(grid.Items.Count == 0 && ReferenceEquals(grid.ItemsSource, emptySource), "empty quota update keeps empty table");

            var now = BeijingClock.Now;
            Invoke(window, "ApplyQuotaSummary", module, PerformanceQuota(now, 30m));
            Require(Get<TextBlock>(window, "Quota5hValue").Text == "暂不限", "Pro 20x missing 5h window retains unrestricted display");
            Results.Add(new { check = "quota-in-place-refresh", rows = buckets.Count, sameSource = true,
                samePrices = true, preservedIntermediateSnapshot = true, bindings = true, sorting = true,
                selection = true, scroll = true, unchangedNoNotifications = true, emptyAndSourceBoundaries = true,
                priceAndBucketInvalidation = true });
            MeasureQuotaRefreshPreparation(window, range);
        }
        finally
        {
            BindingOperations.ClearBinding(boundQuota, TextBlock.TextProperty);
            grid.Items.SortDescriptions.Clear();
            module.ClearDisplay();
            Select(window, module, RangeMode.Day);
            await RefreshAndDrainAsync(window);
        }
    }

    private static IReadOnlyList<TokenUsageBucket> CreatePerformanceBuckets(int count)
    {
        var buckets = new TokenUsageBucket[count];
        for (var index = 0; index < count; index++)
        {
            var time = SeedDay.AddHours(1).AddMilliseconds(index * 500);
            var bucket = new TokenUsageBucket { StartLocal = time };
            bucket.Add(new TokenUsageEvent(time, 100_000 + index, 95_000, 300, 100,
                100_300 + index, $"performance-{index}", ModelId: "gpt-6-astra"));
            buckets[index] = bucket;
        }
        return buckets;
    }

    private static IReadOnlyList<CodexQuotaSnapshot> PerformanceSnapshots(IReadOnlyList<TokenUsageBucket> buckets,
        decimal firstUsed, decimal lastUsed) => new[]
    {
        new CodexQuotaSnapshot(buckets[0].StartLocal, "codex", "Codex", null, null, firstUsed, SeedDay.AddDays(7)),
        new CodexQuotaSnapshot(buckets[^1].StartLocal, "codex", "Codex", null, null, lastUsed, SeedDay.AddDays(7))
    };

    private static CodexQuotaEstimate PerformanceQuota(DateTimeOffset time, decimal used)
    {
        var usage = new TokenUsageSummary { StartLocal = SeedDay, EndLocal = time };
        var week = new CodexQuotaWindowEstimate("7d", used, 7 * 24 * 60, SeedDay, time, SeedDay.AddDays(7),
            usage, 1m, null, null);
        return new CodexQuotaEstimate(time, "codex", "Codex", null, week);
    }

    private static void MeasureQuotaRefreshPreparation(MainWindow window, SelectedRange range)
    {
        var presets = PriceSettingsStore.DisplayPresetsForSource(UsageSource.Codex, count: 0).ToList();
        foreach (var count in new[] { 6_430, 30_000 })
        {
            var buckets = CreatePerformanceBuckets(count);
            var a = PerformanceSnapshots(buckets, 20m, 60m);
            var b = PerformanceSnapshots(buckets, 21m, 61m);
            var useA = false;
            Action rebuild = () => Invoke(window, "ApplyBreakdownRows", range, buckets,
                (useA = !useA) ? a : b, UsageSource.Codex, presets);
            Action patch = () => Require((bool)Invoke(window, "TryUpdateBreakdownQuota", range, buckets,
                (useA = !useA) ? a : b, UsageSource.Codex)!, "benchmark uses valid row mapping");
            for (var warm = 0; warm < 3; warm++) { rebuild(); patch(); }
            var full = MeasurePreparation(rebuild);
            rebuild();
            var incremental = MeasurePreparation(patch);
            Results.Add(new { check = "quota-refresh-preparation-performance", rows = count, samples = 7,
                scope = "synthetic UI-thread preparation; excludes quota I/O, layout and rendering",
                fullRebuildMedianMs = full.MedianMs, quotaOnlyMedianMs = incremental.MedianMs,
                fullRebuildMedianAllocatedBytes = full.MedianBytes, quotaOnlyMedianAllocatedBytes = incremental.MedianBytes });
        }
    }

    private static (double MedianMs, long MedianBytes) MeasurePreparation(Action action)
    {
        var elapsed = new double[7];
        var allocated = new long[7];
        for (var index = 0; index < elapsed.Length; index++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            action();
            elapsed[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated[index] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(elapsed);
        Array.Sort(allocated);
        return (elapsed[elapsed.Length / 2], allocated[allocated.Length / 2]);
    }
}
