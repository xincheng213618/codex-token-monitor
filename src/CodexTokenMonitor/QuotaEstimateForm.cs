namespace CodexTokenMonitor;

internal sealed class QuotaEstimateForm : Form
{
    private const decimal MinimumStableQuotaDeltaPercent = 3m;
    private static readonly TimeSpan ResetClusterTolerance = TimeSpan.FromMinutes(10);

    private readonly CodexQuotaEstimate currentQuota;
    private readonly CurrentWindowCardBindings fiveHourCard = new();
    private readonly CurrentWindowCardBindings weekCard = new();
    private readonly ListView weeklyList = new();
    private readonly Label statusLabel = new();
    private readonly NumericUpDown weekManualFromBox = new();
    private readonly NumericUpDown weekManualToBox = new();
    private readonly Button weekManualButton = new();
    private readonly Label weekManualResult = new();
    private CancellationTokenSource? loadCancellation;
    private int currentWindowRowsLoaded;

    public QuotaEstimateForm(CodexQuotaEstimate currentQuota)
    {
        this.currentQuota = currentQuota;
        BuildUi();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = LoadRowsAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        loadCancellation?.Cancel();
        base.OnFormClosing(e);
    }

    private void BuildUi()
    {
        Text = "Codex 额度估算";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1360, 760);
        MinimumSize = new Size(980, 620);
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 6,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(CreateHeader("当前窗口"), 0, 0);
        root.Controls.Add(BuildCurrentWindowPanel(), 0, 1);

        root.Controls.Add(BuildManualEstimateBar(), 0, 2);

