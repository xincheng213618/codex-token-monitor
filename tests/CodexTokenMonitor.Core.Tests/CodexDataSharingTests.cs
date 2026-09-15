using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexDataSharingTests
{
    private const string Key = "test-sharing-key-123456";
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task FullHistorySharing_MergesOldDaysBothWays_AndRepricesEnrichedModelsWithoutDuplicates()
    {
        using var source = new TestCache();
        using var host = new TestCache();
        source.AddEvent("same", Now.AddMonths(-6), 100, "", "priority");
        host.AddEvent("same", Now.AddMonths(-6), 100, "gpt-6-astra");
        source.AddEvent("local-old", Now.AddMonths(-3), 200, "gpt-5.6-sol");
        host.AddEvent("remote-old", Now.AddMonths(-1), 300, "gpt-5.6-luna");
        source.AddEvent("today", Now, 400);
        host.AddQuota(Now.AddYears(-1), 40); // A date with only quota snapshots must transfer too.
        source.AddQuota(Now.AddMonths(-6), 50);
        var localHistory = new CodexHistorySharingStore(source.Folder);
        var remoteHistory = new CodexHistorySharingStore(host.Folder);
        await using var server = new CodexDataSharingServer(host.ExportAsync, host.ImportAsync, remoteHistory);
        await server.StartAsync(0, Key, listenAddress: IPAddress.Loopback);
        using var client = new CodexDataSharingClient($"127.0.0.1:{server.Port}", Key);
        var first = await client.SyncHistoryAsync(localHistory, null, CancellationToken.None);
        Assert.True(first.BatchCount > 1);
        Assert.Equal(2, first.Uploaded.AddedUsageEventCount);
        Assert.Equal(1, first.Downloaded.AddedUsageEventCount);
        Assert.Equal(source.Events.OrderBy(x => x.Key), host.Events.OrderBy(x => x.Key));
        Assert.Equal("gpt-6-astra", source.Events.Single(x => x.Key == "codex:same").ModelId);
        Assert.Equal("priority", source.Events.Single(x => x.Key == "codex:same").ServiceTier);
        Assert.Equal(4, source.Events.Count);
        Assert.Equal(2, QuotaSnapshotCacheStore.Load(source.Folder).GetAllSnapshots().Count);
        var repeated = await client.SyncHistoryAsync(localHistory, null, CancellationToken.None);
        Assert.Equal(0, repeated.Uploaded.AddedUsageEventCount);
        Assert.Equal(0, repeated.Downloaded.AddedUsageEventCount);
        Assert.Equal(0, repeated.Uploaded.AddedQuotaSnapshotCount);
        Assert.Equal(0, repeated.Downloaded.AddedQuotaSnapshotCount);
        Assert.Equal(4, source.Events.Count);
    }

    [Fact]
    public async Task FullHistorySharing_CancelBetweenBatchesCanBeRepeatedSafely()
    {
        using var source = new TestCache();
        using var host = new TestCache();
        for (var i = 0; i < 3; i++) source.AddEvent($"old-{i}", Now.AddMonths(-3 + i), 100);
        var history = new CodexHistorySharingStore(source.Folder);
        await using var server = new CodexDataSharingServer(host.ExportAsync, host.ImportAsync, new(host.Folder));
        await server.StartAsync(0, Key, listenAddress: IPAddress.Loopback);
        using var client = new CodexDataSharingClient($"127.0.0.1:{server.Port}", Key);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SyncHistoryAsync(history, message =>
        {
            if (message.StartsWith("全部历史 2/", StringComparison.Ordinal)) cancellation.Cancel();
        }, cancellation.Token));
        Assert.Single(host.Events);
        var resumed = await client.SyncHistoryAsync(history, null, CancellationToken.None);
        Assert.Equal(2, resumed.Uploaded.AddedUsageEventCount);
        Assert.Equal(3, host.Events.Count);
        Assert.Equal(source.Events.OrderBy(x => x.Key), host.Events.OrderBy(x => x.Key));
    }

    [Fact]
    public async Task FullHistoryEndpoints_ValidateAuthenticationRangeAndPackageBeforeWriting()
    {
        using var source = new TestCache();
        using var host = new TestCache();
        source.AddEvent("old", Now.AddYears(-1), 100);
        using var package = new CodexSharingTemporaryFile();
        CodexDataTransferService.Export(package.FilePath, source.Folder, "source", "Source", Now);
        await using var server = new CodexDataSharingServer(host.ExportAsync, host.ImportAsync, new(host.Folder));
        await server.StartAsync(0, Key, listenAddress: IPAddress.Loopback);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        foreach (var endpoint in new[] { "api/history/dates", "api/history?start=2026-09-01&end=2026-09-05" })
        {
            using var denied = await http.GetAsync(endpoint);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        http.DefaultRequestHeaders.Add(CodexDataSharingProtocol.KeyHeader, Key);
        foreach (var query in new[] { "start=bad&end=bad", "start=2026-01-01&end=2026-09-01", "start=2026-09-01&end=2026-09-01", "start=2026-09-01&end=2026-09-05" })
        {
            using var content = new StreamContent(File.OpenRead(package.FilePath));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var invalid = await http.PostAsync("api/history?" + query, content);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.Empty(host.Events);
        using var client = new CodexDataSharingClient($"127.0.0.1:{server.Port}", Key);
        using var empty = new TestCache();
        var result = await client.SyncHistoryAsync(new(empty.Folder), null, CancellationToken.None);
        Assert.Equal(0, result.BatchCount);
    }

    [Fact]
    public void FullHistoryBatches_CoverBothSidesIncludingGaps_WithoutExceedingSevenDates()
    {
        var day = new DateOnly(2026, 1, 1);
        var dates = new[] { day, day.AddDays(6), day.AddDays(7), day.AddMonths(1), day };
        var batches = CodexHistorySharingStore.BatchDates(dates);
        Assert.Equal(3, batches.Count);
        Assert.All(dates, date => Assert.Single(batches, b => date >= b.Start && date < b.End));
        Assert.All(batches, b => b.Validate());
    }

    [Fact]
    public async Task TwoWaySharing_UsesRealHttpAndCaches_DeduplicatesRoundTrips()
    {
        using var source = new TestCache();
        using var host = new TestCache();
        using var third = new TestCache();
        source.AddEvent("source", Now, 100, "gpt-5.6-luna");
        host.AddEvent("host", Now.AddMinutes(1), 200, "gpt-6-astra");
        host.AddEvent("last-week", Now.AddDays(-9), 500);
        source.AddQuota(Now, 20);
        host.AddQuota(Now.AddMinutes(1), 21);
        await using var server = new CodexDataSharingServer(host.ExportAsync, host.ImportAsync);
        Assert.False(server.IsRunning);
        await server.StartAsync(0, Key, listenAddress: IPAddress.Loopback);
        Assert.True(server.IsRunning);
        using var client = new CodexDataSharingClient($"127.0.0.1:{server.Port}", Key);
        var peer = await client.TestConnectionAsync(CancellationToken.None);
        Assert.Equal(CodexDataSharingProtocol.Format, peer.Format);
        Assert.Equal(CodexDataSharingProtocol.Version, peer.Version);
        Assert.False(peer.SupportsToday);
        using var package = new CodexSharingTemporaryFile();
        await source.ExportAsync(package.FilePath, CancellationToken.None);
        var first = await client.UploadAsync(package.FilePath, CancellationToken.None);
        var second = await client.UploadAsync(package.FilePath, CancellationToken.None);
        Assert.Equal(1, first.AddedUsageEventCount);
        Assert.Equal(1, first.AddedQuotaSnapshotCount);
        Assert.Equal(0, second.AddedUsageEventCount);
        Assert.Equal(1, second.ExistingUsageEventCount);
        using var downloaded = new CodexSharingTemporaryFile();
        await client.DownloadAsync(downloaded.FilePath, CancellationToken.None);
        var localMerge = await source.ImportAsync(downloaded.FilePath, CancellationToken.None);
        var thirdMerge = await third.ImportAsync(downloaded.FilePath, CancellationToken.None);
        Assert.Equal(1, localMerge.AddedUsageEventCount);
        Assert.Equal(2, thirdMerge.AddedUsageEventCount);
        Assert.Equal(2, thirdMerge.AddedQuotaSnapshotCount);
        Assert.All(third.Events, item => Assert.Equal(10, item.CacheWriteInputTokens));
        Assert.Equal(new[] { "gpt-5.6-luna", "gpt-6-astra" }, third.Events.Select(e => e.ModelId));
        var mergedUsage = UsageCacheStore.Load(third.Folder).ReadRange(Now.Date, Now.AddDays(1));
        Assert.Equal(2, mergedUsage.ModelUsage.Count);
        Assert.True(CodexModelCost.Estimate(mergedUsage).IsComplete);
        // Downloaded data can circulate through another computer without
        // changing stable keys or being counted again on the original server.
        var roundTrip = await client.UploadAsync(downloaded.FilePath, CancellationToken.None);
        Assert.Equal(0, roundTrip.AddedUsageEventCount);
        Assert.Equal(2, roundTrip.ExistingUsageEventCount);
        Assert.Equal(3, host.Events.Count); // Including the host's previous week.
        await server.StopAsync();
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task TodaySharing_TransfersOnlyCurrentBeijingDate()
    {
        using var source = new TestCache();
        using var host = new TestCache();
        using var third = new TestCache();
        source.AddEvent("source-today", Now, 100);
        source.AddEvent("source-yesterday", Now.AddDays(-1), 200);
        host.AddEvent("host-today", Now.AddMinutes(1), 300);
        host.AddEvent("host-yesterday", Now.AddDays(-1), 400);
        await using var server = new CodexDataSharingServer(
            host.ExportAsync,
            host.ImportAsync,
            exportToday: host.ExportTodayAsync,
            importToday: host.ImportTodayAsync);
        await server.StartAsync(0, Key, listenAddress: IPAddress.Loopback);
        using var client = new CodexDataSharingClient($"127.0.0.1:{server.Port}", Key);
        Assert.True((await client.TestConnectionAsync(CancellationToken.None)).SupportsToday);

        using var invalidUpload = new CodexSharingTemporaryFile();
        CodexDataTransferService.Export(invalidUpload.FilePath, source.Folder, "source", "Source", Now);
        var invalid = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.UploadTodayAsync(invalidUpload.FilePath, CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.DoesNotContain(host.Events, item => item.Key?.StartsWith("codex:source-", StringComparison.Ordinal) == true);

        using var upload = new CodexSharingTemporaryFile();
        await source.ExportTodayAsync(upload.FilePath, CancellationToken.None);
        var uploaded = await client.UploadTodayAsync(upload.FilePath, CancellationToken.None);
        Assert.Equal(1, uploaded.AddedUsageEventCount);
        Assert.DoesNotContain(host.Events, item => item.Key == "codex:source-yesterday");

        using var download = new CodexSharingTemporaryFile();
        await client.DownloadTodayAsync(download.FilePath, CancellationToken.None);
        var downloaded = await third.ImportTodayAsync(download.FilePath, CancellationToken.None);
        Assert.Equal(2, downloaded.AddedUsageEventCount);
        Assert.Equal(
            new[] { "codex:host-today", "codex:source-today" },
            third.Events.Select(item => item.Key).OrderBy(item => item));
    }

    [Fact]
    public async Task AuthenticationAndInvalidUploads_LeaveCacheUnchanged()
    {
        using var host = new TestCache();
        await using var server = new CodexDataSharingServer(host.ExportAsync, host.ImportAsync);
        await server.StartAsync(0, Key, listenAddress: IPAddress.Loopback);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        foreach (var path in new[] { "api/health", "api/week", "api/today" })
        {
            using var response = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using (var response = await http.PostAsync("api/week", new StringContent("{}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        http.DefaultRequestHeaders.Add(CodexDataSharingProtocol.KeyHeader, Key);
        using (var response = await http.PostAsync("api/week", new StringContent("{}")))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }
        foreach (var invalid in new[] { "", "not json", "{}", "{\"format\":\"codex-token-monitor-transfer\",\"version\":1,\"sourceDeviceId\":\"a\",\"usageEvents\":[null]}" })
        {
            using var response = await http.PostAsync("api/week", new StringContent(invalid, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Empty(host.Events);
        using var wrongKey = new CodexDataSharingClient($"127.0.0.1:{server.Port}", "wrong-key-12345");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => wrongKey.TestConnectionAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task UploadOutsideRecentDays_IsRejectedBeforeAnyCacheWrite()
    {
        using var source = new TestCache();
        using var host = new TestCache();
        source.AddEvent("current", Now, 100);
        source.AddEvent("previous", Now.AddDays(-9), 100);
        using var package = new CodexSharingTemporaryFile();
        CodexDataTransferService.Export(package.FilePath, source.Folder, "source", "Source", Now);
        await using var server = new CodexDataSharingServer(host.ExportAsync, host.ImportAsync);
        await server.StartAsync(0, Key, listenAddress: IPAddress.Loopback);
        using var client = new CodexDataSharingClient($"127.0.0.1:{server.Port}", Key);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.UploadAsync(package.FilePath, CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Empty(host.Events);
    }

    [Fact]
    public async Task Stop_CancelsInFlightTransfer_AndReleasesPortForRestart()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new CodexDataSharingServer(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            throw new InvalidOperationException();
        }, (_, _) => throw new InvalidOperationException());
        await server.StartAsync(0, Key, listenAddress: IPAddress.Loopback);
        var port = server.Port;
        using var client = new CodexDataSharingClient($"127.0.0.1:{port}", Key);
        using var package = new CodexSharingTemporaryFile();
        var downloading = client.DownloadAsync(package.FilePath, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => downloading);
        await server.StartAsync(port, Key, listenAddress: IPAddress.Loopback);
        Assert.Equal(port, server.Port);
        Assert.NotNull(await client.TestConnectionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BusyServer_RejectsSecondTransfer_ButAllowsHealthCheck()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new CodexDataSharingServer(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new InvalidDataException("Test finished");
        }, (_, _) => throw new InvalidOperationException());
        await server.StartAsync(0, Key, listenAddress: IPAddress.Loopback);
        using var client = new CodexDataSharingClient($"127.0.0.1:{server.Port}", Key);
        using var firstPackage = new CodexSharingTemporaryFile();
        using var secondPackage = new CodexSharingTemporaryFile();
        var first = client.DownloadAsync(firstPackage.FilePath, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(await client.TestConnectionAsync(CancellationToken.None));
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadAsync(secondPackage.FilePath, CancellationToken.None));
            Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        }
        finally
        {
            release.TrySetResult();
            await Assert.ThrowsAsync<HttpRequestException>(() => first);
        }
    }

    [Fact]
    public async Task OccupiedPort_ReportsFailure_AndCanStartAfterPortIsReleased()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await using var server = new CodexDataSharingServer((_, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<IOException>(() => server.StartAsync(port, Key, listenAddress: IPAddress.Loopback));
        Assert.False(server.IsRunning);
        occupied.Stop();
        await server.StartAsync(port, Key, listenAddress: IPAddress.Loopback);
        Assert.True(server.IsRunning);
    }

    [Fact]
    public async Task AbortedUpload_NeverCallsImport_AndServerRemainsUsable()
    {
        var imports = 0;
        await using var server = new CodexDataSharingServer((_, _) => throw new InvalidOperationException(), (_, _) =>
        {
            Interlocked.Increment(ref imports);
            throw new InvalidOperationException();
        });
        await server.StartAsync(0, Key, listenAddress: IPAddress.Loopback);
        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback, server.Port);
            var bytes = Encoding.ASCII.GetBytes($"POST /api/week HTTP/1.1\r\nHost: localhost\r\n{CodexDataSharingProtocol.KeyHeader}: {Key}\r\nContent-Type: application/json\r\nContent-Length: 10000\r\n\r\n{{\"partial\":");
            await socket.GetStream().WriteAsync(bytes);
        }
        using var client = new CodexDataSharingClient($"127.0.0.1:{server.Port}", Key);
        Assert.NotNull(await client.TestConnectionAsync(CancellationToken.None));
        await server.StopAsync();
        Assert.Equal(0, imports);
    }

    [Theory]
    [InlineData("192.168.1.10", "http://192.168.1.10:36666/")]
    [InlineData("host:36667", "http://host:36667/")]
    [InlineData("host:80", "http://host/")]
    [InlineData("http://host:36666/", "http://host:36666/")]
    [InlineData("https://host", "https://host/")]
    [InlineData("[::1]", "http://[::1]:36666/")]
    public void Addresses_RespectExplicitPortsAndUse36666ForBareHosts(string address, string expected) =>
        Assert.Equal(expected, CodexDataSharingProtocol.ParseServerAddress(address).AbsoluteUri);

    [Theory]
    [InlineData("ftp://host")]
    [InlineData("http://user:secret@host")]
    [InlineData("http://host/api/week")]
    [InlineData("http://host?key=secret")]
    [InlineData("")]
    public void InvalidAddresses_AreRejected(string address) =>
        Assert.Throws<ArgumentException>(() => CodexDataSharingProtocol.ParseServerAddress(address));

    private sealed class TestCache : IDisposable
    {
        public string Folder { get; } = $"CodexSharingTest-{Guid.NewGuid():N}";
        public IReadOnlyList<TokenUsageEvent> Events => UsageCacheStore.Load(Folder).GetAllDetailEvents();

        public void AddEvent(string key, DateTimeOffset timestamp, long input, string model = "gpt-5.6-sol", string? tier = null)
        {
            UsageCacheStore.Load(Folder).MergeImportedDetailEvents(new[]
            {
                new TokenUsageEvent(timestamp, input, 20, 10, 0, input + 10, "codex:" + key, 10, model, tier)
            });
        }

        public void AddQuota(DateTimeOffset timestamp, decimal percent) =>
            QuotaSnapshotCacheStore.Load(Folder).MergeImportedSnapshots(new[]
            {
                new CodexQuotaSnapshot(timestamp, "codex", null, null, null, percent, timestamp.AddDays(3))
            });

        public Task<CodexDataExportResult> ExportAsync(string path, CancellationToken cancellationToken)
        {
            var range = CodexDataTransferService.GetExportRange(CodexDataExportScope.RecentDays, Now);
            return Task.FromResult(CodexDataTransferService.ExportRange(
                path, Folder, Folder, "Test PC", Now, range.StartInclusive, range.EndExclusive, cancellationToken));
        }

        public Task<CodexDataImportResult> ImportAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(CodexDataTransferService.ImportRecentDays(path, cancellationToken, Folder, Now));

        public Task<CodexDataExportResult> ExportTodayAsync(string path, CancellationToken cancellationToken)
        {
            var range = CodexDataTransferService.GetExportRange(CodexDataExportScope.Today, Now);
            return Task.FromResult(CodexDataTransferService.ExportRange(
                path, Folder, Folder, "Test PC", Now, range.StartInclusive, range.EndExclusive, cancellationToken));
        }

        public Task<CodexDataImportResult> ImportTodayAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(CodexDataTransferService.ImportToday(path, cancellationToken, Folder, Now));

        public void Dispose() => UsageCacheStore.Delete(Folder);
    }
}
