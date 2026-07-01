namespace CodexTokenMonitor;

internal sealed class QuotaEstimateForm : Form
{
    private const decimal MinimumStableQuotaDeltaPercent = 3m;

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
        Size = new Size(1360, 820);
        MinimumSize = new Size(980, 680);
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 7,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(CreateHeader("当前窗口"), 0, 0);
        root.Controls.Add(BuildCurrentWindowPanel(), 0, 1);

        root.Controls.Add(BuildResetOpportunityPanel(), 0, 2);
        root.Controls.Add(BuildManualEstimateBar(), 0, 3);

        root.Controls.Add(CreateHeader("历史 7d 周期"), 0, 4);
        ConfigureWeeklyList();
        root.Controls.Add(weeklyList, 0, 5);

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
        root.Controls.Add(footer, 0, 6);
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
            Padding = new Padding(16, 14, 16, 14),
            Margin = new Padding(0, 0, 12, 0),
            BackColor = Color.White
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

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
        bindings.Value.Font = new Font("Segoe UI", 24f, FontStyle.Bold);
        bindings.Value.ForeColor = Color.FromArgb(29, 40, 55);
        bindings.Value.Margin = new Padding(0);
        panel.Controls.Add(bindings.Value, 0, 1);

        bindings.Detail.Dock = DockStyle.Fill;
        bindings.Detail.AutoSize = false;
        bindings.Detail.Text = "-";
        bindings.Detail.TextAlign = ContentAlignment.MiddleLeft;
        bindings.Detail.Font = new Font("Microsoft YaHei UI", 9.8f);
        bindings.Detail.ForeColor = Color.FromArgb(55, 65, 81);
        bindings.Detail.AutoEllipsis = true;
        bindings.Detail.Margin = new Padding(0, 4, 0, 0);
        panel.Controls.Add(bindings.Detail, 1, 1);

        bindings.Stable.Dock = DockStyle.Fill;
        bindings.Stable.AutoSize = false;
        bindings.Stable.TextAlign = ContentAlignment.MiddleLeft;
        bindings.Stable.Font = new Font("Microsoft YaHei UI", 9.2f);
        bindings.Stable.ForeColor = Color.FromArgb(87, 99, 116);
        bindings.Stable.AutoEllipsis = true;
        bindings.Stable.Margin = new Padding(0);
        panel.Controls.Add(bindings.Stable, 0, 2);
        panel.SetColumnSpan(bindings.Stable, 2);

        return panel;
    }

    private static Control BuildResetOpportunityPanel()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var summary = ResetOpportunityStore.Summarize(now);

        var panel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(12, 8, 12, 8)
        };

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Color.Transparent
        };
        panel.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = ResetOpportunityFormatter.FormatPanelTitle(summary),
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 41, 55),
            Margin = new Padding(0, 3, 18, 4)
        });

        if (summary.AvailableCount == 0)
        {
            layout.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "无未过期重置卡",
                ForeColor = Color.FromArgb(87, 99, 116),
                Margin = new Padding(0, 3, 0, 4)
            });
            return panel;
        }

        foreach (var record in summary.AvailableRecords)
        {
            layout.Controls.Add(new Label
            {
                AutoSize = true,
                Text = ResetOpportunityFormatter.FormatRecordLine(record, now),
                ForeColor = Color.FromArgb(55, 65, 81),
                Margin = new Padding(0, 3, 24, 4)
            });
        }

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
        weeklyList.Columns.Add("周期", 230);
        weeklyList.Columns.Add("重置", 96);
        weeklyList.Columns.Add("快照", 58, HorizontalAlignment.Right);
        weeklyList.Columns.Add("剩余", 66, HorizontalAlignment.Right);
        weeklyList.Columns.Add("已用", 62, HorizontalAlignment.Right);
        weeklyList.Columns.Add("应%", 62, HorizontalAlignment.Right);
        weeklyList.Columns.Add("节奏", 126, HorizontalAlignment.Right);
        weeklyList.Columns.Add("Tokens", 108, HorizontalAlignment.Right);
        weeklyList.Columns.Add("已用 $", 82, HorizontalAlignment.Right);
        weeklyList.Columns.Add("100% $", 82, HorizontalAlignment.Right);
        weeklyList.Columns.Add("套餐", 96);
        weeklyList.Columns.Add("实际 ¥", 84, HorizontalAlignment.Right);
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
        return QuotaEstimateCalculator.BuildManualWeekEstimate(currentQuota, firstRemainingInput, secondRemainingInput);
    }

    private IReadOnlyList<QuotaCurrentWindowRow> BuildCurrentRows()
    {
        return QuotaEstimateCalculator.BuildCurrentRows(currentQuota);
    }

    private void ApplyCurrentRows(IReadOnlyList<QuotaCurrentWindowRow> rows)
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

    private IReadOnlyList<CodexQuotaCycle> BuildWeeklyPeriods(DateTimeOffset now)
    {
        return QuotaEstimateCalculator.BuildWeeklyPeriods(currentQuota, now);
    }

    private static ListViewItem? BuildWeeklyRow(
        CodexQuotaCycle period,
        CodexQuotaWindowEstimate? currentWeek)
    {
        if (period.PeriodEnd <= period.PeriodStart)
        {
            return null;
        }

        var row = QuotaEstimateCalculator.BuildWeeklyRow(period, currentWeek);
        return row is null
            ? null
            : new ListViewItem(new[]
        {
            row.Period,
            row.ResetAt,
            row.SnapshotCount,
            row.Remaining,
            row.UsedPercent,
            row.ExpectedPercent,
            row.Rhythm,
            row.Tokens,
            row.UsedCost,
            row.EstimatedLimit,
            row.PlanNames,
            row.PlanAmount
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

            if (CodexQuotaCycleReader.IsSameQuotaReset(resetAtSelector(previous), currentReset))
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

}
