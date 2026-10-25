namespace CodexTokenMonitor;

/// <summary>On-demand all-date backfill with the same native parsers as live sampling.</summary>
internal sealed class AgentSpeedHistoryIndexer(AgentSpeedHistoryStore store)
{
    public AgentSpeedHistoryIndexResult Index(UsageSource source, DateTimeOffset now, CancellationToken token,
        IProgress<AgentSpeedHistoryProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var paths = source == UsageSource.ZCode
            ? File.Exists(AgentSpeedPaths.ZCodePath()) ? [AgentSpeedPaths.ZCodePath()] : Array.Empty<string>()
            : AgentSpeedPaths.StartupOrder(source, AgentSpeedPaths.Roots(source).Where(Directory.Exists).SelectMany(root => AgentSpeedPaths.Files(source, root))
                .Distinct(StringComparer.OrdinalIgnoreCase)).ToArray();
        var files = paths.Select(path => new AgentSpeedHistoryFile(path, Signature(path))).ToArray();
        var qoder = new Dictionary<string, QoderSpeedParser>(StringComparer.OrdinalIgnoreCase);
        return store.Index(source, files, Read, now, token, progress);

        AgentSpeedHistoryFileResult Read(AgentSpeedHistoryFile file, Func<string, bool> canUse, CancellationToken cancellation)
        {
            var engine = new AgentSpeedEngine();
            try
            {
                if (source == UsageSource.ZCode)
                {
                    var native = new ZCodeSpeedDatabase(file.Path);
                    var errors = native.Read(engine, DateTimeOffset.MinValue.AddDays(2), DateTimeOffset.MaxValue.AddDays(-2), cancellation);
                    return new(engine.Responses, errors == 0 && Signature(file.Path) == file.Signature);
                }
                var agentId = source == UsageSource.Qoder ? Path.GetDirectoryName(Path.GetDirectoryName(file.Path))! : file.Path;
                IAgentSpeedParser parser;
                if (source == UsageSource.Qoder)
                {
                    if (!qoder.TryGetValue(agentId, out var shared)) qoder[agentId] = shared = new(agentId);
                    parser = shared;
                }
                else parser = AgentSpeedPaths.Parser(source, agentId, canUse);
                void Consume(string line)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (line.Length > 32 * 1024 * 1024) return;
                    foreach (var response in parser.ReadResponses(line)) engine.Add(response);
                }
                bool complete;
                if (file.Path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase))
                {
                    using var compressed = new ZstdJsonlTailReader();
                    // Qoder has no compressed segments; other sources have independent per-file state.
                    complete = compressed.Read(file.Path, Consume, parser.Reset, cancellation, allowFinalLine: true);
                }
                else
                {
                    using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    string? lastLine = null;
                    while (reader.ReadLine() is { } line) { Consume(line); lastLine = line; }
                    complete = true;
                    var endedWithNewline = stream.Length == 0;
                    if (stream.Length > 0) { stream.Seek(-1, SeekOrigin.End); endedWithNewline = stream.ReadByte() == (byte)'\n'; }
                    if (!endedWithNewline && lastLine is not null && !string.IsNullOrWhiteSpace(lastLine))
                    {
                        try { using var document = JsonDocument.Parse(lastLine); }
                        catch (JsonException) { complete = false; }
                    }
                }
                return new(engine.Responses, complete && Signature(file.Path) == file.Signature);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (IOException) { return new([], false); }
            catch (UnauthorizedAccessException) { return new([], false); }
        }
    }

    private static string Signature(string path)
    {
        var file = new FileInfo(path); var wal = new FileInfo(path + "-wal");
        return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}:{(wal.Exists ? wal.Length : 0)}:{(wal.Exists ? wal.LastWriteTimeUtc.Ticks : 0)}";
    }
}

internal static class AgentSpeedHistoryCsv
{
    public static string Build(IReadOnlyList<AgentSpeedHistoryRow> rows)
    {
        var values = new List<IEnumerable<string?>> { new[] { "日期(UTC+8)", "来源", "模型", "记录模式", "请求数", "有效测速", "未最终结算", "输入Token", "输出Token", "实测请求毫秒", "平均输出tok/s", "平均总吞吐tok/s", "来源实测输出峰值tok/s", "来源实测总吞吐峰值tok/s" } };
        foreach (var row in rows)
            values.Add(new[] { row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), UsageSourceRegistry.For(row.Source).Title,
                Text(row.Model), Text(row.Mode ?? "未知"), Number(row.Requests), Number(row.MeasuredRequests), Number(row.Unfinalized),
                Number(row.InputTokens), Number(row.OutputTokens), Number(row.MeasuredMilliseconds),
                Decimal(row.Average(AgentSpeedMetric.Output)), Decimal(row.Average(AgentSpeedMetric.TotalThroughput)), Decimal(row.OutputPeak), Decimal(row.TotalPeak) });
        return CsvWriter.Build(values);
    }
    private static string Text(string value) => value.TrimStart().StartsWithAnyFormula() ? "'" + value : value;
    private static bool StartsWithAnyFormula(this string value) => value.Length > 0 && value[0] is '=' or '+' or '-' or '@';
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Decimal(double? value) => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
}