        root.Controls.Add(CreateHeader("历史 7d 周期"), 0, 3);
        ConfigureWeeklyList();
        root.Controls.Add(weeklyList, 0, 4);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 12, 0, 0)
        };
        var closeButton = new Button
        {
            Text = "关闭",
            AutoSize = true,
            MinimumSize = new Size(86, 34),
            BackColor = Color.FromArgb(21, 128, 106),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.Click += (_, _) => Close();
        footer.Controls.Add(closeButton);

        statusLabel.AutoSize = true;
        statusLabel.Text = "正在加载...";
        statusLabel.ForeColor = Color.FromArgb(87, 99, 116);
        statusLabel.Margin = new Padding(0, 8, 20, 0);
        footer.Controls.Add(statusLabel);
        root.Controls.Add(footer, 0, 5);
    }

    private static Label CreateHeader(string text)
    {
        return new Label
        {
            AutoSize = true,
            Text = text,
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 41, 55),
            Margin = new Padding(0, 0, 0, 8)
        };
    }

    private Control BuildCurrentWindowPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.Controls.Add(BuildCurrentWindowCard("5h", fiveHourCard), 0, 0);
        panel.Controls.Add(BuildCurrentWindowCard("7d", weekCard), 1, 0);
        return panel;
    }

    private static Control BuildCurrentWindowCard(string title, CurrentWindowCardBindings bindings)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0, 0, 12, 0),
            BackColor = Color.White
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        var titleLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = title,
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(55, 65, 81),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        panel.Controls.Add(titleLabel, 0, 0);

        bindings.Plan.Dock = DockStyle.Fill;
        bindings.Plan.AutoSize = false;
        bindings.Plan.TextAlign = ContentAlignment.MiddleRight;
        bindings.Plan.Font = new Font("Microsoft YaHei UI", 9f);
        bindings.Plan.ForeColor = Color.FromArgb(87, 99, 116);
        bindings.Plan.AutoEllipsis = true;
        bindings.Plan.Margin = new Padding(0);
        panel.Controls.Add(bindings.Plan, 1, 0);

        bindings.Value.Dock = DockStyle.Fill;
        bindings.Value.AutoSize = false;
        bindings.Value.Text = "-";
        bindings.Value.TextAlign = ContentAlignment.MiddleLeft;
        bindings.Value.Font = new Font("Segoe UI", 22f, FontStyle.Bold);
        bindings.Value.ForeColor = Color.FromArgb(29, 40, 55);
        bindings.Value.Margin = new Padding(0);
        panel.Controls.Add(bindings.Value, 0, 1);

        bindings.Detail.Dock = DockStyle.Fill;
        bindings.Detail.AutoSize = false;
        bindings.Detail.Text = "-";
        bindings.Detail.TextAlign = ContentAlignment.MiddleLeft;
        bindings.Detail.Font = new Font("Microsoft YaHei UI", 9.2f);
        bindings.Detail.ForeColor = Color.FromArgb(55, 65, 81);
        bindings.Detail.AutoEllipsis = true;
        bindings.Detail.Margin = new Padding(0, 4, 0, 0);
        panel.Controls.Add(bindings.Detail, 1, 1);

        bindings.Stable.Dock = DockStyle.Fill;
        bindings.Stable.AutoSize = false;
        bindings.Stable.TextAlign = ContentAlignment.MiddleLeft;
        bindings.Stable.Font = new Font("Microsoft YaHei UI", 8.8f);
        bindings.Stable.ForeColor = Color.FromArgb(87, 99, 116);
        bindings.Stable.AutoEllipsis = true;
        bindings.Stable.Margin = new Padding(0);
        panel.Controls.Add(bindings.Stable, 0, 2);
        panel.SetColumnSpan(bindings.Stable, 2);

        return panel;
    }

    private Control BuildManualEstimateBar()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 58,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(12, 8, 12, 8)
        };

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent
        };
        panel.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "7d 手动估算",
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 41, 55),
            Margin = new Padding(0, 6, 12, 0)
        });
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "剩余",
            ForeColor = Color.FromArgb(87, 99, 116),
            Margin = new Padding(0, 6, 6, 0)
        });

        ConfigurePercentBox(weekManualFromBox, 90);
        layout.Controls.Add(weekManualFromBox);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "% ->",
            ForeColor = Color.FromArgb(87, 99, 116),
            Margin = new Padding(4, 6, 6, 0)
        });

        ConfigurePercentBox(weekManualToBox, 85);
        layout.Controls.Add(weekManualToBox);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "%",
            ForeColor = Color.FromArgb(87, 99, 116),
            Margin = new Padding(4, 6, 10, 0)
        });

        weekManualButton.Text = "估算";
        weekManualButton.Width = 72;
        weekManualButton.Height = 30;
        weekManualButton.Margin = new Padding(0, 1, 12, 0);
        weekManualButton.BackColor = Color.FromArgb(21, 128, 106);
        weekManualButton.ForeColor = Color.White;
        weekManualButton.FlatStyle = FlatStyle.Flat;
        weekManualButton.FlatAppearance.BorderSize = 0;
        weekManualButton.Click += async (_, _) => await EstimateManualWeekAsync();
        layout.Controls.Add(weekManualButton);

        weekManualResult.AutoSize = false;
        weekManualResult.Width = 820;
        weekManualResult.Height = 42;
        weekManualResult.Text = "输入剩余百分比后估算当前 7d 周额度";
        weekManualResult.TextAlign = ContentAlignment.MiddleLeft;
        weekManualResult.AutoEllipsis = false;
        weekManualResult.ForeColor = Color.FromArgb(55, 65, 81);
        weekManualResult.Margin = new Padding(0, 0, 0, 0);
        layout.Controls.Add(weekManualResult);

        return panel;
    }

    private static void ConfigurePercentBox(NumericUpDown box, decimal value)
    {
        box.Minimum = 0;
        box.Maximum = 100;
        box.DecimalPlaces = 0;
        box.Value = value;
        box.Width = 58;
        box.Height = 30;
        box.TextAlign = HorizontalAlignment.Right;
        box.Margin = new Padding(0, 1, 0, 0);
    }

    private void ConfigureWeeklyList()
    {
        weeklyList.Dock = DockStyle.Fill;
        weeklyList.View = View.Details;
        weeklyList.FullRowSelect = true;
        weeklyList.GridLines = false;
        weeklyList.HideSelection = false;
        weeklyList.Columns.Add("周期", 260);
        weeklyList.Columns.Add("重置", 110);
        weeklyList.Columns.Add("快照", 64, HorizontalAlignment.Right);
        weeklyList.Columns.Add("剩余", 74, HorizontalAlignment.Right);
        weeklyList.Columns.Add("已用", 70, HorizontalAlignment.Right);
        weeklyList.Columns.Add("Tokens", 112, HorizontalAlignment.Right);
        weeklyList.Columns.Add("已用 $", 90, HorizontalAlignment.Right);
        weeklyList.Columns.Add("100% $", 90, HorizontalAlignment.Right);
        weeklyList.Columns.Add("套餐", 120);
        weeklyList.Columns.Add("实际 ¥", 90, HorizontalAlignment.Right);
    }

    private async Task LoadRowsAsync()
    {
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        var cancellationToken = loadCancellation.Token;

        try
        {
            statusLabel.Text = "正在加载当前窗口...";
            weeklyList.Items.Clear();
            currentWindowRowsLoaded = 0;

            var currentRows = await Task.Run(BuildCurrentRows, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            ApplyCurrentRows(currentRows);
            currentWindowRowsLoaded = currentRows.Count;

            var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
            statusLabel.Text = "正在读取历史快照...";
            var periods = await Task.Run(() => BuildWeeklyPeriods(now), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            weeklyList.Items.Clear();
            if (periods.Count == 0)
            {
                statusLabel.Text = $"当前窗口已加载，没有可用的 7d 重置时间 {DateTime.Now:HH:mm:ss}";
                return;
            }

            for (var index = 0; index < periods.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var period = periods[index];
                statusLabel.Text = $"历史周期 {index + 1}/{periods.Count}：{period.PeriodStart:MM-dd HH:mm} - {period.PeriodEnd:MM-dd HH:mm}";

                var row = await Task.Run(() => BuildWeeklyRow(period, currentQuota.Week), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (row is not null)
                {
                    weeklyList.Items.Add(row);
                }
            }

            statusLabel.Text = $"已加载 {currentWindowRowsLoaded + weeklyList.Items.Count:N0} 行 {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing)
            {
                statusLabel.Text = "加载已取消";
            }
        }
        catch (Exception ex)
        {
            statusLabel.Text = "加载失败";
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task EstimateManualWeekAsync()
    {
        if (currentQuota.Week is null)
        {
            weekManualResult.Text = "没有当前 7d 额度窗口";
            return;
        }

        weekManualButton.Enabled = false;
        weekManualResult.Text = "正在估算...";
        try
        {
            var fromRemaining = weekManualFromBox.Value;
            var toRemaining = weekManualToBox.Value;
            var result = await Task.Run(() => BuildManualWeekEstimate(fromRemaining, toRemaining));
            weekManualResult.Text = result;
        }
        catch (Exception ex)
        {
            weekManualResult.Text = $"估算失败：{ex.Message}";
        }
        finally
        {
            weekManualButton.Enabled = true;
        }
    }

    private string BuildManualWeekEstimate(decimal firstRemainingInput, decimal secondRemainingInput)
    {
        var week = currentQuota.Week;
        if (week is null)
        {
            return "没有当前 7d 额度窗口";
        }

        var fromRemaining = Math.Max(firstRemainingInput, secondRemainingInput);
        var toRemaining = Math.Min(firstRemainingInput, secondRemainingInput);
        var requestedDelta = fromRemaining - toRemaining;
        if (requestedDelta <= 0)
        {
            return "请选择不同的剩余百分比";
        }

        var startUsedThreshold = 100m - fromRemaining;
        var endUsedThreshold = 100m - toRemaining;
        var resetAt = week.ResetAtLocal ?? week.WindowEndLocal;
        var snapshots = CodexUsageReader.ReadQuotaSnapshots(week.WindowStartLocal, week.WindowEndLocal.AddMinutes(1))
            .Where(item =>
                item.WeekUsedPercent is not null &&
                IsSameQuotaReset(item.WeekResetAtLocal, resetAt))
            .Append(new CodexQuotaSnapshot(
                currentQuota.SnapshotLocal,
                currentQuota.LimitId,
                currentQuota.LimitName,
                null,
                null,
                week.UsedPercent,
                resetAt))
            .GroupBy(item => item.SnapshotLocal)
            .Select(group => group.OrderByDescending(item => item.WeekUsedPercent ?? -1m).First())
            .OrderBy(item => item.SnapshotLocal)
            .ToList();

        if (snapshots.Count < 2)
        {
            return "当前 7d 快照太少，暂时不能按区间估算";
        }

        var start = snapshots.FirstOrDefault(item => item.WeekUsedPercent >= startUsedThreshold);
        if (start is null)
        {
            return $"当前周还没进入剩余 {fromRemaining:N0}% 附近";
        }

        var end = snapshots.FirstOrDefault(item =>
            item.SnapshotLocal > start.SnapshotLocal &&
            item.WeekUsedPercent >= endUsedThreshold);
        if (end is null)
        {
            return $"当前周还没到剩余 {toRemaining:N0}%";
        }

        var startUsed = start.WeekUsedPercent!.Value;
        var endUsed = end.WeekUsedPercent!.Value;
        var observedDelta = endUsed - startUsed;
        if (observedDelta <= 0)
        {
            return "区间内没有可用的额度下降";
        }

        var usage = CodexUsageReader.ReadRangeFromDetailRows(start.SnapshotLocal, end.SnapshotLocal);
        var usedCost = usage.EstimateCost(PriceProfiles.Gpt55StandardLong);
        var estimatedLimit = usedCost / (observedDelta / 100m);
        var actualFromRemaining = 100m - startUsed;
        var actualToRemaining = 100m - endUsed;

        return
            $"{actualFromRemaining:N0}%->{actualToRemaining:N0}% ({observedDelta:N0}%) " +
            $"{start.SnapshotLocal:MM-dd HH:mm}-{end.SnapshotLocal:HH:mm}，" +
            $"{FormatTokenMillions(usage.TotalTokens)}，{FormatMoney(usedCost)}，100%≈{FormatMoney(estimatedLimit)}";
    }

    private List<CurrentWindowRow> BuildCurrentRows()
    {
        return new List<CurrentWindowRow>
        {
            BuildCurrentRow("5h", currentQuota.FiveHour),
            BuildCurrentRow("7d", currentQuota.Week)
        };
    }

    private void ApplyCurrentRows(IReadOnlyList<CurrentWindowRow> rows)
    {
        foreach (var row in rows)
        {
            var target = row.Label == "5h" ? fiveHourCard : weekCard;
            target.Value.Text = row.RemainingText;
            target.Detail.Text = row.DetailText;
            target.Plan.Text = row.PlanText;
            target.Stable.Text = row.StableText;
        }
    }

    private IReadOnlyList<WeeklyQuotaPeriod> BuildWeeklyPeriods(DateTimeOffset now)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, CodexUsageReader.BeijingOffset);
        var snapshots = CodexUsageReader.ReadCachedQuotaSnapshots(start, now.AddMinutes(1))
            .Where(item => item.WeekResetAtLocal is not null && item.WeekUsedPercent is not null)
            .Concat(CurrentWeekSnapshot(currentQuota))
            .GroupBy(item => item.SnapshotLocal)
            .Select(group => group.OrderByDescending(item => item.WeekUsedPercent ?? -1m).First())
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
        var periods = BuildActualWeeklyPeriods(snapshots, now);

        var currentWeek = currentQuota.Week;
        var currentReset = currentWeek?.ResetAtLocal;
        if (currentWeek is not null && currentReset is not null)
        {
            var currentPeriod = periods.FirstOrDefault(item =>
                item.Snapshots.Any(snapshot => IsSameQuotaReset(snapshot.WeekResetAtLocal, currentReset)) &&
                item.PeriodStart <= currentQuota.SnapshotLocal &&
                item.PeriodEnd >= currentQuota.SnapshotLocal.AddSeconds(-1));
            if (currentPeriod is not null)
            {
                var index = periods.IndexOf(currentPeriod);
                periods[index] = currentPeriod with
                {
                    PeriodEnd = currentWeek.WindowEndLocal,
                    ResetAt = currentReset.Value,
                    IsCurrent = true
                };
            }
        }

        return periods
            .Where(item => item.PeriodEnd > item.PeriodStart)
            .OrderByDescending(item => item.PeriodStart)
            .ToList();
    }

    private static List<WeeklyQuotaPeriod> BuildActualWeeklyPeriods(
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        DateTimeOffset now)
    {
        var periods = new List<WeeklyQuotaPeriod>();
        if (snapshots.Count == 0)
        {
            return periods;
        }

        var current = new List<CodexQuotaSnapshot> { snapshots[0] };
        for (var index = 1; index < snapshots.Count; index++)
        {
            var previous = snapshots[index - 1];
            var snapshot = snapshots[index];
            if (StartsNewQuotaCycle(previous, snapshot))
            {
                AddWeeklyPeriod(periods, current, snapshot.SnapshotLocal, isCurrent: false);
                current = new List<CodexQuotaSnapshot> { snapshot };
            }
            else
            {
                current.Add(snapshot);
            }
        }

        AddWeeklyPeriod(periods, current, now, isCurrent: true);
        return periods;
    }

    private static void AddWeeklyPeriod(
        List<WeeklyQuotaPeriod> periods,
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        DateTimeOffset periodEnd,
        bool isCurrent)
    {
        if (snapshots.Count == 0)
        {
            return;
        }

        var periodStart = snapshots[0].SnapshotLocal;
        var nominalReset = snapshots
            .Select(item => item.WeekResetAtLocal)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .OrderBy(item => item)
            .LastOrDefault();
        if (!isCurrent &&
            nominalReset != default &&
            nominalReset > periodStart &&
            nominalReset < periodEnd)
        {
            periodEnd = nominalReset;
        }

        if (periodEnd <= periodStart)
        {
            periodEnd = periodStart.AddSeconds(1);
        }

        periods.Add(new WeeklyQuotaPeriod(
            periodStart,
            periodEnd,
            isCurrent
                ? snapshots[^1].WeekResetAtLocal ?? periodEnd
                : periodEnd,
            snapshots,
            isCurrent));
    }

    private static bool StartsNewQuotaCycle(CodexQuotaSnapshot previous, CodexQuotaSnapshot current)
    {
        if (previous.WeekUsedPercent is not { } previousUsed ||
            current.WeekUsedPercent is not { } currentUsed)
        {
            return false;
        }

        var resetChanged = !IsSameQuotaReset(previous.WeekResetAtLocal, current.WeekResetAtLocal);
        var resetMovedForward =
            previous.WeekResetAtLocal is not null &&
            current.WeekResetAtLocal is not null &&
            current.WeekResetAtLocal.Value > previous.WeekResetAtLocal.Value.Add(ResetClusterTolerance);
        var usedDroppedHard = currentUsed <= 2m && previousUsed >= 10m;
        var usedDropped = currentUsed + 2m < previousUsed;

        return usedDroppedHard ||
               resetMovedForward && (usedDropped || currentUsed <= 5m) ||
               resetChanged && usedDroppedHard;
    }

    private static IEnumerable<CodexQuotaSnapshot> CurrentWeekSnapshot(CodexQuotaEstimate quota)
    {
        if (quota.Week is null)
        {
            yield break;
        }

        yield return new CodexQuotaSnapshot(
            quota.SnapshotLocal,
            quota.LimitId,
            quota.LimitName,
            null,
            null,
            quota.Week.UsedPercent,
            quota.Week.ResetAtLocal);
    }

    private static CurrentWindowRow BuildCurrentRow(string label, CodexQuotaWindowEstimate? window)
    {
        if (window is null)
        {
            return new CurrentWindowRow(label, "-", "-", "", "");
        }

        var snapshots = CodexUsageReader.ReadQuotaSnapshots(window.WindowStartLocal, window.WindowEndLocal.AddMinutes(1));
        var delta = BuildQuotaDeltaEstimate(
            label,
            snapshots,
            label == "5h" ? item => item.FiveHourUsedPercent : item => item.WeekUsedPercent,
            label == "5h" ? item => item.FiveHourResetAtLocal : item => item.WeekResetAtLocal);
        var plan = SubscriptionPlanStore.Summarize(window.WindowStartLocal, window.WindowEndLocal);
        var remaining = Math.Max(0m, 100m - window.UsedPercent);
        var detail =
            $"{FormatTokenMillions(window.Usage.TotalTokens)} · " +
            $"{FormatMoney(window.UsedGptCost)} · 100% {FormatNullableMoney(window.EstimatedGptLimit)}";
        var planText = plan.HasRecords
            ? $"{plan.PlanNames} · {FormatCny(plan.AmountCny)}"
            : "";
        var stableText =
            $"{window.WindowStartLocal:MM-dd HH:mm}-{window.WindowEndLocal:MM-dd HH:mm} · {FormatDelta(delta)}";
        return new CurrentWindowRow(
            label,
            $"{remaining:N0}%",
            detail,
            planText,
            stableText);
    }

    private static ListViewItem? BuildWeeklyRow(
        WeeklyQuotaPeriod period,
        CodexQuotaWindowEstimate? currentWeek)
    {
        if (period.PeriodEnd <= period.PeriodStart)
        {
            return null;
        }

        var usage = period.IsCurrent && currentWeek is not null
            ? currentWeek.Usage
            : CodexUsageReader.ReadCachedRange(period.PeriodStart, period.PeriodEnd);
        if (usage.Events == 0 && period.Snapshots.Count == 0)
        {
            return null;
        }

        var usedPercents = period.Snapshots
            .Select(item => item.WeekUsedPercent)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .ToList();
        if (period.IsCurrent && currentWeek is not null)
        {
            usedPercents.Add(currentWeek.UsedPercent);
        }

        var maxUsed = usedPercents.Count > 0 ? usedPercents.Max() : (decimal?)null;
        var usedCost = period.IsCurrent && currentWeek is not null
            ? currentWeek.UsedGptCost
            : usage.EstimateCost(PriceProfiles.Gpt55StandardLong);
        var estimatedLimit = maxUsed is > 0m
            ? usedCost / (maxUsed.Value / 100m)
            : (decimal?)null;
        var plan = SubscriptionPlanStore.Summarize(period.PeriodStart, period.PeriodEnd);

        return new ListViewItem(new[]
        {
            $"{period.PeriodStart:MM-dd HH:mm} - {period.PeriodEnd:MM-dd HH:mm}",
            period.ResetAt.ToString("MM-dd HH:mm"),
            period.Snapshots.Count.ToString("N0"),
            FormatRemainingChange(maxUsed),
            maxUsed is null ? "-" : $"{maxUsed.Value:N0}%",
            FormatTokenMillions(usage.TotalTokens),
            FormatMoney(usedCost),
            FormatNullableMoney(estimatedLimit),
            plan.PlanNames,
            plan.HasRecords ? FormatCny(plan.AmountCny) : "-"
        });
    }

    private static QuotaDeltaEstimate? BuildQuotaDeltaEstimate(
        string label,
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        Func<CodexQuotaSnapshot, decimal?> usedPercentSelector,
        Func<CodexQuotaSnapshot, DateTimeOffset?> resetAtSelector)
    {
        var ordered = snapshots.OrderBy(item => item.SnapshotLocal).ToList();
        if (ordered.Count < 2)
        {
            return null;
        }

        var current = ordered[^1];
        var currentUsed = usedPercentSelector(current);
        if (currentUsed is null)
        {
            return null;
        }

        var currentReset = resetAtSelector(current);
        var candidates = new List<QuotaDeltaCandidate>();
        for (var index = 0; index < ordered.Count - 1; index++)
        {
            var previous = ordered[index];
            var previousUsed = usedPercentSelector(previous);
            if (previousUsed is null)
            {
                continue;
            }

            var usedDelta = currentUsed.Value - previousUsed.Value;
            if (usedDelta <= 0)
            {
                continue;
            }

            if (IsSameQuotaReset(resetAtSelector(previous), currentReset))
            {
                candidates.Add(new QuotaDeltaCandidate(previous, previousUsed.Value, usedDelta));
            }
        }

        var candidate = candidates
            .Where(item => item.UsedDeltaPercent >= MinimumStableQuotaDeltaPercent)
            .OrderByDescending(item => item.UsedDeltaPercent)
            .ThenBy(item => item.Snapshot.SnapshotLocal)
            .FirstOrDefault();
        candidate ??= candidates
            .OrderByDescending(item => item.UsedDeltaPercent)
            .ThenBy(item => item.Snapshot.SnapshotLocal)
            .FirstOrDefault();
        if (candidate is null)
        {
            return null;
        }

        var usage = CodexUsageReader.ReadRangeFromDetailRows(candidate.Snapshot.SnapshotLocal, current.SnapshotLocal);
        var usedCost = usage.EstimateCost(PriceProfiles.Gpt55StandardLong);
        var estimatedLimit = usedCost / (candidate.UsedDeltaPercent / 100m);
        return new QuotaDeltaEstimate(
            label,
            candidate.Snapshot.SnapshotLocal,
            current.SnapshotLocal,
            100m - candidate.PreviousUsedPercent,
            100m - currentUsed.Value,
            candidate.UsedDeltaPercent,
            usage,
            usedCost,
            estimatedLimit,
            candidate.UsedDeltaPercent >= MinimumStableQuotaDeltaPercent);
    }

    private static string FormatDelta(QuotaDeltaEstimate? estimate)
    {
        if (estimate is null)
        {
            return "-";
        }

        var reliability = estimate.IsStable ? "稳定" : "参考";
        return $"{reliability} {estimate.PreviousRemainingPercent:N0}%->{estimate.CurrentRemainingPercent:N0}% ({estimate.UsedDeltaPercent:N0}%), {FormatMoney(estimate.UsedCost)}, 100%≈{FormatMoney(estimate.EstimatedLimit)}";
    }

    private static string FormatRemainingChange(decimal? maxUsed)
    {
        if (maxUsed is null)
        {
            return "-";
        }

        return $"{Math.Max(0m, 100m - maxUsed.Value):N0}%";
    }

    private static bool IsSameQuotaReset(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null || second is null)
        {
            return true;
        }

        return (first.Value - second.Value).Duration() <= ResetClusterTolerance;
    }

    private static string FormatTokenMillions(long value)
    {
        return $"{value / 1_000_000d:N3}M";
    }

    private static string FormatMoney(decimal value)
    {
        return value switch
        {
            >= 100 => $"${value:N0}",
            >= 10 => $"${value:N2}",
            >= 1 => $"${value:N3}",
            _ => $"${value:N4}"
        };
    }

    private static string FormatNullableMoney(decimal? value)
    {
        return value is null ? "-" : FormatMoney(value.Value);
    }

    private static string FormatCny(decimal value)
    {
        return value switch
        {
            >= 100 => $"¥{value:N0}",
            >= 10 => $"¥{value:N2}",
            >= 1 => $"¥{value:N2}",
            _ => $"¥{value:N4}"
        };
    }

    private sealed record QuotaDeltaEstimate(
        string Label,
        DateTimeOffset StartLocal,
        DateTimeOffset EndLocal,
        decimal PreviousRemainingPercent,
        decimal CurrentRemainingPercent,
        decimal UsedDeltaPercent,
        TokenUsageSummary Usage,
        decimal UsedCost,
        decimal EstimatedLimit,
        bool IsStable);

    private sealed record QuotaDeltaCandidate(
        CodexQuotaSnapshot Snapshot,
        decimal PreviousUsedPercent,
        decimal UsedDeltaPercent);

    private sealed class CurrentWindowCardBindings
    {
        public Label Value { get; } = new();
        public Label Detail { get; } = new();
        public Label Plan { get; } = new();
        public Label Stable { get; } = new();
    }

    private sealed record CurrentWindowRow(
        string Label,
        string RemainingText,
        string DetailText,
        string PlanText,
        string StableText);

    private sealed record WeeklyQuotaPeriod(
        DateTimeOffset PeriodStart,
        DateTimeOffset PeriodEnd,
        DateTimeOffset ResetAt,
        IReadOnlyList<CodexQuotaSnapshot> Snapshots,
        bool IsCurrent);

}
