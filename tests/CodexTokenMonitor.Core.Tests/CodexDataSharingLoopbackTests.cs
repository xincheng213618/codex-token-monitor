using System.Security.Cryptography;
using Xunit;

namespace CodexTokenMonitor.Tests;

/// <summary>
/// Real loopback: starts the Kestrel sharing server over isolated stores, then
/// drives it with the real client. Delegates push the cache root themselves —
/// Kestrel worker threads do not inherit the test's AsyncLocal scopes.
/// </summary>
public sealed class CodexDataSharingLoopbackTests : IDisposable
{
    private readonly string sourceRoot = Path.Combine(Path.GetTempPath(), $"SharingLoopback-Source-{Guid.NewGuid():N}");
    private readonly string serverRoot = Path.Combine(Path.GetTempPath(), $"SharingLoopback-Server-{Guid.NewGuid():N}");
    private readonly string importRoot = Path.Combine(Path.GetTempPath(), $"SharingLoopback-Import-{Guid.NewGuid():N}");
    private readonly IDisposable rootScope;
    private readonly string accessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public CodexDataSharingLoopbackTests()
    {
        rootScope = MonitorCachePaths.PushLocalAppDataRoot(sourceRoot);
        SeedUsageDay();
    }

    public void Dispose()
    {
        rootScope.Dispose();
        foreach (var dir in new[] { sourceRoot, serverRoot, importRoot })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of the temporary trees.
            }
        }
    }

    private void SeedUsageDay()
    {
        // Today-sync fixtures must stay in the Beijing calendar day even in
        // the first hour after midnight (and before the UTC date rolls over).
        var now = BeijingClock.Now;
        var at = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
        var events = Enumerable.Range(1, 3).Select(index => new TokenUsageEvent(
            at.AddTicks(index), InputTokens: 1_000 * index, CachedInputTokens: 0, OutputTokens: 0,
            ReasoningOutputTokens: 0, TotalTokens: 1_000 * index, Key: $"share-event-{index}")).ToList();
        var day = DateOnly.FromDateTime(at.DateTime);
        var bucket = new TokenUsageBucket
        {
            StartLocal = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
        };
        foreach (var usageEvent in events)
        {
            bucket.Add(usageEvent);
        }

        UsageCacheStore.Load("CodexTokenMonitor").Put(
            bucket, isComplete: true, scannedThroughLocal: day.ToDateTime(TimeOnly.MaxValue),
            detailEvents: events, propagateErrors: true);
    }

    private CodexDataSharingServer StartServer()
    {
        // Kestrel threads do not inherit AsyncLocal cache scopes, so every
        // delegate re-pushes the isolated root before touching the stores.
        var server = new CodexDataSharingServer(
            (path, cancellationToken) =>
            {
                using var scope = MonitorCachePaths.PushLocalAppDataRoot(serverRoot);
                return Task.FromResult(CodexDataTransferService.Export(path, CodexDataExportScope.RecentDays, cancellationToken));
            },
            (path, cancellationToken) =>
            {
                using var scope = MonitorCachePaths.PushLocalAppDataRoot(serverRoot);
                return Task.FromResult(CodexDataTransferService.ImportRecentDays(path, cancellationToken));
            },
            historyStore: null,
            exportToday: (path, cancellationToken) =>
            {
                using var scope = MonitorCachePaths.PushLocalAppDataRoot(serverRoot);
                return Task.FromResult(CodexDataTransferService.Export(path, CodexDataExportScope.Today, cancellationToken));
            },
            importToday: (path, cancellationToken) =>
            {
                using var scope = MonitorCachePaths.PushLocalAppDataRoot(serverRoot);
                return Task.FromResult(CodexDataTransferService.ImportToday(path, cancellationToken));
            });
        server.StartAsync(0, accessKey).GetAwaiter().GetResult();
        return server;
    }

    private string ExportUploadPackage()
    {
        var packagePath = Path.Combine(Path.GetTempPath(), $"share-upload-{Guid.NewGuid():N}.codex.json");
        var exported = CodexDataTransferService.Export(packagePath, CodexDataExportScope.RecentDays);
        Assert.Equal(3, exported.UsageEventCount);
        return packagePath;
    }

    [Fact]
    public async Task WrongAccessKey_IsRejectedWithUnauthorized()
    {
        var server = StartServer();
        await using (server)
        {
            using var client = new CodexDataSharingClient($"http://127.0.0.1:{server.Port}", "wrong-key-0000");

            var failure = await Assert.ThrowsAsync<HttpRequestException>(
                () => client.TestConnectionAsync(CancellationToken.None));

            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, failure.StatusCode);
        }
    }

    [Fact]
    public async Task TestConnection_ReportsProtocolVersionAndDevice()
    {
        var server = StartServer();
        await using (server)
        {
            using var client = new CodexDataSharingClient($"http://127.0.0.1:{server.Port}", accessKey);

            var peer = await client.TestConnectionAsync(CancellationToken.None);

            Assert.Equal(CodexDataSharingProtocol.Format, peer.Format);
            Assert.Equal(CodexDataSharingProtocol.Version, peer.Version);
            Assert.Equal(Environment.MachineName, peer.DeviceName);
        }
    }

    [Fact]
    public async Task UploadTwice_MergesIdempotentlyByStableKey()
    {
        var server = StartServer();
        await using (server)
        {
            var packagePath = ExportUploadPackage();
            try
            {
                using var client = new CodexDataSharingClient($"http://127.0.0.1:{server.Port}", accessKey);

                var first = await client.UploadAsync(packagePath, CancellationToken.None);
                Assert.Equal(3, first.AddedUsageEventCount);
                Assert.Equal(0, first.ExistingUsageEventCount);

                var second = await client.UploadAsync(packagePath, CancellationToken.None);
                Assert.Equal(0, second.AddedUsageEventCount);
                Assert.Equal(3, second.ExistingUsageEventCount);
            }
            finally
            {
                File.Delete(packagePath);
            }
        }
    }

    [Fact]
    public async Task TodayUploadDownload_RoundTripsTodaysEvents()
    {
        var server = StartServer();
        await using (server)
        {
            var packagePath = ExportUploadPackage();
            var downloadPath = Path.Combine(Path.GetTempPath(), $"share-today-{Guid.NewGuid():N}.codex.json");
            try
            {
                using var client = new CodexDataSharingClient($"http://127.0.0.1:{server.Port}", accessKey);

                var uploaded = await client.UploadTodayAsync(packagePath, CancellationToken.None);
                Assert.Equal(3, uploaded.AddedUsageEventCount);

                await client.DownloadTodayAsync(downloadPath, CancellationToken.None);

                using var secondScope = MonitorCachePaths.PushLocalAppDataRoot(importRoot);
                var imported = CodexDataTransferService.Import(new[] { downloadPath });
                Assert.Equal(3, imported.AddedUsageEventCount);
            }
            finally
            {
                File.Delete(downloadPath);
                File.Delete(packagePath);
            }
        }
    }

    [Fact]
    public async Task TodayUpload_PackageWithOutsideDates_IsRejected()
    {
        // A package produced from another root whose events all fall before
        // today must be rejected by the today endpoint's range validation.
        var otherRoot = Path.Combine(Path.GetTempPath(), $"SharingLoopback-C-{Guid.NewGuid():N}");
        var yesterdayPackage = Path.Combine(Path.GetTempPath(), $"share-yesterday-{Guid.NewGuid():N}.codex.json");
        try
        {
            using (var otherScope = MonitorCachePaths.PushLocalAppDataRoot(otherRoot))
            {
                var at = DateTimeOffset.UtcNow.AddHours(-26);
                var usageEvent = new TokenUsageEvent(
                    at, InputTokens: 500, CachedInputTokens: 0, OutputTokens: 0,
                    ReasoningOutputTokens: 0, TotalTokens: 500, Key: "yesterday-event");
                var day = DateOnly.FromDateTime(at.DateTime);
                var bucket = new TokenUsageBucket
                {
                    StartLocal = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
                };
                bucket.Add(usageEvent);
                UsageCacheStore.Load("CodexTokenMonitor").Put(
                    bucket, isComplete: true, scannedThroughLocal: day.ToDateTime(TimeOnly.MaxValue),
                    detailEvents: new[] { usageEvent }, propagateErrors: true);
                CodexDataTransferService.Export(yesterdayPackage, CodexDataExportScope.All);
            }

            var server = StartServer();
            await using (server)
            {
                using var client = new CodexDataSharingClient($"http://127.0.0.1:{server.Port}", accessKey);

                var failure = await Assert.ThrowsAsync<HttpRequestException>(
                    () => client.UploadTodayAsync(yesterdayPackage, CancellationToken.None));

                Assert.Equal(System.Net.HttpStatusCode.BadRequest, failure.StatusCode);
                Assert.Contains("本次同步范围之外", failure.Message);
            }
        }
        finally
        {
            File.Delete(yesterdayPackage);
            try
            {
                Directory.Delete(otherRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of the temporary tree.
            }
        }
    }

    [Fact]
    public async Task Download_ReturnsMergedPackageThatImportsIntoFreshStore()
    {
        var server = StartServer();
        await using (server)
        {
            var packagePath = ExportUploadPackage();
            var downloadPath = Path.Combine(Path.GetTempPath(), $"share-download-{Guid.NewGuid():N}.codex.json");
            try
            {
                using var client = new CodexDataSharingClient($"http://127.0.0.1:{server.Port}", accessKey);
                await client.UploadAsync(packagePath, CancellationToken.None);
                await client.DownloadAsync(downloadPath, CancellationToken.None);
                client.Dispose();

                // Import the downloaded package into a second isolated store.
                using var secondScope = MonitorCachePaths.PushLocalAppDataRoot(importRoot);
                var imported = CodexDataTransferService.Import(new[] { downloadPath });

                Assert.Equal(3, imported.AddedUsageEventCount);
            }
            finally
            {
                File.Delete(downloadPath);
                File.Delete(packagePath);
            }
        }
    }
}
