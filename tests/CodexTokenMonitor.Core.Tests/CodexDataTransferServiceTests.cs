using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexDataTransferServiceTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    [Fact]
    public void ExportAndImport_MergesUsageAndQuotaIdempotently()
    {
        var sourceFolder = $"CodexTransferSource-{Guid.NewGuid():N}";
        var targetFolder = $"CodexTransferTarget-{Guid.NewGuid():N}";
        var transferPath = Path.Combine(Path.GetTempPath(), $"codex-transfer-{Guid.NewGuid():N}.json");
        var dayStart = new DateTimeOffset(2026, 7, 18, 0, 0, 0, Beijing);
        var firstEvent = new TokenUsageEvent(dayStart.AddHours(9), 100, 60, 10, 2, 110, "codex:turn-1");
        var secondEvent = new TokenUsageEvent(dayStart.AddHours(10), 200, 120, 20, 4, 220, "codex:turn-2");
        var quota = new CodexQuotaSnapshot(
            dayStart.AddHours(10),
            "codex",
            null,
            null,
            null,
            2m,
            dayStart.AddDays(7).AddHours(10));

        try
        {
            var sourceUsage = UsageCacheStore.Load(sourceFolder);
            var bucket = new TokenUsageBucket { StartLocal = dayStart };
            foreach (var item in new[] { firstEvent, secondEvent })
            {
                bucket.Add(
                    item.Timestamp,
                    item.InputTokens,
                    item.CachedInputTokens,
                    item.OutputTokens,
                    item.ReasoningOutputTokens,
                    item.TotalTokens);
            }

            sourceUsage.Put(bucket, isComplete: true, detailEvents: new[] { firstEvent, secondEvent });
            QuotaSnapshotCacheStore.Load(sourceFolder).Put(
                DateOnly.FromDateTime(dayStart.DateTime),
                new[] { quota },
                isComplete: true,
                scannedThroughLocal: dayStart.AddDays(1).AddTicks(-1));

            var exported = CodexDataTransferService.Export(
                transferPath,
                sourceFolder,
                "device-a",
                "Laptop A",
                dayStart.AddDays(1));
            var firstImport = CodexDataTransferService.Import(new[] { transferPath }, targetFolder);
            var secondImport = CodexDataTransferService.Import(new[] { transferPath }, targetFolder);

            Assert.Equal(2, exported.UsageEventCount);
            Assert.Equal(1, exported.QuotaSnapshotCount);
            Assert.Equal(2, firstImport.AddedUsageEventCount);
            Assert.Equal(1, firstImport.AddedQuotaSnapshotCount);
            Assert.Equal(0, secondImport.AddedUsageEventCount);
            Assert.Equal(2, secondImport.ExistingUsageEventCount);
            Assert.Equal(0, secondImport.AddedQuotaSnapshotCount);
            Assert.Equal(1, secondImport.ExistingQuotaSnapshotCount);

            var importedUsage = UsageCacheStore.Load(targetFolder).ReadRange(dayStart, dayStart.AddDays(1));
            var importedQuota = QuotaSnapshotCacheStore.Load(targetFolder).GetAllSnapshots();
            Assert.Equal(2, importedUsage.Events);
            Assert.Equal(300, importedUsage.InputTokens);
            Assert.Equal(180, importedUsage.CachedInputTokens);
            Assert.Equal(2m, Assert.Single(importedQuota).WeekUsedPercent);

            var date = DateOnly.FromDateTime(dayStart.DateTime);
            Assert.True(UsageCacheStore.Load(targetFolder).DeleteDay(date));
            Assert.True(QuotaSnapshotCacheStore.Load(targetFolder).DeleteDay(date));
            Assert.Equal(2, UsageCacheStore.Load(targetFolder).GetDetailEvents(date).Count);
            Assert.Single(QuotaSnapshotCacheStore.Load(targetFolder).GetSnapshots(date));

            // Reimporting after a local day refresh rebuilds the aggregate but
            // still does not count the preserved portable records twice.
            var afterRefreshImport = CodexDataTransferService.Import(new[] { transferPath }, targetFolder);
            Assert.Equal(0, afterRefreshImport.AddedUsageEventCount);
            Assert.Equal(0, afterRefreshImport.AddedQuotaSnapshotCount);
            Assert.Equal(2, UsageCacheStore.Load(targetFolder).ReadRange(dayStart, dayStart.AddDays(1)).Events);
        }
        finally
        {
            UsageCacheStore.Delete(sourceFolder);
            UsageCacheStore.Delete(targetFolder);
            if (File.Exists(transferPath))
            {
                File.Delete(transferPath);
            }
        }
    }

    [Fact]
    public void Import_ValidatesAllFilesBeforeChangingCache()
    {
        var sourceFolder = $"CodexTransferSource-{Guid.NewGuid():N}";
        var targetFolder = $"CodexTransferTarget-{Guid.NewGuid():N}";
        var validPath = Path.Combine(Path.GetTempPath(), $"codex-transfer-{Guid.NewGuid():N}.json");
        var invalidPath = Path.Combine(Path.GetTempPath(), $"codex-transfer-{Guid.NewGuid():N}.json");
        var timestamp = new DateTimeOffset(2026, 7, 18, 9, 0, 0, Beijing);

        try
        {
            var usageEvent = new TokenUsageEvent(timestamp, 100, 50, 10, 0, 110, "codex:turn-1");
            var bucket = new TokenUsageBucket
            {
                StartLocal = new DateTimeOffset(timestamp.Year, timestamp.Month, timestamp.Day, 0, 0, 0, Beijing)
            };
            bucket.Add(timestamp, 100, 50, 10, 0, 110);
            UsageCacheStore.Load(sourceFolder).Put(bucket, detailEvents: new[] { usageEvent });
            CodexDataTransferService.Export(validPath, sourceFolder, "device-a", "Laptop A", timestamp);
            File.WriteAllText(invalidPath, "not a transfer package");

            Assert.Throws<InvalidDataException>(() =>
                CodexDataTransferService.Import(new[] { validPath, invalidPath }, targetFolder));
            Assert.Empty(UsageCacheStore.Load(targetFolder).GetAllDetailEvents());
        }
        finally
        {
            UsageCacheStore.Delete(sourceFolder);
            UsageCacheStore.Delete(targetFolder);
            foreach (var path in new[] { validPath, invalidPath })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public void Import_MergesMultipleDevicesAndDeduplicatesOverlappingPackages()
    {
        var sourceFolderA = $"CodexTransferSourceA-{Guid.NewGuid():N}";
        var sourceFolderB = $"CodexTransferSourceB-{Guid.NewGuid():N}";
        var targetFolder = $"CodexTransferTarget-{Guid.NewGuid():N}";
        var packageA = Path.Combine(Path.GetTempPath(), $"codex-device-a-{Guid.NewGuid():N}.codex.json");
        var packageB = Path.Combine(Path.GetTempPath(), $"codex-device-b-{Guid.NewGuid():N}.codex.json");
        var dayStart = new DateTimeOffset(2026, 7, 19, 0, 0, 0, Beijing);
        var shared = new TokenUsageEvent(dayStart.AddHours(9), 100, 60, 10, 2, 110, "codex:shared-turn");
        var deviceA = new TokenUsageEvent(dayStart.AddHours(10), 200, 120, 20, 4, 220, "codex:device-a-turn");
        var deviceB = new TokenUsageEvent(dayStart.AddHours(11), 300, 180, 30, 6, 330, "codex:device-b-turn");
        var sharedQuota = CreateQuota(dayStart.AddHours(9).AddMinutes(30), 20m);
        var deviceAQuota = CreateQuota(dayStart.AddHours(10).AddMinutes(30), 21m);
        var deviceBQuota = CreateQuota(dayStart.AddHours(11).AddMinutes(30), 22m);

        try
        {
            PutEvents(sourceFolderA, dayStart, shared, deviceA);
            PutEvents(sourceFolderB, dayStart, shared, deviceB);
            PutQuotaSnapshots(sourceFolderA, dayStart, sharedQuota, deviceAQuota);
            PutQuotaSnapshots(sourceFolderB, dayStart, sharedQuota, deviceBQuota);
            CodexDataTransferService.Export(packageA, sourceFolderA, "device-a", "Laptop A", dayStart.AddHours(12));
            CodexDataTransferService.Export(packageB, sourceFolderB, "device-b", "Desktop B", dayStart.AddHours(12));

            var firstImport = CodexDataTransferService.Import(new[] { packageA, packageB }, targetFolder);
            var secondImport = CodexDataTransferService.Import(new[] { packageB, packageA, packageA }, targetFolder);

            Assert.Equal(2, firstImport.FileCount);
            Assert.Equal(2, firstImport.DeviceCount);
            Assert.Equal(3, firstImport.AddedUsageEventCount);
            Assert.Equal(0, firstImport.ExistingUsageEventCount);
            Assert.Equal(3, firstImport.AddedQuotaSnapshotCount);
            Assert.Equal(0, firstImport.ExistingQuotaSnapshotCount);
            Assert.Equal(3, UsageCacheStore.Load(targetFolder).GetAllDetailEvents().Count);
            Assert.Equal(3, QuotaSnapshotCacheStore.Load(targetFolder).GetAllSnapshots().Count);

            Assert.Equal(3, secondImport.FileCount);
            Assert.Equal(2, secondImport.DeviceCount);
            Assert.Equal(0, secondImport.AddedUsageEventCount);
            Assert.Equal(3, secondImport.ExistingUsageEventCount);
            Assert.Equal(0, secondImport.AddedQuotaSnapshotCount);
            Assert.Equal(3, secondImport.ExistingQuotaSnapshotCount);
            Assert.Equal(3, UsageCacheStore.Load(targetFolder).GetAllDetailEvents().Count);
            Assert.Equal(3, QuotaSnapshotCacheStore.Load(targetFolder).GetAllSnapshots().Count);
        }
        finally
        {
            UsageCacheStore.Delete(sourceFolderA);
            UsageCacheStore.Delete(sourceFolderB);
            UsageCacheStore.Delete(targetFolder);
            foreach (var path in new[] { packageA, packageB })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private static void PutEvents(
        string cacheFolder,
        DateTimeOffset dayStart,
        params TokenUsageEvent[] events)
    {
        var bucket = new TokenUsageBucket { StartLocal = dayStart };
        foreach (var item in events)
        {
            bucket.Add(
                item.Timestamp,
                item.InputTokens,
                item.CachedInputTokens,
                item.OutputTokens,
                item.ReasoningOutputTokens,
                item.TotalTokens);
        }

        UsageCacheStore.Load(cacheFolder).Put(bucket, isComplete: true, detailEvents: events);
    }

    private static CodexQuotaSnapshot CreateQuota(DateTimeOffset timestamp, decimal weekUsedPercent)
    {
        return new CodexQuotaSnapshot(
            timestamp,
            "codex",
            null,
            null,
            null,
            weekUsedPercent,
            timestamp.AddDays(7));
    }

    private static void PutQuotaSnapshots(
        string cacheFolder,
        DateTimeOffset dayStart,
        params CodexQuotaSnapshot[] snapshots)
    {
        QuotaSnapshotCacheStore.Load(cacheFolder).Put(
            DateOnly.FromDateTime(dayStart.DateTime),
            snapshots,
            isComplete: true,
            scannedThroughLocal: dayStart.AddDays(1).AddTicks(-1));
    }
}
