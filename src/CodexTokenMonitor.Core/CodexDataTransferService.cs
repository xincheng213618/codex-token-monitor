namespace CodexTokenMonitor;

internal sealed record CodexDataExportResult(
    string FilePath,
    int UsageEventCount,
    int QuotaSnapshotCount,
    string DeviceName);

internal sealed record CodexDataImportResult(
    int FileCount,
    int DeviceCount,
    int AddedUsageEventCount,
    int ExistingUsageEventCount,
    int AddedQuotaSnapshotCount,
    int ExistingQuotaSnapshotCount);

internal enum CodexDataExportScope
{
    All,
    Today,
    ThisWeek,
    RecentDays
}

/// <summary>
/// Moves cached Codex usage events and quota snapshots between computers.
/// Stable event/snapshot keys make repeated and overlapping imports idempotent.
/// </summary>
internal static class CodexDataTransferService
{
    private const string CacheFolder = "CodexTokenMonitor";
    private const string PackageFormat = "codex-token-monitor-transfer";
    private const int PackageVersion = 3;
    private static readonly object DeviceIdSyncRoot = new();
    private static readonly SemaphoreSlim ImportGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static CodexDataExportResult Export(string filePath)
    {
        return Export(filePath, CodexDataExportScope.All, CancellationToken.None);
    }

    public static CodexDataExportResult Export(
        string filePath,
        CodexDataExportScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exportedAtLocal = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var (startInclusive, endExclusive) = GetExportRange(scope, exportedAtLocal);
        return ExportRange(
            filePath,
            CacheFolder,
            GetOrCreateDeviceId(),
            Environment.MachineName,
            exportedAtLocal,
            startInclusive,
            endExclusive,
            cancellationToken);
    }

    internal static CodexDataExportResult Export(
        string filePath,
        string cacheFolder,
        string deviceId,
        string deviceName,
        DateTimeOffset exportedAtLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ExportRange(
            filePath,
            cacheFolder,
            deviceId,
            deviceName,
            exportedAtLocal,
            startInclusive: null,
            endExclusive: null,
            cancellationToken);
    }

