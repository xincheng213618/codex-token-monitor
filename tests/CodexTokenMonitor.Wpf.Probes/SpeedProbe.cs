using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexTokenMonitor;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor.Wpf.Probes;

internal static class SpeedProbe
{
    internal static void Run(string output)
    {
        var isolated = Path.Combine(output, "isolated-" + Guid.NewGuid().ToString("N"));
        using var caches = MonitorCachePaths.PushLocalAppDataRoot(isolated);
        using var logs = UsageLogPaths.PushRoot(Path.Combine(isolated, "logs"));
        var results = new List<object>();
        var windows = new List<Window>();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/CodexTokenMonitor;component/Themes/MonitorTheme.xaml", UriKind.Relative) });
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            Exception? failure = null;
            try { await CheckAsync(); }
            catch (Exception ex) { failure = ex; }
            foreach (var window in windows)
            {
                try
                {
                    window.Close();
                    await (window is AgentSpeedWindow speed ? speed.Completion : ((AgentSpeedHistoryWindow)window).Completion).WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { failure ??= ex; }
            }
            results.Add(new { check = "isolation", isolatedRoot = isolated, allClosed = windows.All(w => !w.IsVisible), realLogsRead = false });
            Environment.ExitCode = failure is null ? 0 : 1;
            ProbeReport.Write(output, "speed", failure is null, failure?.ToString(), results);
            app.Shutdown(Environment.ExitCode);
        }));
        app.Run();

        async Task CheckAsync()
        {
            var day = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(8));
            var now = day.AddSeconds(300);
            var home = UsageLogPaths.GetOverrideRoot(UsageSource.Codex)!;
            var sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions);
            var path = Path.Combine(sessions, "fixture.jsonl");
            string Line(int second, string type, object payload) => JsonSerializer.Serialize(new { timestamp = day.AddSeconds(second), type, payload }) + "\n";
            string Input(int second) => Line(second, "event_msg", new { type = "task_started" });
            string Usage(int second, string id, int outputTokens) => Line(second, "token_usage_record", new { response_id = id,
                usage = new { input_tokens = 1000, output_tokens = outputTokens } });
            var content = Line(0, "session_meta", new { id = "speed-probe-session" }) +
                Line(1, "turn_context", new { model = "gpt-6.1-sol" });
            for (var index = 0; index < 12; index++)
                content += Input(10 + index * 20) + Usage(18 + index * 20, "r" + index, (index % 4 + 1) * 400);
            content += Input(290);
            File.WriteAllText(path, content);
            var monitor = new AgentSpeedMonitor(); // Must honor the scoped log root.
            var window = new AgentSpeedWindow(monitor, () => now, autoPoll: false);
            windows.Add(window); window.Show();
            using var runtime = new MonitorRuntime();
            await runtime.SharedIoGate.WaitAsync();
            try
            {
                var responsive = false;
                var read = window.RefreshAsync();
                await Dispatcher.Yield(DispatcherPriority.Background); responsive = true;
                await read.WaitAsync(TimeSpan.FromSeconds(15));
                Require(window.LastSnapshot is { Requests: 12, MeasuredRequests: 12, EstimatedShare: 1 }, "live sample bypasses held usage gate");
                Require(((TextBlock)window.FindName("LiveValue")).Text != "—", "live label populated");
                Require(((DataGrid)window.FindName("ModelGrid")).Items.Count == 1, "model table populated");
                results.Add(new { check = "independent-live-sampling", sharedGateHeld = true, dispatcherResponsive = responsive,
                    requests = window.LastSnapshot!.Requests });
            }
            finally { runtime.SharedIoGate.Release(); }
            await RenderAsync(window, "speed-analysis.png");
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            await RenderAsync(window, "speed-analysis-narrow.png");
            var estimated = window.LastSnapshot!.Live;
            File.AppendAllText(path, Usage(300, "corrected", 100));
            await window.RefreshAsync();
            Require(window.LastSnapshot is { Requests: 13, EstimatedShare: 0 } && window.LastSnapshot.Live < estimated,
                "completion replaces in-flight estimate");
            results.Add(new { check = "estimate-correction", before = estimated, after = window.LastSnapshot!.Live });

            var emptyHome = Path.Combine(isolated, "empty"); Directory.CreateDirectory(emptyHome);
            var empty = new AgentSpeedWindow(new AgentSpeedMonitor(emptyHome), () => now, autoPoll: false);
            windows.Add(empty); empty.Show(); await empty.RefreshAsync();
            Require(empty.LastSnapshot is { Requests: 0, Average: null }, "empty logs show unknown average");
            await RenderAsync(empty, "speed-analysis-empty.png");
            var missing = new AgentSpeedEngine();
            missing.Add(new("missing", "a", "unknown", null, now, 800, 0));
            empty.Apply(missing.Snapshot(now, day));
            Require(((TextBlock)empty.FindName("LastText")).Text.Contains("缺少有效耗时"), "missing start is not presented as measured");
            results.Add(new { check = "empty-and-missing-timing", emptyRequests = 0, missingTimingExcluded = true });

            SeedOtherSources();
            var sourceBox = (ComboBox)window.FindName("SourceBox"); var metricBox = (ComboBox)window.FindName("MetricBox");
            foreach (var source in UsageSourceRegistry.All.Where(source => source.Source != UsageSource.Codex))
            {
                window.SelectSource(source.Source);
                await WaitAsync(() => window.LastSnapshot is { Requests: 1, MeasuredRequests: 1 } && window.Title.StartsWith(source.Title), "native source selection " + source.Title);
            }
            window.SelectSource(null);
            await WaitAsync(() => window.LastSnapshot?.Requests == 19, "all-source aggregate");
            Require(window.LastSnapshot!.Models.Count == 7, "all seven source/model rows exist");
            metricBox.SelectedIndex = 1;
            await WaitAsync(() => window.LastSnapshot?.Metric == AgentSpeedMetric.TotalThroughput, "metric switch");
            Require(window.LastSnapshot!.MeasuredPeak > 0 && window.LastSnapshot.Average > 0, "throughput and measured peak are populated");
            await RenderAsync(window, "speed-all-sources.png");
            results.Add(new { check = "source-metric-and-peak", sources = 7, requests = 19, modeDistinguished = true, totalThroughput = true });

            var historyStore = new AgentSpeedHistoryStore();
            var history = new AgentSpeedHistoryWindow(historyStore, null, () => now, autoIndex: false);
            windows.Add(history); history.Show();
            await runtime.SharedIoGate.WaitAsync();
            try { await history.IndexAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { runtime.SharedIoGate.Release(); }
            await history.RefreshRowsAsync();
            Require(history.VisibleRows.Sum(row => row.Requests) == 19, "all-date history includes every native source");
            var csv = AgentSpeedHistoryCsv.Build(history.VisibleRows);
            File.WriteAllText(Path.Combine(output, "speed-history.csv"), csv);
            Require(csv.Contains("ZCode") && csv.Contains("Qoder"), "history CSV includes other sources");
            await RenderAsync(history, "speed-history.png");
            File.Delete(path); await history.IndexAsync();
            Require(history.VisibleRows.Sum(row => row.Requests) == 19, "deleted logs retain persisted history");
            results.Add(new { check = "history-index-and-export", sharedGateHeld = true, persistedAfterDeletion = true, rows = history.VisibleRows.Count });

            var repairStore = new AgentSpeedHistoryStore(Path.Combine(isolated, "repair-history.sqlite3"));
            var repair = new AgentSpeedHistoryWindow(repairStore, null, () => now, autoIndex: false);
            windows.Add(repair); repair.Show(); await repair.IndexAsync();
            var oldRows = repair.VisibleRows; var corrupt = System.Text.Encoding.UTF8.GetBytes("corrupt speed history fixture");
            File.WriteAllBytes(repairStore.Path, corrupt); await repair.RefreshRowsAsync();
            Require(ReferenceEquals(oldRows, repair.VisibleRows) && File.ReadAllBytes(repairStore.Path).SequenceEqual(corrupt), "corruption preserves rows and original bytes");
            await RenderAsync(repair, "speed-history-failure.png");
            await repair.IndexAsync(repair: true);
            var backup = Directory.GetFiles(isolated, "repair-history.sqlite3.backup-*").Single(file => !file.EndsWith("-wal") && !file.EndsWith("-shm"));
            Require(File.ReadAllBytes(backup).SequenceEqual(corrupt) && repair.VisibleRows.Sum(row => row.Requests) == 6, "repair preserves exact backup and rebuilds remaining logs");
            results.Add(new { check = "history-corruption-repair", previousRowsPreserved = true, bytesPreserved = true, backupExact = true });
            var closingIndex = repair.IndexAsync(); var beforeHistoryClose = repair.VisibleRows; repair.Close();
            await closingIndex.WaitAsync(TimeSpan.FromSeconds(10)); await repair.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Require(ReferenceEquals(beforeHistoryClose, repair.VisibleRows), "closed history ignores late publication");
            results.Add(new { check = "history-close", workerDrained = true, latePublication = false });

            // Close immediately after scheduling real file I/O; the worker must drain and never publish to the closed window.
            var pending = window.RefreshAsync();
            var beforeClose = window.LastSnapshot;
            window.Close();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Require(ReferenceEquals(beforeClose, window.LastSnapshot), "closed window ignores the late sample");
            var completion = window.Completion;
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            results.Add(new { check = "speed-window-close", workerDrained = completion.IsCompleted, latePublication = false });

            void SeedOtherSources()
            {
                void Write(UsageSource source, string relative, params string[] records)
                {
                    var file = Path.Combine(UsageLogPaths.GetOverrideRoot(source)!, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, string.Join("\n", records) + "\n");
                }
                string Json(object value) => JsonSerializer.Serialize(value);
                Write(UsageSource.ClaudeCode, "s.jsonl", Json(new { timestamp = day.AddSeconds(1), type = "user", message = new { content = "fixture" } }),
                    Json(new { timestamp = day.AddSeconds(5), type = "assistant", message = new { id = "claude-r", model = "claude", stop_reason = "end_turn", usage = new { input_tokens = 900, output_tokens = 400 } } }));
                Write(UsageSource.WorkBuddy, "s.jsonl", Json(new { timestamp = day.AddSeconds(1), type = "message", role = "user" }),
                    Json(new { timestamp = day.AddSeconds(5), type = "model-usage", providerData = new { messageId = "buddy-r", model = "hy3", usage = new { inputTokens = 900, outputTokens = 400 } } }));
                Write(UsageSource.Dsh, "s/session.v4.jsonl", Json(new { type = "session", id = "s", createdAt = day.ToUnixTimeMilliseconds() }),
                    Json(new { time = day.AddSeconds(1).ToUnixTimeMilliseconds(), type = "step/start", data = new { } }),
                    Json(new { time = day.AddSeconds(5).ToUnixTimeMilliseconds(), type = "assistant/message", data = new { message = new { id = "dsh-r", source = new { model = "deepseek" } }, usage = new { inputTokens = 900, outputTokens = 400 } } }));
                Write(UsageSource.Kimi, "s/agents/main/wire.jsonl", Json(new { time = day.AddSeconds(1).ToUnixTimeMilliseconds(), type = "llm.request", turnStep = "0.1" }),
                    Json(new { time = day.AddSeconds(5).ToUnixTimeMilliseconds(), type = "usage.record", usageScope = "turn", model = "kimi", usage = new { inputOther = 900, output = 400 } }));
                Write(UsageSource.Qoder, "s/segments/001.jsonl", Json(new { ts = day.AddSeconds(1), type = "model.request.started", request_id = "qoder-r", data = new { model = "qoder" } }),
                    Json(new { ts = day.AddSeconds(5), type = "model.response.completed", request_id = "qoder-r", data = new { model = "qoder", input_tokens = 900, output_tokens = 400 } }));
                var database = AgentSpeedPaths.ZCodePath(); Directory.CreateDirectory(Path.GetDirectoryName(database)!);
                using var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False"); connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE model_usage(id TEXT, session_id TEXT, model_id TEXT, started_at INTEGER, completed_at INTEGER, input_tokens INTEGER, output_tokens INTEGER); INSERT INTO model_usage VALUES('zcode-r','s','glm',$start,$end,900,400)";
                command.Parameters.AddWithValue("$start", day.AddSeconds(1).ToUnixTimeMilliseconds()); command.Parameters.AddWithValue("$end", day.AddSeconds(5).ToUnixTimeMilliseconds()); command.ExecuteNonQuery();
            }
        }

        async Task WaitAsync(Func<bool> condition, string name)
        {
            var watch = Stopwatch.StartNew();
            while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(name); await Task.Delay(20); }
        }

        async Task RenderAsync(Window window, string name)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
            var width = (int)Math.Ceiling(window.ActualWidth); var height = (int)Math.Ceiling(window.ActualHeight);
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            var path = Path.Combine(output, name); using var stream = File.Create(path); encoder.Save(stream);
            results.Add(new { check = "render", path, width, height });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Speed probe failed: " + message);
    }
}
