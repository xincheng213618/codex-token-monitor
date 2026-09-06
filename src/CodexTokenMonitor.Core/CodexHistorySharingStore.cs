namespace CodexTokenMonitor;

internal sealed record CodexHistoryRange(DateOnly Start, DateOnly End)
{
    public DateTimeOffset StartLocal => new(Start.ToDateTime(TimeOnly.MinValue), CodexUsageReader.BeijingOffset);
    public DateTimeOffset EndLocal => new(End.ToDateTime(TimeOnly.MinValue), CodexUsageReader.BeijingOffset);

    public void Validate()
    {
        if (End.DayNumber <= Start.DayNumber || End.DayNumber - Start.DayNumber > 7)
            throw new InvalidDataException("历史数据每批需为 1–7 个北京时间日期。");
    }
}

/// <summary>Shares cached history in bounded batches, without rescanning session logs.</summary>
internal sealed class CodexHistorySharingStore(
    string folder = "CodexTokenMonitor",
    SemaphoreSlim? cacheGate = null,
    Action<CodexDataImportResult>? imported = null)
{
    private readonly string localAppData = MonitorCachePaths.LocalAppData;

    public Task<IReadOnlyList<DateOnly>> GetDatesAsync(CancellationToken token) => RunAsync<IReadOnlyList<DateOnly>>(() =>
    {
        UsageCacheStore.Load(folder);
        QuotaSnapshotCacheStore.Load(folder);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = UsageCacheStore.GetCachePath(folder), Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT date FROM usage_events UNION SELECT date FROM quota_snapshots ORDER BY date";
        using var reader = command.ExecuteReader();
        var dates = new List<DateOnly>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            dates.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        return dates;
    }, token);

    public Task<CodexDataExportResult> ExportAsync(string path, CodexHistoryRange range, CancellationToken token) => RunAsync(() =>
    {
        range.Validate();
        return CodexDataTransferService.ExportRange(path, folder,
            folder == "CodexTokenMonitor" ? CodexDataTransferService.GetOrCreateDeviceId() : folder,
            Environment.MachineName, BeijingClock.Now, range.StartLocal, range.EndLocal, token);
    }, token);

    public async Task<CodexDataImportResult> ImportAsync(string path, CodexHistoryRange range, CancellationToken token)
    {
        range.Validate();
        var result = await RunAsync(() => CodexDataTransferService.ImportHistoryRange(
            path, folder, range, token), token).ConfigureAwait(false);
        try { imported?.Invoke(result); } catch { }
        return result;
    }

    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken token)
    {
        // HTTP request execution contexts do not inherit caller AsyncLocals.
        using var root = MonitorCachePaths.PushLocalAppDataRoot(localAppData);
        if (cacheGate is not null) await cacheGate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(action, token).ConfigureAwait(false); }
        finally { cacheGate?.Release(); }
    }

    public static IReadOnlyList<CodexHistoryRange> BatchDates(IEnumerable<DateOnly> dates)
    {
        var batches = new List<CodexHistoryRange>();
        foreach (var date in dates.Distinct().Order())
        {
            if (batches.Count > 0 && date.DayNumber - batches[^1].Start.DayNumber < 7)
                batches[^1] = batches[^1] with { End = date.AddDays(1) };
            else batches.Add(new(date, date.AddDays(1)));
        }
        return batches;
    }
}

internal sealed record CodexHistorySyncResult(int BatchCount, CodexDataImportResult Uploaded, CodexDataImportResult Downloaded);