    internal static CodexDataExportResult ExportRange(
        string filePath,
        string cacheFolder,
        string deviceId,
        string deviceName,
        DateTimeOffset exportedAtLocal,
        DateTimeOffset? startInclusive,
        DateTimeOffset? endExclusive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if ((startInclusive is null) != (endExclusive is null) || startInclusive >= endExclusive)
        {
            throw new ArgumentException("导出时间范围无效。");
        }

        var usageCache = UsageCacheStore.Load(cacheFolder);
        var quotaCache = QuotaSnapshotCacheStore.Load(cacheFolder);
        var quotaSnapshots = quotaCache.EnumerateSnapshots(
            startInclusive,
            endExclusive,
            cancellationToken);
        var sourceDeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Unknown device" : deviceName.Trim();

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        var usageEventCount = 0;
        var quotaSnapshotCount = 0;
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       options: FileOptions.SequentialScan))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("format", PackageFormat);
                writer.WriteNumber("version", PackageVersion);
                writer.WriteString("packageId", Guid.NewGuid().ToString("N"));
                writer.WriteString("sourceDeviceId", deviceId);
                writer.WriteString("sourceDeviceName", sourceDeviceName);
                writer.WriteString("exportedAtLocal", exportedAtLocal);

                writer.WritePropertyName("usageEvents");
                writer.WriteStartArray();
                foreach (var item in usageCache.EnumerateDetailEvents(
                             startInclusive,
                             endExclusive,
                             cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteUsageEvent(writer, item);
                    usageEventCount++;
                }

                writer.WriteEndArray();
                writer.WritePropertyName("quotaSnapshots");
                writer.WriteStartArray();
                foreach (var item in quotaSnapshots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteQuotaSnapshot(writer, item);
                    quotaSnapshotCount++;
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new CodexDataExportResult(
            fullPath,
            usageEventCount,
            quotaSnapshotCount,
            sourceDeviceName);
    }

    internal static (DateTimeOffset? StartInclusive, DateTimeOffset? EndExclusive) GetExportRange(
        CodexDataExportScope scope,
        DateTimeOffset exportedAtLocal)
    {
        var localNow = exportedAtLocal.ToOffset(CodexUsageReader.BeijingOffset);
        var todayStart = new DateTimeOffset(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            0,
            0,
            0,
            CodexUsageReader.BeijingOffset);

        return scope switch
        {
            CodexDataExportScope.All => (null, null),
            CodexDataExportScope.Today => (todayStart, todayStart.AddDays(1)),
            CodexDataExportScope.ThisWeek => GetThisWeekRange(todayStart),
            // Eight Beijing dates cover the entire rolling 7d quota across calendar weeks.
            CodexDataExportScope.RecentDays => (todayStart.AddDays(-7), todayStart.AddDays(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "未知的导出范围。")
        };
    }

    private static (DateTimeOffset StartInclusive, DateTimeOffset EndExclusive) GetThisWeekRange(
        DateTimeOffset todayStart)
    {
        var daysSinceMonday = ((int)todayStart.DayOfWeek + 6) % 7;
        var weekStart = todayStart.AddDays(-daysSinceMonday);
        return (weekStart, weekStart.AddDays(7));
    }

    private static void WriteUsageEvent(Utf8JsonWriter writer, TokenUsageEvent item)
    {
        item = item.Normalize();
        writer.WriteStartObject();
        writer.WriteString("key", UsageEventMerger.GetStableKey(item));
        writer.WriteString("timestampLocal", item.Timestamp);
        writer.WriteNumber("inputTokens", item.InputTokens);
        writer.WriteNumber("cachedInputTokens", item.CachedInputTokens);
        writer.WriteNumber("cacheWriteInputTokens", item.CacheWriteInputTokens);
        if (item.ModelId is not null) writer.WriteString("modelId", item.ModelId);
        if (item.ServiceTier is not null) writer.WriteString("serviceTier", item.ServiceTier);
        writer.WriteNumber("outputTokens", item.OutputTokens);
        writer.WriteNumber("reasoningOutputTokens", item.ReasoningOutputTokens);
        writer.WriteNumber("totalTokens", item.TotalTokens);
        writer.WriteEndObject();
    }

    private static void WriteQuotaSnapshot(Utf8JsonWriter writer, CodexQuotaSnapshot item)
    {
        writer.WriteStartObject();
        writer.WriteString("snapshotLocal", item.SnapshotLocal);
        writer.WriteString("limitId", item.LimitId);
        writer.WriteString("limitName", item.LimitName);
        WriteNullableDecimal(writer, "fiveHourUsedPercent", item.FiveHourUsedPercent);
        WriteNullableDateTimeOffset(writer, "fiveHourResetAtLocal", item.FiveHourResetAtLocal);
        WriteNullableDecimal(writer, "weekUsedPercent", item.WeekUsedPercent);
        WriteNullableDateTimeOffset(writer, "weekResetAtLocal", item.WeekResetAtLocal);
        writer.WriteBoolean("isAnomaly", item.IsAnomaly);
        writer.WriteEndObject();
    }

    private static void WriteNullableDecimal(Utf8JsonWriter writer, string propertyName, decimal? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(propertyName, number);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteNullableDateTimeOffset(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset? value)
    {
        if (value is { } timestamp)
        {
            writer.WriteString(propertyName, timestamp);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    public static CodexDataImportResult Import(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        return Import(filePaths, CacheFolder, cancellationToken);
    }

    internal static CodexDataImportResult Import(
        IReadOnlyList<string> filePaths,
        string cacheFolder,
        CancellationToken cancellationToken = default)
    {
        return ImportCore(filePaths, cacheFolder, null, cancellationToken);
    }

    internal static CodexDataImportResult ImportRecentDays(
        string filePath,
        CancellationToken cancellationToken = default,
        string cacheFolder = CacheFolder,
        DateTimeOffset? now = null)
    {
        var range = GetExportRange(CodexDataExportScope.RecentDays, now ?? DateTimeOffset.UtcNow);
        return ImportCore(new[] { filePath }, cacheFolder, (range.StartInclusive!.Value, range.EndExclusive!.Value), cancellationToken);
    }

    internal static CodexDataImportResult ImportHistoryRange(string filePath, string cacheFolder,
        CodexHistoryRange range, CancellationToken cancellationToken)
    {
        range.Validate();
        return ImportCore(new[] { filePath }, cacheFolder, (range.StartLocal, range.EndLocal), cancellationToken);
    }

    private static CodexDataImportResult ImportCore(
        IReadOnlyList<string> filePaths,
        string cacheFolder,
        (DateTimeOffset Start, DateTimeOffset End)? allowedRange,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        if (filePaths.Count == 0)
        {
            throw new ArgumentException("至少选择一个数据包。", nameof(filePaths));
        }

        // Validate every package before changing the cache, so one invalid file
        // cannot leave a partially imported batch.
        var packages = new List<CodexTransferPackage>(filePaths.Count);
        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            packages.Add(ReadPackage(filePath, cancellationToken));
        }

        var usageEvents = new List<TokenUsageEvent>();
        var quotaSnapshots = new List<CodexQuotaSnapshot>();
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            usageEvents.AddRange(package.UsageEvents.Select(item => ToUsageEvent(item, package.SourceDeviceId)));
            quotaSnapshots.AddRange(package.QuotaSnapshots.Select(ToQuotaSnapshot));
        }

        if (allowedRange is { } range &&
            (usageEvents.Any(item => item.Timestamp < range.Start || item.Timestamp >= range.End) ||
             quotaSnapshots.Any(item => item.SnapshotLocal < range.Start || item.SnapshotLocal >= range.End)))
        {
            throw new InvalidDataException("数据包包含本次同步范围之外的数据，请检查两台电脑的日期并重新同步。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var mergedUsageEvents = UsageEventMerger.Merge(usageEvents);
        var mergedQuotaSnapshots = MergeQuotaSnapshots(quotaSnapshots);
        cancellationToken.ThrowIfCancellationRequested();

        // Keep the read/merge/write sequence exclusive across imports. The
        // usage and quota stores share a SQLite file, but each store owns its
        // own transactions; serializing this sequence prevents two overlapping
        // imports from calculating a stale day aggregate and then overwriting
        // the other import's summary.
        ImportGate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var addedUsage = UsageCacheStore.Load(cacheFolder).MergeImportedDetailEvents(mergedUsageEvents);
            var addedQuota = QuotaSnapshotCacheStore.Load(cacheFolder).MergeImportedSnapshots(mergedQuotaSnapshots);

            return new CodexDataImportResult(
                packages.Count,
                packages.Select(item => item.SourceDeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                addedUsage,
                mergedUsageEvents.Count - addedUsage,
                addedQuota,
                mergedQuotaSnapshots.Count - addedQuota);
        }
        finally
        {
            ImportGate.Release();
        }
    }

    private static CodexTransferPackage ReadPackage(
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        CodexTransferPackage? package;
        try
        {
            // Deserialize directly from the file stream.  Full exports can
            // contain hundreds of thousands of events; ReadAllText would add
            // another copy of the entire package to the process heap before
            // JsonSerializer creates the object graph.
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            // Keep the synchronous public API, but let the serializer observe
            // cancellation while a large package is still being read and parsed.
            package = JsonSerializer.DeserializeAsync<CodexTransferPackage>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException($"无法读取数据包：{Path.GetFileName(filePath)}", ex);
        }

        if (package is null ||
            !string.Equals(package.Format, PackageFormat, StringComparison.Ordinal) ||
            package.Version != PackageVersion ||
            string.IsNullOrWhiteSpace(package.SourceDeviceId))
        {
            throw new InvalidDataException($"请使用新版监控器重新统计并导出（需要模型和速度信息 v3 数据包）：{Path.GetFileName(filePath)}");
        }

        package.UsageEvents ??= new List<PortableUsageEvent>();
        package.QuotaSnapshots ??= new List<PortableQuotaSnapshot>();
        return package;
    }

    private static TokenUsageEvent ToUsageEvent(PortableUsageEvent item, string sourceDeviceId)
    {
        if (item is null || item.TimestampLocal == default ||
            item.InputTokens < 0 ||
            item.CachedInputTokens < 0 ||
            item.CachedInputTokens > item.InputTokens ||
            item.CacheWriteInputTokens < 0 ||
            item.CacheWriteInputTokens > item.InputTokens - item.CachedInputTokens ||
            item.OutputTokens < 0 ||
            item.ReasoningOutputTokens < 0 ||
            item.TotalTokens < 0)
        {
            throw new InvalidDataException("数据包包含无效的 token 事件。");
        }

        var timestamp = item.TimestampLocal.ToOffset(CodexUsageReader.BeijingOffset);
        var key = string.IsNullOrWhiteSpace(item.Key)
            ? $"portable:{sourceDeviceId}:{timestamp:O}:{item.InputTokens}:{item.CachedInputTokens}:{item.CacheWriteInputTokens}:{item.OutputTokens}:{item.ReasoningOutputTokens}:{item.TotalTokens}"
            : item.Key.Trim();
        return new TokenUsageEvent(
            timestamp,
            item.InputTokens,
            item.CachedInputTokens,
            item.OutputTokens,
            item.ReasoningOutputTokens,
            item.TotalTokens,
            key,
            item.CacheWriteInputTokens,
            item.ModelId, item.ServiceTier);
    }

    private static CodexQuotaSnapshot ToQuotaSnapshot(PortableQuotaSnapshot item)
    {
        if (item is null || item.SnapshotLocal == default ||
            !IsValidPercent(item.FiveHourUsedPercent) ||
            !IsValidPercent(item.WeekUsedPercent))
        {
            throw new InvalidDataException("数据包包含无效的额度快照。");
        }

        var snapshot = CodexUsageReader.NormalizeQuotaSnapshotWindows(new CodexQuotaSnapshot(
            item.SnapshotLocal.ToOffset(CodexUsageReader.BeijingOffset),
            item.LimitId,
            item.LimitName,
            item.FiveHourUsedPercent,
            item.FiveHourResetAtLocal?.ToOffset(CodexUsageReader.BeijingOffset),
            item.WeekUsedPercent,
            item.WeekResetAtLocal?.ToOffset(CodexUsageReader.BeijingOffset),
            item.IsAnomaly));
        if (snapshot.FiveHourUsedPercent is null && snapshot.WeekUsedPercent is null)
        {
            throw new InvalidDataException("数据包包含没有可用额度窗口的快照。");
        }

        return snapshot;
    }

    private static bool IsValidPercent(decimal? value)
    {
        return value is null || value is >= 0m and <= 100m;
    }

    private static IReadOnlyList<CodexQuotaSnapshot> MergeQuotaSnapshots(
        IEnumerable<CodexQuotaSnapshot> snapshots)
    {
        return snapshots
            .GroupBy(QuotaSnapshotKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => item.IsAnomaly)
                .ThenByDescending(QuotaCompleteness)
                .First())
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
    }

    private static string QuotaSnapshotKey(CodexQuotaSnapshot snapshot)
    {
        return $"{snapshot.SnapshotLocal.ToOffset(CodexUsageReader.BeijingOffset):O}|{snapshot.LimitId ?? ""}";
    }

    private static int QuotaCompleteness(CodexQuotaSnapshot snapshot)
    {
        return (snapshot.FiveHourUsedPercent is null ? 0 : 1) +
               (snapshot.FiveHourResetAtLocal is null ? 0 : 1) +
               (snapshot.WeekUsedPercent is null ? 0 : 1) +
               (snapshot.WeekResetAtLocal is null ? 0 : 1);
    }

    internal static string GetOrCreateDeviceId()
    {
        lock (DeviceIdSyncRoot)
        {
            var directory = Path.Combine(MonitorCachePaths.LocalAppData, CacheFolder);
            var path = Path.Combine(directory, "transfer-device-id-v1.txt");
            try
            {
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path).Trim();
                    if (Guid.TryParseExact(existing, "N", out _))
                    {
                        return existing;
                    }
                }

                Directory.CreateDirectory(directory);
                var created = Guid.NewGuid().ToString("N");
                File.WriteAllText(path, created);
                return created;
            }
            catch
            {
                // Device identity is metadata only; event keys still provide
                // cross-import deduplication if this ID cannot be persisted.
                return $"ephemeral-{Environment.MachineName}";
            }
        }
    }

    private sealed class CodexTransferPackage
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public string PackageId { get; set; } = "";
        public string SourceDeviceId { get; set; } = "";
        public string SourceDeviceName { get; set; } = "";
        public DateTimeOffset ExportedAtLocal { get; set; }
        public List<PortableUsageEvent> UsageEvents { get; set; } = new();
        public List<PortableQuotaSnapshot> QuotaSnapshots { get; set; } = new();
    }

    private sealed class PortableUsageEvent
    {
        public string? ModelId { get; set; }
        public string? ServiceTier { get; set; }
        public string? Key { get; set; }
        public DateTimeOffset TimestampLocal { get; set; }
        public long InputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long CacheWriteInputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ReasoningOutputTokens { get; set; }
        public long TotalTokens { get; set; }
    }

    private sealed class PortableQuotaSnapshot
    {
        public DateTimeOffset SnapshotLocal { get; set; }
        public string? LimitId { get; set; }
        public string? LimitName { get; set; }
        public decimal? FiveHourUsedPercent { get; set; }
        public DateTimeOffset? FiveHourResetAtLocal { get; set; }
        public decimal? WeekUsedPercent { get; set; }
        public DateTimeOffset? WeekResetAtLocal { get; set; }
        public bool IsAnomaly { get; set; }
    }
}
