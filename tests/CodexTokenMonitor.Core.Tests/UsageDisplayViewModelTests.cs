using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageDisplayViewModelTests
{
    private static readonly DateTimeOffset Day = new(2026, 2, 15, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly SelectedRange Range = new(Day, Day.AddDays(1), "样例日", "明细", RangeMode.Day);

    [Fact]
    public void InitialFailureIsUnavailableAndRecoveryRestoresContentAndCopy()
    {
        var view = Create();
        view.ShowLoading(UsageSource.ClaudeCode, "Claude Code");
        view.ShowReadFailure("文件损坏", "isolated/file");
        Assert.Equal(UsageDisplayStage.Unavailable, view.Stage);
        Assert.Contains("Claude Code 统计暂不可用", view.Snapshot.EmptyTitle);
        Assert.DoesNotContain("暂无", view.Snapshot.EmptyTitle);
        Assert.Equal("-", view.Snapshot.Total);
        Assert.True(view.ShowEmpty);
        Assert.False(view.CanCopy);
        Assert.Equal("isolated/file", view.StatusDetail);

        view.ShowResult(UsageSource.ClaudeCode, "Claude Code", Range, Result());
        Assert.Equal(UsageDisplayStage.Ready, view.Stage);
        Assert.True(view.ShowContent);
        Assert.True(view.CanCopy);
        Assert.Null(view.StatusDetail);
        Assert.DoesNotContain("失败", view.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailurePreservesTheExactSuccessfulSnapshotIncludingKnownEmptyResults(bool empty)
    {
        var view = Create();
        view.ShowResult(UsageSource.Codex, "Codex", Range, empty ? Result(0, 0) : Result());
        var successful = view.Snapshot;
        view.ShowReadFailure("暂时不可读", "path and operation");
        Assert.Equal(UsageDisplayStage.Stale, view.Stage);
        Assert.Same(successful, view.Snapshot);
        Assert.Equal(!empty, view.ShowContent);
        Assert.Equal(!empty, view.CanCopy);
        Assert.Contains("上次成功结果", view.Status);
        Assert.Equal("path and operation", view.StatusDetail);
    }

    [Fact]
    public void SourceChangeClearsPreviousNumbersWarningsAndCopyAvailability()
    {
        var view = Create();
        view.ShowResult(UsageSource.Codex, "Codex", Range, Result());
        view.ShowReadFailure("坏缓存", "codex path");
        view.ShowLoading(UsageSource.Dsh, "DSH");
        Assert.Equal(UsageSource.Dsh, view.Snapshot.Source);
        Assert.Equal(UsageDisplayStage.Loading, view.Stage);
        Assert.Equal("-", view.Snapshot.Total);
        Assert.Equal("-", view.Snapshot.Events);
        Assert.Equal("DSH Token 额度监控器", view.WindowTitle);
        Assert.Null(view.StatusDetail);
        Assert.False(view.CanCopy);
        view.ShowReadFailure("另一个错误", "dsh path");
        Assert.Equal(UsageDisplayStage.Unavailable, view.Stage);
        Assert.DoesNotContain("上次成功", view.Status);
    }

    [Fact]
    public void SuccessfulEmptyRangeHasSourceAndPeriodAndContinuesIndependentQuotaHint()
    {
        var view = Create();
        view.ShowResult(UsageSource.Codex, "Codex", Range, Result(0, 0));
        Assert.Equal(UsageDisplayStage.Empty, view.Stage);
        Assert.Equal("今天还没有 Codex 用量", view.Snapshot.EmptyTitle);
        Assert.Contains("2026-02-15 00:00", view.Snapshot.EmptyPeriod);
        Assert.Contains("额度会继续独立刷新", view.Snapshot.EmptyHint);
        Assert.False(view.CanCopy);
        view.ShowResult(UsageSource.ZCode, "ZCode", Range with { Start = Day.AddDays(-1) }, Result(0, 0));
        Assert.Equal("样例日暂无 ZCode 用量", view.Snapshot.EmptyTitle);
        Assert.DoesNotContain("额度", view.Snapshot.EmptyHint);
    }

    [Fact]
    public void BusyAndStatusChangesDoNotDiscardDisplayedValues()
    {
        var view = Create();
        view.SetBusy(true);
        view.ShowResult(UsageSource.Codex, "Codex", Range, Result());
        var snapshot = view.Snapshot;
        Assert.False(view.CanCopy);
        view.SetBusy(false);
        Assert.True(view.CanCopy);
        view.ShowReadFailure("故障", "details");
        view.SetStatus("已复制摘要");
        Assert.Null(view.StatusDetail);
        Assert.Same(snapshot, view.Snapshot);
        Assert.True(view.CanCopy);
    }

    [Fact]
    public void SummaryPublishNotifiesWithOneCoherentImmutableSnapshot()
    {
        var view = Create();
        UsageDisplaySnapshot? notified = null;
        view.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(view.Snapshot))
            {
                notified = view.Snapshot;
                Assert.Equal(UsageSource.WorkBuddy, notified.Source);
                Assert.Equal("12", notified.Events);
                Assert.Equal("1h 5m", notified.CodingTime);
                Assert.True(view.ShowContent);
            }
        };
        var query = Result();
        view.ShowResult(UsageSource.WorkBuddy, "WorkBuddy", Range, query);
        Assert.Same(notified, view.Snapshot);
        var total = view.Snapshot.Total;
        query.Summary.TotalTokens = 0;
        Assert.Equal(total, view.Snapshot.Total);
    }

    [Fact]
    public void NonzeroEventsAndDetailRowsRemainUsageWhenSummaryTotalIsZero()
    {
        var view = Create();
        view.ShowResult(UsageSource.Codex, "Codex", Range, Result(0, 1));
        Assert.True(view.CanCopy);
        var rowsOnly = Result(0, 0) with
        {
            BreakdownRows = new[] { new TokenUsageBucket { StartLocal = Day, ReasoningOutputTokens = 1 } }
        };
        view.ShowResult(UsageSource.Codex, "Codex", Range, rowsOnly);
        Assert.True(view.ShowContent);
        Assert.True(view.CanCopy);
    }

    [Fact]
    public void WarningBearingResultsCannotReplaceSuccessfulDisplay()
    {
        var view = Create();
        view.ShowResult(UsageSource.Codex, "Codex", Range, Result());
        var successful = view.Snapshot;
        var failed = Result(0, 0) with
        {
            CacheWarnings = new[] { new CacheWarning("file", "Read", CacheWarningKind.Corrupt, "bad") }
        };
        Assert.Throws<ArgumentException>(() => view.ShowResult(UsageSource.Codex, "Codex", Range, failed));
        Assert.Same(successful, view.Snapshot);
    }

    [Fact]
    public void InvalidatingTheCopySourcePreservesDisplayButDisablesCopyUntilNewSuccess()
    {
        var view = Create();
        view.ShowResult(UsageSource.Codex, "Codex", Range, Result());
        var snapshot = view.Snapshot;
        view.SetCopySourceAvailable(false);
        view.SetBusy(true);
        view.SetBusy(false);
        Assert.False(view.CanCopy);
        Assert.Same(snapshot, view.Snapshot);
        view.ShowResult(UsageSource.Codex, "Codex", Range, Result());
        Assert.True(view.CanCopy);
    }

    [Fact]
    public void StoppedPresentationRejectsLateResultsAndStatus()
    {
        var view = Create();
        view.ShowResult(UsageSource.Codex, "Codex", Range, Result());
        view.SetStatus("before closing");
        var successful = view.Snapshot;
        view.Stop();
        view.ShowLoading(UsageSource.Dsh, "DSH");
        view.ShowResult(UsageSource.Dsh, "DSH", Range, Result(2, 1));
        view.ShowReadFailure("late failure", "late path");
        view.SetStatus("late status");
        view.SetBusy(false);
        Assert.Same(successful, view.Snapshot);
        Assert.Equal("before closing", view.Status);
        Assert.False(view.CanCopy);
    }

    private static UsageDisplayViewModel Create() => new(new FixedClock());
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Day.AddHours(12);
    }
    private static UsageQueryResult Result(long tokens = 1234567, int events = 12) => new(
        new TokenUsageSummary { StartLocal = Range.Start, EndLocal = Range.End, TotalTokens = tokens, Events = events },
        Array.Empty<TokenUsageBucket>(), TimeSpan.FromMinutes(65), null, Array.Empty<CodexQuotaSnapshot>());
}
