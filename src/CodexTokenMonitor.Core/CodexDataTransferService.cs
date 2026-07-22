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
    ThisWeek
}

/// <summary>
/// Moves cached Codex usage events and quota snapshots between computers.
/// Stable event/snapshot keys make repeated and overlapping imports idempotent.
/// </summary>
internal static class CodexDataTransferService
{
    private const string CacheFolder = "CodexTokenMonitor";
    private const string PackageFormat = "codex-token-monitor-transfer";
    private const int PackageVersion = 1;
    private static readonly object DeviceIdSyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static CodexDataExportResult Export(string filePath)
    {
        return Export(filePath, CodexDataExportScope.All);
    }

    public static CodexDataExportResult Export(string filePath, CodexDataExportScope scope)
    {
        var exportedAtLocal = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var (startInclusive, endExclusive) = GetExportRange(scope, exportedAtLocal);
        return ExportRange(
            filePath,
            CacheFolder,
            GetOrCreateDeviceId(),
            Environment.MachineName,
            exportedAtLocal,
            startInclusive,
            endExclusive);
    }

    internal static CodexDataExportResult Export(
        string filePath,
        string cacheFolder,
        string deviceId,
        string deviceName,
        DateTimeOffset exportedAtLocal)
    {
        return ExportRange(
            filePath,
            cacheFolder,
            deviceId,
            deviceName,
            exportedAtLocal,
            startInclusive: null,
            endExclusive: null);
    }

    internal static CodexDataExportResult ExportRange(
        string filePath,
        string cacheFolder,
        string deviceId,
        string deviceName,
        DateTimeOffset exportedAtLocal,
        DateTimeOffset? startInclusive,
        DateTimeOffset? endExclusive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if ((startInclusive is null) != (endExclusive is null) || startInclusive >= endExclusive)
        {
            throw new ArgumentException("导出时间范围无效。");
        }

        var usageEvents = UsageCacheStore.Load(cacheFolder)
            .GetAllDetailEvents()
            .Where(item => IsInExportRange(item.Timestamp, startInclusive, endExclusive))
            .ToList();
        var quotaSnapshots = QuotaSnapshotCacheStore.Load(cacheFolder)
            .GetAllSnapshots()
            .Where(item => IsInExportRange(item.SnapshotLocal, startInclusive, endExclusive))
            .ToList();
        var package = new CodexTransferPackage
        {
            Format = PackageFormat,
            Version = PackageVersion,
            PackageId = Guid.NewGuid().ToString("N"),
            SourceDeviceId = deviceId,
            SourceDeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Unknown device" : deviceName.Trim(),
            ExportedAtLocal = exportedAtLocal,
            UsageEvents = usageEvents.Select(item => new PortableUsageEvent
            {
                Key = UsageEventMerger.GetStableKey(item),
                TimestampLocal = item.Timestamp,
                InputTokens = item.InputTokens,
                CachedInputTokens = item.CachedInputTokens,
                OutputTokens = item.OutputTokens,
                ReasoningOutputTokens = item.ReasoningOutputTokens,
                TotalTokens = item.TotalTokens
            }).ToList(),
            QuotaSnapshots = quotaSnapshots.Select(item => new PortableQuotaSnapshot
            {
                SnapshotLocal = item.SnapshotLocal,
                LimitId = item.LimitId,
                LimitName = item.LimitName,
                FiveHourUsedPercent = item.FiveHourUsedPercent,
                FiveHourResetAtLocal = item.FiveHourResetAtLocal,
                WeekUsedPercent = item.WeekUsedPercent,
                WeekResetAtLocal = item.WeekResetAtLocal,
                IsAnomaly = item.IsAnomaly
            }).ToList()
        };

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(package, JsonOptions),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
            package.UsageEvents.Count,
            package.QuotaSnapshots.Count,
            package.SourceDeviceName);
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

    private static bool IsInExportRange(
        DateTimeOffset timestamp,
        DateTimeOffset? startInclusive,
        DateTimeOffset? endExclusive)
    {
        return startInclusive is null || timestamp >= startInclusive.Value && timestamp < endExclusive!.Value;
    }

    public static CodexDataImportResult Import(IReadOnlyList<string> filePaths)
    {
        return Import(filePaths, CacheFolder);
    }

    internal static CodexDataImportResult Import(
        IReadOnlyList<string> filePaths,
        string cacheFolder)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        if (filePaths.Count == 0)
        {
            throw new ArgumentException("至少选择一个数据包。", nameof(filePaths));
        }

        // Validate every package before changing the cache, so one invalid file
        // cannot leave a partially imported batch.
        var packages = filePaths.Select(ReadPackage).ToList();
        var usageEvents = new List<TokenUsageEvent>();
        var quotaSnapshots = new List<CodexQuotaSnapshot>();
        foreach (var package in packages)
        {
            usageEvents.AddRange(package.UsageEvents.Select(item => ToUsageEvent(item, package.SourceDeviceId)));
            quotaSnapshots.AddRange(package.QuotaSnapshots.Select(ToQuotaSnapshot));
        }

        var mergedUsageEvents = UsageEventMerger.Merge(usageEvents);
        var mergedQuotaSnapshots = MergeQuotaSnapshots(quotaSnapshots);
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

    private static CodexTransferPackage ReadPackage(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        CodexTransferPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<CodexTransferPackage>(
                File.ReadAllText(filePath),
                JsonOptions);
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
            throw new InvalidDataException($"不是受支持的 Codex 监控器数据包：{Path.GetFileName(filePath)}");
        }

        package.UsageEvents ??= new List<PortableUsageEvent>();
        package.QuotaSnapshots ??= new List<PortableQuotaSnapshot>();
        return package;
    }

    private static TokenUsageEvent ToUsageEvent(PortableUsageEvent item, string sourceDeviceId)
    {
        if (item.TimestampLocal == default ||
            item.InputTokens < 0 ||
            item.CachedInputTokens < 0 ||
            item.OutputTokens < 0 ||
            item.ReasoningOutputTokens < 0 ||
            item.TotalTokens < 0)
        {
            throw new InvalidDataException("数据包包含无效的 token 事件。");
        }

        var timestamp = item.TimestampLocal.ToOffset(CodexUsageReader.BeijingOffset);
        var key = string.IsNullOrWhiteSpace(item.Key)
            ? $"portable:{sourceDeviceId}:{timestamp:O}:{item.InputTokens}:{item.CachedInputTokens}:{item.OutputTokens}:{item.ReasoningOutputTokens}:{item.TotalTokens}"
            : item.Key.Trim();
        return new TokenUsageEvent(
            timestamp,
            item.InputTokens,
            item.CachedInputTokens,
            item.OutputTokens,
            item.ReasoningOutputTokens,
            item.TotalTokens,
            key);
    }

    private static CodexQuotaSnapshot ToQuotaSnapshot(PortableQuotaSnapshot item)
    {
        if (item.SnapshotLocal == default ||
            !IsValidPercent(item.FiveHourUsedPercent) ||
            !IsValidPercent(item.WeekUsedPercent))
        {
            throw new InvalidDataException("数据包包含无效的额度快照。");
        }

        return new CodexQuotaSnapshot(
            item.SnapshotLocal.ToOffset(CodexUsageReader.BeijingOffset),
            item.LimitId,
            item.LimitName,
            item.FiveHourUsedPercent,
            item.FiveHourResetAtLocal?.ToOffset(CodexUsageReader.BeijingOffset),
            item.WeekUsedPercent,
            item.WeekResetAtLocal?.ToOffset(CodexUsageReader.BeijingOffset),
            item.IsAnomaly);
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

    private static string GetOrCreateDeviceId()
    {
        lock (DeviceIdSyncRoot)
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                CacheFolder);
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
        public string? Key { get; set; }
        public DateTimeOffset TimestampLocal { get; set; }
        public long InputTokens { get; set; }
        public long CachedInputTokens { get; set; }
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
