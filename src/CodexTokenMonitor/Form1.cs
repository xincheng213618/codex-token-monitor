namespace CodexTokenMonitor;

public partial class Form1 : Form
{
    private const int BackgroundCacheIntervalMs = 120_000;
    private const int CostCardWidth = 218;
    private const int CostCardRightMargin = 14;
    private const decimal MinimumStableQuotaDeltaPercent = 3m;
    private static readonly TimeSpan DayTimelineInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MultiDayBreakdownInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MonthTimelineInterval = TimeSpan.FromHours(1);
    private static readonly DateTimeOffset BackgroundCacheStart = new(
        2026,
        1,
        1,
        0,
        0,
        0,
        CodexUsageReader.BeijingOffset);

    private enum RangeMode
    {
        Day,
        Week,
        Month,
        Cycle
    }

    private enum QuotaWindowDisplayMode
    {
        FiveHour,
        Week
    }

    private readonly Label titleLabel = new();
    private readonly TabControl sourceTabs = new();
    private readonly Button resetSettingsButton = new();
    private readonly Button planSettingsButton = new();
    private readonly Button priceSettingsButton = new();
    private readonly ComboBox rangeModeBox = new();
    private readonly DateTimePicker datePicker = new();
    private readonly ComboBox cycleBox = new();
    private readonly Button previousButton = new();
    private readonly Button nextButton = new();
    private readonly Button currentButton = new();
    private readonly Button startNowButton = new();
    private readonly Button refreshDayButton = new();
    private readonly Label queryStatusValue = new();
    private readonly DateTimePicker startTimePicker = new();
    private readonly Button weekPickerButton = new();
    private readonly Label priceLabel = new();
    private readonly Label totalValue = new();
    private readonly Label periodValue = new();
    private readonly Label inputValue = new();
    private readonly Label cachedValue = new();
    private readonly Label uncachedValue = new();
    private readonly Label outputValue = new();
    private readonly Label reasoningValue = new();
    private readonly Label cacheRatioValue = new();
    private readonly Label eventsValue = new();
    private readonly Label lastEventValue = new();
    private readonly Panel quotaPanel = new();
    private readonly Label quota5hValue = new();
    private readonly Label quota5hDetail = new();
    private readonly Label quotaWeekValue = new();
    private readonly Label quotaWeekDetail = new();
    private readonly Label planSpendValue = new();
    private readonly Label planSpendDetail = new();
    private readonly Label resetOpportunityValue = new();
    private readonly Label resetOpportunityDetail = new();
    private readonly Label resetPaceValue = new();
    private readonly Label resetPaceDetail = new();
    private readonly Button quotaCalculateButton = new();
    private readonly Label quotaLimitValue = new();
    private readonly Label gpt55CostValue = new();
    private readonly Label deepSeekCostValue = new();
    private readonly Label xiaomiCreditValue = new();
    private readonly Label costPanel1Title = new();
    private readonly Label costPanel1Subtitle = new();
    private readonly Label costPanel2Title = new();
    private readonly Label costPanel2Subtitle = new();
    private readonly Label costPanel3Title = new();
    private readonly Label costPanel3Subtitle = new();
    private readonly FlowLayoutPanel costCardsFlow = new();
    private readonly Label breakdownTitle = new();
    private readonly Label statusValue = new();
    private readonly Button refreshButton = new();
    private readonly Button clearCacheButton = new();
    private readonly CheckBox autoRefreshBox = new();
    private readonly TokenTimelineControl timelineChart = new();
    private readonly ListView breakdownList = new NoHorizontalScrollListView();
    private readonly System.Windows.Forms.Timer refreshTimer = new();
    private readonly System.Windows.Forms.Timer backgroundCacheTimer = new();
    private readonly SemaphoreSlim usageQueryGate = new(1, 1);
    private RowStyle? timelineRowStyle;
    private CancellationTokenSource? backgroundCacheCts;
    private DateTimeOffset? customStartLocal;
    private CodexQuotaEstimate? currentQuotaEstimate;
    private IReadOnlyList<CodexQuotaSnapshot> currentQuotaSnapshots = Array.Empty<CodexQuotaSnapshot>();
    private IReadOnlyList<CodexQuotaCycle> quotaCycles = Array.Empty<CodexQuotaCycle>();
    private bool isRefreshing;
    private bool isQuotaRefreshing;
    private bool isBackgroundCaching;
    private bool suppressDateRefresh;
    private bool suppressCycleRefresh;
    private bool suppressStartTimeRefresh;

    public Form1()
    {
        InitializeComponent();
        BuildUi();

        quotaCalculateButton.Click += (_, _) => UpdateQuotaLimitCalculation();
        resetSettingsButton.Click += async (_, _) => await OpenResetOpportunitySettingsAsync();
        planSettingsButton.Click += async (_, _) => await OpenSubscriptionPlanSettingsAsync();
        priceSettingsButton.Click += async (_, _) => await OpenPriceSettingsAsync();
        previousButton.Click += async (_, _) => await ShiftPeriodAsync(-1);
        nextButton.Click += async (_, _) => await ShiftPeriodAsync(1);
        currentButton.Click += async (_, _) => await JumpToCurrentPeriodAsync();
        startNowButton.Click += async (_, _) => await StartFromNowAsync();
        refreshDayButton.Click += async (_, _) => await RefreshSelectedDayFromCacheAsync();
        startTimePicker.ValueChanged += async (_, _) => await StartTimeChangedAsync();
        weekPickerButton.Click += async (_, _) => await OpenWeekPickerAsync();
        sourceTabs.SelectedIndexChanged += async (_, _) => await SourceChangedAsync();
        rangeModeBox.SelectedIndexChanged += async (_, _) => await RangeModeChangedAsync();
        cycleBox.SelectedIndexChanged += async (_, _) =>
        {
            if (!suppressCycleRefresh)
            {
                ClearCustomStart();
                UpdateRangeControls();
                await RefreshUsageAsync();
            }
        };
        datePicker.ValueChanged += async (_, _) =>
        {
            if (!suppressDateRefresh)
            {
                ClearCustomStart();
                UpdateRangeControls();
                await RefreshUsageAsync();
            }
        };
        refreshTimer.Interval = 30_000;
        refreshTimer.Tick += async (_, _) =>
        {
            _ = RefreshQuotaSummaryAsync();
            await RefreshUsageAsync();
        };
        backgroundCacheTimer.Interval = BackgroundCacheIntervalMs;
        backgroundCacheTimer.Tick += async (_, _) => await WarmCacheInBackgroundAsync();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RefreshUsageAsync(cacheOnly: true);
        _ = RefreshQuotaSummaryAsync();
        _ = RefreshUsageAsync();
        _ = SyncResetOpportunitiesFromCodexAsync(showError: false);
        backgroundCacheTimer.Enabled = true;
        _ = WarmCacheInBackgroundAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        backgroundCacheCts?.Cancel();
        backgroundCacheTimer.Enabled = false;
        refreshTimer.Enabled = false;
        base.OnFormClosing(e);
    }

    private void BuildUi()
    {
        Text = "Codex Token 额度监控器";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 1000);
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 960);
        Size = new Size(
            Math.Min(1380, Math.Max(1000, workingArea.Width - 80)),
            Math.Min(1200, Math.Max(1120, workingArea.Height - 40)));
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 14, 20, 18),
            ColumnCount = 1,
            RowCount = 6,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildSourceTabs(), 0, 0);
        root.Controls.Add(BuildQuotaPanel(), 0, 1);
        root.Controls.Add(BuildQueryBar(), 0, 2);
        root.Controls.Add(BuildSummaryPanel(), 0, 3);
        root.Controls.Add(BuildMetricGrid(), 0, 4);
        root.Controls.Add(BuildBreakdownPanel(), 0, 5);

        rangeModeBox.SelectedIndex = 0;
        refreshTimer.Enabled = true;
    }

    private Control BuildSourceTabs()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = BackColor,
            ColumnCount = 5,
            RowCount = 1
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        sourceTabs.Height = 38;
        sourceTabs.Dock = DockStyle.Fill;
        sourceTabs.Margin = new Padding(0);
        sourceTabs.Appearance = TabAppearance.Normal;
        sourceTabs.TabPages.Add("Codex");
        sourceTabs.TabPages.Add("Claude Code");
        sourceTabs.TabPages.Add("ZCode");
        sourceTabs.SelectedIndex = 0;
        panel.Controls.Add(sourceTabs, 0, 0);

        queryStatusValue.AutoSize = false;
        queryStatusValue.Dock = DockStyle.Fill;
        queryStatusValue.Text = "";
        queryStatusValue.TextAlign = ContentAlignment.MiddleRight;
        queryStatusValue.AutoEllipsis = true;
        queryStatusValue.ForeColor = Color.FromArgb(87, 99, 116);
        queryStatusValue.Margin = new Padding(0, 4, 12, 0);
        panel.Controls.Add(queryStatusValue, 1, 0);

        ConfigureTopButton(resetSettingsButton, "重置设置");
        resetSettingsButton.Margin = new Padding(0, 0, 8, 0);
        panel.Controls.Add(resetSettingsButton, 2, 0);

        ConfigureTopButton(planSettingsButton, "套餐设置");
        planSettingsButton.Margin = new Padding(0, 0, 8, 0);
        panel.Controls.Add(planSettingsButton, 3, 0);

        ConfigureTopButton(priceSettingsButton, "价格设置");
        panel.Controls.Add(priceSettingsButton, 4, 0);

        return panel;
    }

    private static void ConfigureTopButton(Button button, string text)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0);
        button.BackColor = Color.FromArgb(229, 234, 242);
        button.ForeColor = Color.FromArgb(55, 65, 81);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
    }

    private Control BuildQueryBar()
    {
        var panel = CreatePanel();
        panel.Padding = new Padding(14, 12, 14, 12);
        panel.Margin = new Padding(0, 0, 0, 14);
        panel.Height = 64;
        panel.Dock = DockStyle.Top;

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent
        };
        panel.Controls.Add(layout);

        rangeModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        rangeModeBox.Items.AddRange(new object[] { "按天", "按周", "按月", "按周期" });
        rangeModeBox.Width = 110;
        rangeModeBox.Margin = new Padding(0, 5, 12, 0);
        layout.Controls.Add(rangeModeBox);

        previousButton.Text = "<";
        previousButton.Width = 42;
        previousButton.Height = 32;
        previousButton.Margin = new Padding(0, 4, 6, 0);
        layout.Controls.Add(previousButton);

        datePicker.Format = DateTimePickerFormat.Custom;
        datePicker.CustomFormat = "yyyy-MM-dd";
        datePicker.Width = 140;
        datePicker.Margin = new Padding(0, 4, 6, 0);
        datePicker.Value = DateTime.Today;
        layout.Controls.Add(datePicker);

        cycleBox.DropDownStyle = ComboBoxStyle.DropDownList;
        cycleBox.Width = 300;
        cycleBox.Height = 32;
        cycleBox.Margin = new Padding(0, 4, 6, 0);
        cycleBox.Visible = false;
        layout.Controls.Add(cycleBox);

        weekPickerButton.Text = "选7天";
        weekPickerButton.Width = 78;
        weekPickerButton.Height = 32;
        weekPickerButton.Margin = new Padding(0, 4, 6, 0);
        weekPickerButton.Visible = false;
        layout.Controls.Add(weekPickerButton);

        nextButton.Text = ">";
        nextButton.Width = 42;
        nextButton.Height = 32;
        nextButton.Margin = new Padding(0, 4, 12, 0);
        layout.Controls.Add(nextButton);

        currentButton.Text = "今天";
        currentButton.Width = 86;
        currentButton.Height = 32;
        currentButton.Margin = new Padding(0, 4, 12, 0);
        layout.Controls.Add(currentButton);

        startNowButton.Text = "从当前算";
        startNowButton.Width = 118;
        startNowButton.Height = 32;
        startNowButton.Margin = new Padding(0, 4, 12, 0);
        startNowButton.BackColor = Color.FromArgb(229, 234, 242);
        startNowButton.ForeColor = Color.FromArgb(55, 65, 81);
        startNowButton.FlatStyle = FlatStyle.Flat;
        startNowButton.FlatAppearance.BorderSize = 0;
        layout.Controls.Add(startNowButton);

        refreshDayButton.Text = "刷新";
        refreshDayButton.Width = 72;
        refreshDayButton.Height = 32;
        refreshDayButton.Margin = new Padding(0, 4, 12, 0);
        refreshDayButton.BackColor = Color.FromArgb(229, 234, 242);
        refreshDayButton.ForeColor = Color.FromArgb(55, 65, 81);
        refreshDayButton.FlatStyle = FlatStyle.Flat;
        refreshDayButton.FlatAppearance.BorderSize = 0;
        layout.Controls.Add(refreshDayButton);

        startTimePicker.Format = DateTimePickerFormat.Custom;
        startTimePicker.CustomFormat = "yyyy-MM-dd HH:mm:ss";
        startTimePicker.ShowUpDown = true;
        startTimePicker.Width = 180;
        startTimePicker.Height = 32;
        startTimePicker.Margin = new Padding(0, 4, 12, 0);
        startTimePicker.Visible = false;
        layout.Controls.Add(startTimePicker);

        return panel;
    }

    private Control BuildSummaryPanel()
    {
        var summaryPanel = CreatePanel();
        summaryPanel.Padding = new Padding(22, 8, 22, 8);
        summaryPanel.Margin = new Padding(0, 0, 0, 12);
        summaryPanel.Height = 126;
        summaryPanel.Dock = DockStyle.Top;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        summaryPanel.Controls.Add(layout);

        var totalLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        totalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        totalLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        totalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        layout.Controls.Add(totalLayout, 0, 0);

        totalLayout.Controls.Add(CreateCaption("TOTAL TOKENS"), 0, 0);
        totalValue.AutoSize = false;
        totalValue.Dock = DockStyle.Fill;
        totalValue.Text = "-";
        totalValue.Font = new Font("Segoe UI", 23f, FontStyle.Bold);
        totalValue.ForeColor = Color.FromArgb(18, 114, 97);
        totalValue.TextAlign = ContentAlignment.MiddleLeft;
        totalValue.Margin = new Padding(0);
        totalLayout.Controls.Add(totalValue, 0, 1);

        periodValue.AutoSize = false;
        periodValue.Dock = DockStyle.Fill;
        periodValue.Text = "-";
        periodValue.Font = new Font("Microsoft YaHei UI", 8.8f);
        periodValue.ForeColor = Color.FromArgb(87, 99, 116);
        periodValue.TextAlign = ContentAlignment.TopLeft;
        periodValue.Margin = new Padding(0, 2, 0, 0);
        totalLayout.Controls.Add(periodValue, 0, 2);

        costCardsFlow.Dock = DockStyle.Fill;
        costCardsFlow.FlowDirection = FlowDirection.LeftToRight;
        costCardsFlow.WrapContents = false;
        costCardsFlow.AutoScroll = false;
        costCardsFlow.BackColor = Color.Transparent;
        costCardsFlow.Margin = new Padding(0);
        layout.Controls.Add(costCardsFlow, 1, 0);
        return summaryPanel;
    }

    private Control BuildCostPanel(
        string title,
        string subtitle,
        Label valueLabel,
        Label titleLabel,
        Label subtitleLabel)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14, 4, 0, 4),
            BackColor = Color.Transparent
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        titleLabel.AutoSize = true;
        titleLabel.Text = title;
        titleLabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        titleLabel.ForeColor = Color.FromArgb(92, 105, 122);
        titleLabel.Margin = new Padding(0);
        panel.Controls.Add(titleLabel, 0, 0);

        subtitleLabel.AutoSize = true;
        subtitleLabel.Text = subtitle;
        subtitleLabel.ForeColor = Color.FromArgb(101, 114, 130);
        subtitleLabel.Margin = new Padding(0, 2, 0, 8);
        panel.Controls.Add(subtitleLabel, 0, 1);

        valueLabel.AutoSize = true;
        valueLabel.Text = "-";
        valueLabel.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        valueLabel.ForeColor = Color.FromArgb(31, 41, 55);
        valueLabel.Margin = new Padding(0);
        panel.Controls.Add(valueLabel, 0, 2);
        return panel;
    }

    private Control BuildMetricGrid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 2,
            Height = 224,
            BackColor = BackColor,
            Margin = new Padding(0, 0, 0, 12)
        };
        for (var i = 0; i < 4; i++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        AddMetric(grid, 0, 0, "Input", inputValue);
        AddMetric(grid, 1, 0, "Cached Input", cachedValue);
        AddMetric(grid, 2, 0, "Uncached Input", uncachedValue);
        AddMetric(grid, 3, 0, "Output", outputValue);
        AddMetric(grid, 0, 1, "Reasoning Output", reasoningValue);
        AddMetric(grid, 1, 1, "Cache Ratio", cacheRatioValue);
        AddMetric(grid, 2, 1, "Events", eventsValue);
        AddMetric(grid, 3, 1, "Coding Time", lastEventValue);

        return grid;
    }

    private Control BuildQuotaPanel()
    {
        quotaPanel.Dock = DockStyle.Top;
        quotaPanel.Height = 104;
        quotaPanel.Margin = new Padding(0, 0, 0, 12);
        quotaPanel.Padding = new Padding(14, 8, 14, 8);
        quotaPanel.BackColor = Color.White;
        quotaPanel.Visible = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12));
        quotaPanel.Controls.Add(layout);

        layout.Controls.Add(BuildQuotaCard("5h", quota5hValue, quota5hDetail), 0, 0);
        layout.Controls.Add(BuildQuotaCard("周", quotaWeekValue, quotaWeekDetail), 1, 0);
        layout.Controls.Add(BuildQuotaCard("Pro 20x", planSpendValue, planSpendDetail), 2, 0);
        layout.Controls.Add(BuildQuotaCard("重置过期", resetOpportunityValue, resetOpportunityDetail), 3, 0);
        resetOpportunityValue.Font = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Bold);
        resetOpportunityDetail.Font = new Font("Segoe UI", 11.2f, FontStyle.Bold);
        layout.Controls.Add(BuildQuotaCard("重置评估", resetPaceValue, resetPaceDetail), 4, 0);
        resetPaceValue.Font = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Regular);
        resetPaceDetail.Font = new Font("Microsoft YaHei UI", 9.4f, FontStyle.Regular);
        resetPaceDetail.ForeColor = Color.FromArgb(55, 65, 81);
        layout.Controls.Add(BuildQuotaCalculationCard(), 5, 0);
        return quotaPanel;
    }

    private static Control BuildQuotaCard(string title, Label valueLabel, Label detailLabel)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8, 0, 8, 0),
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        valueLabel.Dock = DockStyle.Fill;
        valueLabel.AutoSize = false;
        valueLabel.Text = title;
        valueLabel.TextAlign = ContentAlignment.MiddleLeft;
        valueLabel.Font = new Font("Microsoft YaHei UI", 9.4f, FontStyle.Bold);
        valueLabel.ForeColor = Color.FromArgb(90, 103, 119);
        valueLabel.Margin = new Padding(0);
        valueLabel.AutoEllipsis = true;
        layout.Controls.Add(valueLabel, 0, 0);

        detailLabel.Dock = DockStyle.Fill;
        detailLabel.AutoSize = false;
        detailLabel.Text = "-";
        detailLabel.TextAlign = ContentAlignment.MiddleLeft;
        detailLabel.Font = new Font("Segoe UI", 14.5f, FontStyle.Bold);
        detailLabel.ForeColor = Color.FromArgb(29, 40, 55);
        detailLabel.Margin = new Padding(0);
        detailLabel.AutoEllipsis = true;
        layout.Controls.Add(detailLabel, 0, 1);

        return layout;
    }

    private Control BuildQuotaCalculationCard()
    {
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 30, 0, 0),
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        quotaCalculateButton.Text = "估算";
        quotaCalculateButton.Width = 86;
        quotaCalculateButton.Height = 34;
        quotaCalculateButton.Margin = new Padding(0);
        quotaCalculateButton.BackColor = Color.FromArgb(21, 128, 106);
        quotaCalculateButton.ForeColor = Color.White;
        quotaCalculateButton.FlatStyle = FlatStyle.Flat;
        quotaCalculateButton.FlatAppearance.BorderSize = 0;
        layout.Controls.Add(quotaCalculateButton);

        quotaLimitValue.Text = "";

        return layout;
    }

    private Control BuildBreakdownPanel()
    {
        var panel = CreatePanel();
        panel.Padding = new Padding(14);
        panel.Margin = new Padding(0);
        panel.Dock = DockStyle.Fill;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        timelineRowStyle = new RowStyle(SizeType.Absolute, 0);
        layout.RowStyles.Add(timelineRowStyle);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(layout);

        timelineChart.Dock = DockStyle.Fill;
        timelineChart.Margin = Padding.Empty;
        timelineChart.Visible = false;
        layout.Controls.Add(timelineChart, 0, 0);

        breakdownList.Dock = DockStyle.Fill;
        breakdownList.View = View.Details;
        breakdownList.FullRowSelect = true;
        breakdownList.GridLines = false;
        breakdownList.HideSelection = false;
        breakdownList.Columns.Add("日期", 104);
        breakdownList.Columns.Add("Total", 96, HorizontalAlignment.Right);
        breakdownList.Columns.Add("Input", 96, HorizontalAlignment.Right);
        breakdownList.Columns.Add("Cached", 96, HorizontalAlignment.Right);
        breakdownList.Columns.Add("Uncached", 104, HorizontalAlignment.Right);
        breakdownList.Columns.Add("Output", 88, HorizontalAlignment.Right);
        breakdownList.Columns.Add("GPT-5.5", 92, HorizontalAlignment.Right);
        breakdownList.Columns.Add("DeepSeek ¥", 108, HorizontalAlignment.Right);
        breakdownList.Columns.Add("Xiaomi Credits", 132, HorizontalAlignment.Right);
        breakdownList.Columns.Add("额度(5h/7d)", 126, HorizontalAlignment.Right);
        layout.Controls.Add(breakdownList, 0, 1);

        return panel;
    }

    private Control BuildActionBar()
    {
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 16, 0, 0),
            BackColor = BackColor
        };

        refreshButton.Text = "刷新";
        refreshButton.AutoSize = true;
        refreshButton.MinimumSize = new Size(84, 34);
        refreshButton.BackColor = Color.FromArgb(21, 128, 106);
        refreshButton.ForeColor = Color.White;
        refreshButton.FlatStyle = FlatStyle.Flat;
        refreshButton.FlatAppearance.BorderSize = 0;
        actions.Controls.Add(refreshButton);

        clearCacheButton.Text = "清缓存";
        clearCacheButton.AutoSize = true;
        clearCacheButton.MinimumSize = new Size(84, 34);
        clearCacheButton.BackColor = Color.FromArgb(229, 234, 242);
        clearCacheButton.ForeColor = Color.FromArgb(55, 65, 81);
        clearCacheButton.FlatStyle = FlatStyle.Flat;
        clearCacheButton.FlatAppearance.BorderSize = 0;
        clearCacheButton.Margin = new Padding(0, 0, 12, 0);
        actions.Controls.Add(clearCacheButton);

        autoRefreshBox.Text = "自动刷新";
        autoRefreshBox.AutoSize = true;
        autoRefreshBox.Checked = true;
        autoRefreshBox.Margin = new Padding(0, 8, 16, 0);
        autoRefreshBox.ForeColor = Color.FromArgb(55, 65, 81);
        actions.Controls.Add(autoRefreshBox);

        statusValue.AutoSize = true;
        statusValue.Text = "准备就绪";
        statusValue.Margin = new Padding(0, 8, 24, 0);
        statusValue.ForeColor = Color.FromArgb(87, 99, 116);
        actions.Controls.Add(statusValue);

        return actions;
    }

    private void SetStatus(string text)
    {
        statusValue.Text = text;
        queryStatusValue.Text = text;
        queryStatusValue.Visible = !string.IsNullOrWhiteSpace(text);
    }

    private static Panel CreatePanel()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Margin = new Padding(6),
            Padding = new Padding(14)
        };
    }

    private static Label CreateCaption(string text)
    {
        return new Label
        {
            AutoSize = true,
            Text = text,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(92, 105, 122),
            Margin = new Padding(0)
        };
    }

    private static void AddMetric(TableLayoutPanel grid, int column, int row, string label, Label valueLabel)
    {
        var panel = CreatePanel();
        panel.Padding = new Padding(12, 8, 12, 8);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(layout);

        var caption = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(90, 103, 119),
            Margin = new Padding(0)
        };
        layout.Controls.Add(caption, 0, 0);

        valueLabel.AutoSize = false;
        valueLabel.Dock = DockStyle.Fill;
        valueLabel.Text = "-";
        valueLabel.TextAlign = ContentAlignment.MiddleLeft;
        valueLabel.AutoEllipsis = true;
        valueLabel.Font = new Font("Segoe UI", 15.2f, FontStyle.Bold);
        valueLabel.ForeColor = Color.FromArgb(29, 40, 55);
        valueLabel.Margin = new Padding(0);
        valueLabel.Padding = new Padding(0);
        layout.Controls.Add(valueLabel, 0, 1);

        grid.Controls.Add(panel, column, row);
    }

    private async Task ShiftPeriodAsync(int delta)
    {
        ClearCustomStart();
        if (CurrentMode() == RangeMode.Cycle)
        {
            UpdateCycleOptions(keepSelection: true);
            if (cycleBox.Items.Count == 0)
            {
                return;
            }

            var targetIndex = Math.Clamp(cycleBox.SelectedIndex - delta, 0, cycleBox.Items.Count - 1);
            if (targetIndex == cycleBox.SelectedIndex)
            {
                return;
            }

            suppressCycleRefresh = true;
            cycleBox.SelectedIndex = targetIndex;
            suppressCycleRefresh = false;
            UpdateRangeControls();
            await RefreshUsageAsync();
            return;
        }

        suppressDateRefresh = true;
        datePicker.Value = CurrentMode() switch
        {
            RangeMode.Day => datePicker.Value.Date.AddDays(delta),
            RangeMode.Week => datePicker.Value.AddDays(delta * 7),
            RangeMode.Month => datePicker.Value.Date.AddMonths(delta),
            _ => datePicker.Value
        };
        suppressDateRefresh = false;
        UpdateRangeControls();
        await RefreshUsageAsync();
    }

    private async Task JumpToCurrentPeriodAsync()
    {
        ClearCustomStart();
        if (CurrentMode() == RangeMode.Cycle)
        {
            UpdateCycleOptions(keepSelection: false);
            if (cycleBox.Items.Count > 0)
            {
                suppressCycleRefresh = true;
                cycleBox.SelectedIndex = 0;
                suppressCycleRefresh = false;
            }

            UpdateRangeControls();
            await RefreshUsageAsync();
            return;
        }

        suppressDateRefresh = true;
        datePicker.Value = CurrentMode() == RangeMode.Week
            ? DateTime.Now
            : DateTime.Today;
        suppressDateRefresh = false;
        UpdateRangeControls();
        await RefreshUsageAsync();
    }

    private async Task RangeModeChangedAsync()
    {
        ClearCustomStart();
        if (CurrentMode() == RangeMode.Cycle && CurrentSource() != UsageSource.Codex)
        {
            sourceTabs.SelectedIndex = 0;
            return;
        }

        if (CurrentMode() == RangeMode.Week && datePicker.Value.Date == DateTime.Today)
        {
            suppressDateRefresh = true;
            datePicker.Value = DateTime.Now;
            suppressDateRefresh = false;
        }

        if (CurrentMode() == RangeMode.Cycle)
        {
            UpdateCycleOptions(keepSelection: false);
        }

        UpdateRangeControls();
        await RefreshUsageAsync();
    }

    private async Task SourceChangedAsync()
    {
        ClearCustomStart();
        if (CurrentMode() == RangeMode.Cycle && CurrentSource() != UsageSource.Codex)
        {
            rangeModeBox.SelectedIndex = 0;
            return;
        }

        UpdateRangeControls();
        await RefreshUsageAsync();
    }

    private async Task StartFromNowAsync()
    {
        customStartLocal = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        UpdateStartNowButtonState();
        await RefreshUsageAsync();
    }

    private async Task StartTimeChangedAsync()
    {
        if (suppressStartTimeRefresh || customStartLocal is null)
        {
            return;
        }

        customStartLocal = new DateTimeOffset(
            startTimePicker.Value.Year,
            startTimePicker.Value.Month,
            startTimePicker.Value.Day,
            startTimePicker.Value.Hour,
            startTimePicker.Value.Minute,
            startTimePicker.Value.Second,
            CodexUsageReader.BeijingOffset);
        UpdateStartNowButtonState();
        await RefreshUsageAsync();
    }

    private async Task OpenWeekPickerAsync()
    {
        if (CurrentMode() != RangeMode.Week)
        {
            return;
        }

        var range = GetSelectedRange();
        using var dialog = new Form
        {
            Text = "选择7天窗口",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(536, 254),
            BackColor = Color.FromArgb(246, 248, 251),
            Font = Font
        };

        var calendar = new MonthCalendar
        {
            MaxSelectionCount = 7,
            CalendarDimensions = new Size(2, 1),
            ShowToday = true,
            ShowTodayCircle = true,
            Location = new Point(14, 14),
            Margin = new Padding(0)
        };

        var suppressCalendarSelection = false;
        void SelectSevenDaysEndingAt(DateTime endDate)
        {
            suppressCalendarSelection = true;
            calendar.SelectionStart = endDate.Date.AddDays(-6);
            calendar.SelectionEnd = endDate.Date;
            suppressCalendarSelection = false;
        }

        calendar.DateSelected += (_, e) =>
        {
            if (suppressCalendarSelection)
            {
                return;
            }

            var endDate = e.End > e.Start ? e.End : e.Start;
            SelectSevenDaysEndingAt(endDate);
        };
        SelectSevenDaysEndingAt(range.End.Date);
        dialog.Controls.Add(calendar);

        var okButton = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Width = 84,
            Height = 32,
            Location = new Point(dialog.ClientSize.Width - 190, dialog.ClientSize.Height - 46),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = Color.FromArgb(21, 128, 106),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        okButton.FlatAppearance.BorderSize = 0;
        dialog.Controls.Add(okButton);

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Width = 84,
            Height = 32,
            Location = new Point(dialog.ClientSize.Width - 98, dialog.ClientSize.Height - 46),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = Color.FromArgb(229, 234, 242),
            ForeColor = Color.FromArgb(55, 65, 81),
            FlatStyle = FlatStyle.Flat
        };
        cancelButton.FlatAppearance.BorderSize = 0;
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var selectedEnd = calendar.SelectionEnd;
        var previous = datePicker.Value;
        suppressDateRefresh = true;
        datePicker.Value = new DateTime(
            selectedEnd.Year,
            selectedEnd.Month,
            selectedEnd.Day,
            previous.Hour,
            previous.Minute,
            previous.Second);
        suppressDateRefresh = false;
        UpdateRangeControls();
        await RefreshUsageAsync();
    }

    private void ClearCustomStart()
    {
        if (customStartLocal is null)
        {
            UpdateStartNowButtonState();
            return;
        }

        customStartLocal = null;
        UpdateStartNowButtonState();
    }

    private void UpdateStartNowButtonState()
    {
        var enabled = CurrentMode() == RangeMode.Day || customStartLocal is not null;
        startNowButton.Enabled = enabled;
        if (customStartLocal is not null)
        {
            startNowButton.BackColor = Color.FromArgb(21, 128, 106);
            startNowButton.ForeColor = Color.White;
            startNowButton.Text = "重设起点";
            suppressStartTimeRefresh = true;
            startTimePicker.Value = customStartLocal.Value.DateTime;
            startTimePicker.Visible = true;
            startTimePicker.Enabled = true;
            suppressStartTimeRefresh = false;
        }
        else
        {
            startNowButton.BackColor = Color.FromArgb(229, 234, 242);
            startNowButton.ForeColor = enabled ? Color.FromArgb(55, 65, 81) : Color.FromArgb(145, 153, 166);
            startNowButton.Text = "从当前算";
            suppressStartTimeRefresh = true;
            startTimePicker.Visible = false;
            startTimePicker.Enabled = false;
            suppressStartTimeRefresh = false;
        }

        UpdateRefreshDayButtonState();
    }

    private void UpdateRefreshDayButtonState()
    {
        var visible = CurrentMode() == RangeMode.Day;
        refreshDayButton.Visible = visible;
        refreshDayButton.Enabled = visible && !isRefreshing;
    }

    private async Task RefreshSelectedDayFromCacheAsync()
    {
        if (isRefreshing || CurrentMode() != RangeMode.Day)
        {
            return;
        }

        backgroundCacheCts?.Cancel();
        refreshDayButton.Enabled = false;

        var reader = CurrentReader();
        var selectedDay = DateOnly.FromDateTime(datePicker.Value.Date);
        SetStatus($"清除 {reader.Title} {selectedDay:yyyy-MM-dd} 缓存...");

        bool deleted;
        await usageQueryGate.WaitAsync();
        try
        {
            deleted = await Task.Run(() => reader.RefreshCachedDay(selectedDay));
        }
        finally
        {
            usageQueryGate.Release();
        }

        SetStatus(deleted
            ? $"已清除 {selectedDay:yyyy-MM-dd}，正在重新解析..."
            : $"{selectedDay:yyyy-MM-dd} 无缓存，正在解析...");
        await RefreshUsageAsync();
        _ = WarmCacheInBackgroundAsync();
    }

    private async Task ClearCacheAsync()
    {
        if (isRefreshing)
        {
            return;
        }

        backgroundCacheCts?.Cancel();
        var reader = CurrentReader();
        await usageQueryGate.WaitAsync();
        bool deleted;
        try
        {
            deleted = reader.ClearCache();
        }
        finally
        {
            usageQueryGate.Release();
        }

        SetStatus(deleted ? "缓存已清理，正在重新统计..." : "没有缓存，正在统计...");
        await RefreshUsageAsync();
        _ = WarmCacheInBackgroundAsync();
    }

    private async Task OpenPriceSettingsAsync()
    {
        using var form = new PriceSettingsForm();
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            currentQuotaEstimate = null;
            await RefreshUsageAsync();
        }
    }

    private Task OpenResetOpportunitySettingsAsync()
    {
        using var form = new ResetOpportunityForm();
        form.ShowDialog(this);
        ApplyResetOpportunitySummary();

        return Task.CompletedTask;
    }

    private async Task SyncResetOpportunitiesFromCodexAsync(bool showError)
    {
        try
        {
            var result = await ResetOpportunityStore.SyncFromCodexAsync();
            if (IsDisposed)
            {
                return;
            }

            if (result.Success)
            {
                ApplyResetOpportunitySummary();
            }
            else if (showError)
            {
                MessageBox.Show(this, result.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task OpenSubscriptionPlanSettingsAsync()
    {
        using var form = new SubscriptionPlanForm();
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            await RefreshUsageAsync();
        }
    }

    private async Task RefreshUsageAsync(bool cacheOnly = false)
    {
        if (isRefreshing)
        {
            return;
        }

        isRefreshing = true;
        refreshButton.Enabled = false;
        clearCacheButton.Enabled = false;
        refreshDayButton.Enabled = false;
        SetStatus(cacheOnly ? "正在读取缓存..." : "正在刷新...");

        try
        {
            var range = GetSelectedRange();
            var source = CurrentSource();
            var reader = UsageSourceReaders.For(source);
            var includeLiveToday = !cacheOnly && ShouldIncludeLiveToday(range);
            var cachedQuota = currentQuotaEstimate;
            if (!cacheOnly && reader.SupportsQuota)
            {
                _ = RefreshQuotaSummaryAsync();
            }

            if (isBackgroundCaching)
            {
                backgroundCacheCts?.Cancel();
                SetStatus("正在刷新...");
            }

            await usageQueryGate.WaitAsync();
            QueryResult result;
            try
            {
                result = await Task.Run(() =>
                {
                    if (range.IsCustomStart)
                    {
                        var transientRows = reader.ReadTransientDetailRows(range.Start, range.End);
                        var transientSummary = CreateSummaryFromRows(range, transientRows);
                        var transientQuota = ReadQuotaForRefresh(reader, includeLiveToday, cachedQuota);
                        var transientQuotaSnapshots = reader.SupportsQuota
                            ? ReadQuotaSnapshotsForRefresh(
                                range,
                                includeLiveToday,
                                transientQuota)
                            : Array.Empty<CodexQuotaSnapshot>();
                        return new QueryResult(
                            transientSummary,
                            transientRows,
                            EstimateCodingTime(transientRows),
                            transientQuota,
                            transientQuotaSnapshots);
                    }

                    if (range.Mode == RangeMode.Day)
                    {
                        var dayUsage = reader.ReadDay(range.Start, range.End, includeLiveToday);
                        var dayQuota = ReadQuotaForRefresh(reader, includeLiveToday, cachedQuota);
                        var dayQuotaSnapshots = reader.SupportsQuota
                            ? ReadQuotaSnapshotsForRefresh(range, includeLiveToday, dayQuota)
                            : Array.Empty<CodexQuotaSnapshot>();
                        return new QueryResult(
                            dayUsage.Summary,
                            dayUsage.Rows,
                            EstimateCodingTime(dayUsage.Rows),
                            dayQuota,
                            dayQuotaSnapshots);
                    }

                    var summary = includeLiveToday
                        ? reader.ReadRange(range.Start, range.End, includeLiveToday)
                        : reader.ReadCachedRange(range.Start, range.End);
                    var rows = BuildBreakdownRows(reader, range, summary);
                    var codingTime = EstimateCodingTimeForRange(reader, range, rows, includeLiveToday, cacheOnly: !includeLiveToday);
                    var quota = ReadQuotaForRefresh(reader, includeLiveToday, cachedQuota);
                    var quotaSnapshots = reader.SupportsQuota
                        ? ReadQuotaSnapshotsForRefresh(range, includeLiveToday, quota)
                        : Array.Empty<CodexQuotaSnapshot>();
                    return new QueryResult(summary, rows, codingTime, quota, quotaSnapshots);
                });
            }
            finally
            {
                usageQueryGate.Release();
            }

            ApplySummary(
                result.Summary,
                range,
                result.BreakdownRows,
                result.CodingTime,
                result.Quota,
                result.QuotaSnapshots,
                source);
            SetStatus(includeLiveToday
                ? $"已刷新 {DateTime.Now:HH:mm:ss}"
                : $"缓存命中 {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            SetStatus("读取失败");
            MessageBox.Show(this, ex.Message, "Codex Token 额度监控器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            refreshButton.Enabled = true;
            clearCacheButton.Enabled = true;
            isRefreshing = false;
            UpdateRefreshDayButtonState();
        }
    }

    private async Task RefreshQuotaSummaryAsync()
    {
        if (isQuotaRefreshing || IsDisposed || Disposing)
        {
            return;
        }

        var source = CurrentSource();
        if (source != UsageSource.Codex)
        {
            ApplyQuotaSummary(source, null);
            return;
        }

        isQuotaRefreshing = true;
        try
        {
            var quota = await Task.Run(CodexUsageReader.ReadQuotaEstimate);
            if (IsDisposed || Disposing || CurrentSource() != source)
            {
                return;
            }

            ApplyQuotaSummary(source, quota);
            if (CurrentMode() == RangeMode.Cycle)
            {
                UpdateCycleOptions(keepSelection: true);
            }
        }
        catch
        {
            // Quota refresh is best-effort and should not block token usage data.
        }
        finally
        {
            isQuotaRefreshing = false;
        }
    }

    private async Task WarmCacheInBackgroundAsync()
    {
        if (isBackgroundCaching || IsDisposed || Disposing)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var lastHistoricalDay = StartOfDay(now).AddDays(-1);
        if (lastHistoricalDay < BackgroundCacheStart)
        {
            return;
        }

        backgroundCacheCts?.Cancel();
        var cts = new CancellationTokenSource();
        backgroundCacheCts = cts;
        var token = cts.Token;
        isBackgroundCaching = true;
        backgroundCacheTimer.Enabled = false;

        try
        {
            var sources = GetBackgroundSourceOrder();
            var pending = sources.ToDictionary(
                source => source,
                source => UsageSourceReaders.For(source).GetIncompleteHistoricalDays(BackgroundCacheStart, lastHistoricalDay)
                    .Select(day => DateOnly.FromDateTime(day.DateTime))
                    .ToHashSet());
            var pendingQuota = CodexUsageReader.GetIncompleteQuotaSnapshotDays(BackgroundCacheStart, lastHistoricalDay)
                .Select(day => DateOnly.FromDateTime(day.DateTime))
                .ToHashSet();
            var total = pending.Values.Sum(days => days.Count) + pendingQuota.Count;
            if (total == 0)
            {
                SetStatus($"缓存完成 {DateTime.Now:HH:mm:ss}");
                return;
            }

            var completed = 0;
            SetStatus($"缓存 0/{total}");
            for (var day = lastHistoricalDay; day >= BackgroundCacheStart; day = day.AddDays(-1))
            {
                var date = DateOnly.FromDateTime(day.DateTime);
                if (pendingQuota.Contains(date))
                {
                    token.ThrowIfCancellationRequested();
                    while (isRefreshing)
                    {
                        await Task.Delay(250, token);
                    }

                    SetStatus($"缓存 {completed + 1}/{total} 额度 {day:MM-dd}");
                    // Background warming only touches historical data. Let it avoid the foreground gate so
                    // switching to today can use cached/incremental reads immediately.
                    await Task.Run(() => CodexUsageReader.WarmQuotaSnapshotDay(day), token);

                    completed++;
                    await Task.Delay(20, token);
                }

                foreach (var source in sources)
                {
                    if (!pending[source].Contains(date))
                    {
                        continue;
                    }

                    var reader = UsageSourceReaders.For(source);
                    token.ThrowIfCancellationRequested();
                    while (isRefreshing)
                    {
                        await Task.Delay(250, token);
                    }

                    SetStatus($"缓存 {completed + 1}/{total} {reader.Title} {day:MM-dd}");
                    await Task.Run(() => reader.WarmHistoricalDay(day), token);

                    completed++;
                    await Task.Delay(20, token);
                }
            }

            SetStatus($"缓存完成 {DateTime.Now:HH:mm:ss}");
        }
        catch (OperationCanceledException)
        {
            // A foreground action can cancel background warming; the next idle pass resumes from cache.
        }
        catch (Exception ex)
        {
            SetStatus($"后台缓存暂停：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(backgroundCacheCts, cts))
            {
                backgroundCacheCts = null;
            }

            isBackgroundCaching = false;
            if (!IsDisposed && !Disposing)
            {
                backgroundCacheTimer.Enabled = true;
            }
        }
    }

    private UsageSource[] GetBackgroundSourceOrder()
    {
        var current = CurrentSource();
        var sources = new[] { UsageSource.Codex, UsageSource.ClaudeCode, UsageSource.ZCode };
        return sources
            .Where(source => source == current)
            .Concat(sources.Where(source => source != current))
            .ToArray();
    }

    private static TokenUsageSummary CreateSummaryFromRows(
        SelectedRange range,
        IReadOnlyList<TokenUsageBucket> rows)
    {
        var summary = new TokenUsageSummary
        {
            StartLocal = range.Start,
            EndLocal = range.End
        };

        foreach (var row in rows)
        {
            summary.Add(
                row.StartLocal,
                row.InputTokens,
                row.CachedInputTokens,
                row.OutputTokens,
                row.ReasoningOutputTokens,
                row.TotalTokens);
        }

        return summary;
    }

    private void ApplySummary(
        TokenUsageSummary summary,
        SelectedRange range,
        IReadOnlyList<TokenUsageBucket> breakdownRows,
        TimeSpan codingTime,
        CodexQuotaEstimate? quota,
        IReadOnlyList<CodexQuotaSnapshot> quotaSnapshots,
        UsageSource source)
    {
        var reader = UsageSourceReaders.For(source);
        Text = $"{reader.Title} Token 额度监控器 - {range.Title}";
        var displayPresets = PriceSettingsStore.DisplayPresetsForSource(reader.Source, count: 0).ToList();
        priceLabel.Text = "";
        totalValue.Text = FormatTokenMillions(summary.TotalTokens);
        periodValue.Text = $"{summary.StartLocal:yyyy-MM-dd HH:mm} - {summary.EndLocal:yyyy-MM-dd HH:mm:ss}  GMT+8";
        inputValue.Text = FormatTokenMillions(summary.InputTokens);
        cachedValue.Text = FormatTokenMillions(summary.CachedInputTokens);
        uncachedValue.Text = FormatTokenMillions(summary.UncachedInputTokens);
        outputValue.Text = FormatTokenAdaptive(summary.OutputTokens);
        reasoningValue.Text = FormatTokenAdaptive(summary.ReasoningOutputTokens);
        cacheRatioValue.Text = summary.InputTokens > 0 ? $"{summary.CacheRatioPercent:N2}%" : "0.00%";
        eventsValue.Text = summary.Events.ToString("N0");
        lastEventValue.Text = FormatDuration(codingTime);

        ApplyCostCards(displayPresets, summary);
        var tablePresets = displayPresets
            .Take(GetVisibleCostCardCount(displayPresets.Count))
            .ToList();
        currentQuotaSnapshots = source == UsageSource.Codex
            ? quotaSnapshots
            : Array.Empty<CodexQuotaSnapshot>();
        ApplyQuotaSummary(source, quota);
        if (source == UsageSource.Codex && range.Mode == RangeMode.Cycle)
        {
            UpdateCycleOptions(keepSelection: true);
        }

        var scrollAnchor = CaptureBreakdownScrollAnchor();
        breakdownList.BeginUpdate();
        var showTimeline = breakdownRows.Count > 0;
        try
        {
            breakdownList.Items.Clear();
            if (timelineRowStyle is not null)
            {
                timelineRowStyle.Height = showTimeline ? 300 : 0;
            }

            timelineChart.Visible = showTimeline;
            if (showTimeline)
            {
                var chartInterval = GetTimelineInterval(range.Mode);
            var chartRows = GetTimelineRows(reader, range, breakdownRows);
                timelineChart.SetData(range.Start, range.End, chartRows, chartInterval);
            }
            else
            {
                timelineChart.ClearData();
            }

            var eventBreakdown = UsesEventBreakdown(range, breakdownRows);
            RebuildBreakdownColumns(range, eventBreakdown, tablePresets);
            foreach (var bucket in breakdownRows)
            {
                var rowIsEvent = IsEventBucket(range, bucket);
                var item = new ListViewItem(FormatBucketLabel(range, bucket.StartLocal, rowIsEvent));
                item.SubItems.Add(FormatBreakdownToken(bucket.TotalTokens, rowIsEvent));
                item.SubItems.Add(FormatBreakdownToken(bucket.InputTokens, rowIsEvent));
                item.SubItems.Add(FormatBreakdownToken(bucket.CachedInputTokens, rowIsEvent));
                item.SubItems.Add(FormatBreakdownToken(bucket.UncachedInputTokens, rowIsEvent));
                item.SubItems.Add(FormatTokenAdaptive(bucket.OutputTokens));
                foreach (var preset in tablePresets)
                {
                    AddPresetCost(item, bucket, preset);
                }

                item.SubItems.Add(source == UsageSource.Codex
                    ? FormatQuotaSnapshotForBucket(range, bucket, quotaSnapshots, rowIsEvent)
                    : "-");
                breakdownList.Items.Add(item);
            }

            RestoreBreakdownScrollAnchor(scrollAnchor);
        }
        finally
        {
            breakdownList.EndUpdate();
        }
    }

    private static TimeSpan? GetTimelineInterval(RangeMode mode)
    {
        return mode switch
        {
            RangeMode.Day => DayTimelineInterval,
            RangeMode.Week or RangeMode.Cycle => MultiDayBreakdownInterval,
            RangeMode.Month => MonthTimelineInterval,
            _ => null
        };
    }

    private static IReadOnlyList<TokenUsageBucket> GetTimelineRows(
        IUsageSourceReader reader,
        SelectedRange range,
        IReadOnlyList<TokenUsageBucket> breakdownRows)
    {
        if (range.Mode != RangeMode.Month)
        {
            return breakdownRows;
        }

        var detailRows = reader.ReadCachedDetailRows(range.Start, range.End);
        return detailRows.Count > 0 ? detailRows : breakdownRows;
    }

    private void ApplyCostCards(IReadOnlyList<PricePreset> presets, TokenUsageSummary summary)
    {
        costCardsFlow.SuspendLayout();
        costCardsFlow.Controls.Clear();
        foreach (var preset in presets)
        {
            costCardsFlow.Controls.Add(CreateCostCard(preset, summary));
        }

        costCardsFlow.ResumeLayout();
    }

    private static Control CreateCostCard(PricePreset preset, TokenUsageSummary summary)
    {
        var profile = preset.ToProfile();
        var panel = new TableLayoutPanel
        {
            Width = CostCardWidth,
            Height = 118,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14, 6, 10, 4),
            Margin = new Padding(0, 0, CostCardRightMargin, 0),
            BackColor = Color.Transparent
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        panel.Controls.Add(new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 24,
            Text = string.IsNullOrWhiteSpace(preset.Provider) ? preset.Model : preset.Provider,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(92, 105, 122),
            AutoEllipsis = true,
            Margin = new Padding(0)
        }, 0, 0);

        panel.Controls.Add(new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 30,
            Text = preset.Model,
            ForeColor = Color.FromArgb(101, 114, 130),
            AutoEllipsis = true,
            Margin = new Padding(0, 0, 0, 4)
        }, 0, 1);

        panel.Controls.Add(new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = FormatCost(summary.EstimateCost(profile), profile),
            Font = new Font("Segoe UI", 15.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 41, 55),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        }, 0, 2);

        return panel;
    }

    private int GetVisibleCostCardCount(int presetCount)
    {
        if (presetCount <= 0)
        {
            return 0;
        }

        var availableWidth = costCardsFlow.ClientSize.Width > 0
            ? costCardsFlow.ClientSize.Width
            : costCardsFlow.Width;
        if (availableWidth <= 0)
        {
            return Math.Min(3, presetCount);
        }

        var visible = (availableWidth + CostCardRightMargin) / (CostCardWidth + CostCardRightMargin);
        return Math.Clamp(visible, 1, presetCount);
    }

    private static void ApplyCostDisplay(
        Label titleLabel,
        Label subtitleLabel,
        Label valueLabel,
        PricePreset? preset,
        TokenUsageSummary summary)
    {
        if (preset is null)
        {
            titleLabel.Text = "-";
            subtitleLabel.Text = "";
            valueLabel.Text = "-";
            return;
        }

        var profile = preset.ToProfile();
        titleLabel.Text = string.IsNullOrWhiteSpace(preset.Provider) ? preset.Model : preset.Provider;
        subtitleLabel.Text = preset.Model;
        valueLabel.Text = FormatCost(summary.EstimateCost(profile), profile);
    }

    private static void AddPresetCost(ListViewItem item, TokenUsageBucket bucket, PricePreset? preset)
    {
        if (preset is null)
        {
            item.SubItems.Add("-");
            return;
        }

        var profile = preset.ToProfile();
        item.SubItems.Add(FormatCost(bucket.EstimateCost(profile), profile));
    }

    private static string FormatPresetColumnTitle(PricePreset? preset, string fallback)
    {
        if (preset is null)
        {
            return fallback;
        }

        if (string.IsNullOrWhiteSpace(preset.Provider))
        {
            return ShortenColumnTitle(preset.Model);
        }

        return ShortenColumnTitle($"{preset.Provider} {preset.Model}");
    }

    private static string ShortenColumnTitle(string text)
    {
        return text.Length <= 18 ? text : $"{text[..16]}...";
    }

    private void ApplyQuotaSummary(UsageSource source, CodexQuotaEstimate? quota)
    {
        var show = source == UsageSource.Codex;
        if (!show)
        {
            quotaPanel.Visible = false;
            quotaPanel.Height = 0;
            quotaPanel.Margin = new Padding(0);
            quotaCalculateButton.Enabled = false;
            return;
        }

        var effectiveQuota = FreshQuotaOrNull(quota) ?? FreshQuotaOrNull(currentQuotaEstimate);
        currentQuotaEstimate = effectiveQuota;
        quotaPanel.Visible = show;
        quotaPanel.Height = 104;
        quotaPanel.Margin = new Padding(0, 0, 0, 12);
        quotaCalculateButton.Enabled = effectiveQuota is not null;
        ApplyCurrentPlanSummary();
        ApplyResetOpportunitySummary();
        ApplyResetPaceSummary(effectiveQuota?.Week);
        if (effectiveQuota is null)
        {
            quota5hValue.Text = "5h";
            quota5hDetail.Text = "";
            quotaWeekValue.Text = "周";
            quotaWeekDetail.Text = "";
            resetPaceValue.Text = "重置评估";
            resetPaceDetail.Text = "";
            quotaLimitValue.Text = "";
            return;
        }

        ApplyQuotaWindow(quota5hValue, quota5hDetail, effectiveQuota.FiveHour, QuotaWindowDisplayMode.FiveHour);
        ApplyQuotaWindow(quotaWeekValue, quotaWeekDetail, effectiveQuota.Week, QuotaWindowDisplayMode.Week);
        quotaLimitValue.Text = "";
    }

    private void ApplyCurrentPlanSummary()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var active = SubscriptionPlanStore.Load()
            .Where(item => item.StartLocal <= now && item.EndLocal > now)
            .OrderByDescending(item => item.StartLocal)
            .ToList();
        if (active.Count == 0)
        {
            planSpendValue.Text = "-";
            planSpendDetail.Text = "";
            return;
        }

        var amount = active.Sum(item => item.AmountCny);
        planSpendValue.Text = string.Join(" / ", active.Select(item => item.PlanName).Distinct());
        planSpendDetail.Text = FormatCny(amount);
    }

    private void ApplyResetOpportunitySummary()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var summary = ResetOpportunityStore.Summarize(now);
        resetOpportunityValue.Text = "重置过期";
        resetOpportunityDetail.Text = summary.AvailableRecords.Count == 0
            ? "无可用"
            : string.Join(" / ", summary.AvailableRecords
                .Take(4)
                .Select(item => item.ExpiresLocal.ToString("MM-dd")));
    }

    private void ApplyResetPaceSummary(CodexQuotaWindowEstimate? week)
    {
        resetPaceValue.Text = "重置评估";
        resetPaceDetail.Text = QuotaPaceAnalyzer.FormatShort(week);
    }

    private static void ApplyQuotaWindow(
        Label valueLabel,
        Label detailLabel,
        CodexQuotaWindowEstimate? window,
        QuotaWindowDisplayMode mode)
    {
        if (window is null)
        {
            valueLabel.Text = mode == QuotaWindowDisplayMode.FiveHour ? "5h" : "周";
            detailLabel.Text = "";
            return;
        }

        var remainingPercent = Math.Max(0m, 100m - window.UsedPercent);
        var resetAt = window.ResetAtLocal ?? window.WindowEndLocal;
        valueLabel.Text = mode == QuotaWindowDisplayMode.FiveHour
            ? $"5h {resetAt:HH:mm}"
            : $"周 {resetAt:MM-dd HH:mm}";
        detailLabel.Text = mode == QuotaWindowDisplayMode.FiveHour
            ? $"{remainingPercent:N0}% ≈ {FormatMoney(window.UsedGptCost, PriceProfiles.Gpt55StandardLong)}"
            : $"{remainingPercent:N0}% {FormatQuotaLimit(window)}";
    }

    private void UpdateQuotaLimitCalculation()
    {
        if (currentQuotaEstimate is null)
        {
            quotaLimitValue.Text = "";
            return;
        }

        quotaLimitValue.Text = "";
        var form = new QuotaEstimateForm(currentQuotaEstimate);
        form.Show(this);
    }

    private static string FormatQuotaLimit(CodexQuotaWindowEstimate? window)
    {
        return window?.EstimatedGptLimit is null
            ? "-"
            : $"≈{FormatMoney(window.EstimatedGptLimit.Value, PriceProfiles.Gpt55StandardLong)}";
    }

    private static string FormatQuotaRemaining(DateTimeOffset resetAtLocal)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var remaining = resetAtLocal - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "已到期";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{Math.Ceiling(remaining.TotalDays):N0}天";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalHours))}小时";
    }

    private static string FormatQuotaWindowReport(string label, CodexQuotaWindowEstimate? window)
    {
        if (window is null)
        {
            return $"{label}: 暂无快照";
        }

        var limit = window.EstimatedGptLimit is null
            ? "-"
            : FormatMoney(window.EstimatedGptLimit.Value, PriceProfiles.Gpt55StandardLong);
        return $"{label}: {window.WindowStartLocal:MM-dd HH:mm} - {window.WindowEndLocal:MM-dd HH:mm}, 已用 {window.UsedPercent:N0}%, {FormatTokenMillions(window.Usage.TotalTokens)}, {FormatMoney(window.UsedGptCost, PriceProfiles.Gpt55StandardLong)}, 100%≈{limit}";
    }

    private static QuotaDeltaEstimate? BuildQuotaDeltaEstimate(
        string label,
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        Func<CodexQuotaSnapshot, decimal?> usedPercentSelector,
        Func<CodexQuotaSnapshot, DateTimeOffset?> resetAtSelector)
    {
        var ordered = snapshots
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
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

    private static string FormatQuotaDeltaReport(QuotaDeltaEstimate? estimate)
    {
        if (estimate is null)
        {
            return "- 暂无百分比变化段";
        }

        var reliability = estimate.IsStable ? "稳定段" : "样本太窄，仅参考";
        return $"{estimate.Label}: {reliability}, 剩余 {estimate.PreviousRemainingPercent:N0}% -> {estimate.CurrentRemainingPercent:N0}% ({estimate.UsedDeltaPercent:N0}%), {estimate.StartLocal:HH:mm:ss}-{estimate.EndLocal:HH:mm:ss}, {FormatTokenMillions(estimate.Usage.TotalTokens)}, {FormatMoney(estimate.UsedCost, PriceProfiles.Gpt55StandardLong)}, 100%≈{FormatMoney(estimate.EstimatedLimit, PriceProfiles.Gpt55StandardLong)}";
    }

    private static IReadOnlyList<CodexQuotaSnapshot> ReadQuotaSnapshotsForWindow(CodexQuotaWindowEstimate? window)
    {
        return window is null
            ? Array.Empty<CodexQuotaSnapshot>()
            : CodexUsageReader.ReadQuotaSnapshots(window.WindowStartLocal, window.WindowEndLocal.AddMinutes(1));
    }

    private static bool IsSameQuotaReset(DateTimeOffset? first, DateTimeOffset? second)
    {
        return first is null || second is null || first.Value == second.Value;
    }

    private void RebuildBreakdownColumns(
        SelectedRange range,
        bool eventBreakdown,
        IReadOnlyList<PricePreset> tablePresets)
    {
        var expectedTitles = new List<string>
        {
            eventBreakdown ? "时间" : "日期",
            "Total",
            "Input",
            "Cached",
            "Uncached",
            "Output"
        };
        for (var i = 0; i < tablePresets.Count; i++)
        {
            expectedTitles.Add(FormatPresetColumnTitle(tablePresets[i], $"价格{i + 1}"));
        }

        expectedTitles.Add("额度(5h/7d)");

        if (!BreakdownColumnsMatch(expectedTitles))
        {
            breakdownList.Columns.Clear();
            breakdownList.Columns.Add(expectedTitles[0], 104, HorizontalAlignment.Left);
            breakdownList.Columns.Add("Total", 96, HorizontalAlignment.Right);
            breakdownList.Columns.Add("Input", 96, HorizontalAlignment.Right);
            breakdownList.Columns.Add("Cached", 96, HorizontalAlignment.Right);
            breakdownList.Columns.Add("Uncached", 104, HorizontalAlignment.Right);
            breakdownList.Columns.Add("Output", 88, HorizontalAlignment.Right);
            for (var i = 0; i < tablePresets.Count; i++)
            {
                breakdownList.Columns.Add(expectedTitles[6 + i], 108, HorizontalAlignment.Right);
            }

            breakdownList.Columns.Add("额度(5h/7d)", 126, HorizontalAlignment.Right);
        }

        ApplyBreakdownColumnWidths(range, eventBreakdown, tablePresets.Count);
    }

    private bool BreakdownColumnsMatch(IReadOnlyList<string> expectedTitles)
    {
        if (breakdownList.Columns.Count != expectedTitles.Count)
        {
            return false;
        }

        for (var i = 0; i < expectedTitles.Count; i++)
        {
            if (!string.Equals(breakdownList.Columns[i].Text, expectedTitles[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private ListScrollAnchor? CaptureBreakdownScrollAnchor()
    {
        if (breakdownList.Items.Count == 0)
        {
            return null;
        }

        try
        {
            var topItem = breakdownList.TopItem;
            return topItem is null
                ? null
                : new ListScrollAnchor(topItem.Index, topItem.Text);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private void RestoreBreakdownScrollAnchor(ListScrollAnchor? anchor)
    {
        if (anchor is null || breakdownList.Items.Count == 0)
        {
            return;
        }

        var index = FindBreakdownAnchorIndex(anchor);
        if (index < 0)
        {
            return;
        }

        try
        {
            breakdownList.TopItem = breakdownList.Items[index];
        }
        catch (InvalidOperationException)
        {
            breakdownList.Items[index].EnsureVisible();
        }
        catch (ArgumentOutOfRangeException)
        {
            breakdownList.Items[^1].EnsureVisible();
        }
    }

    private int FindBreakdownAnchorIndex(ListScrollAnchor anchor)
    {
        var count = breakdownList.Items.Count;
        if (count == 0)
        {
            return -1;
        }

        var clampedIndex = Math.Clamp(anchor.Index, 0, count - 1);
        if (breakdownList.Items[clampedIndex].Text == anchor.Text)
        {
            return clampedIndex;
        }

        for (var distance = 1; distance < count; distance++)
        {
            var before = clampedIndex - distance;
            if (before >= 0 && breakdownList.Items[before].Text == anchor.Text)
            {
                return before;
            }

            var after = clampedIndex + distance;
            if (after < count && breakdownList.Items[after].Text == anchor.Text)
            {
                return after;
            }
        }

        return clampedIndex;
    }

    private void ApplyBreakdownColumnWidths(SelectedRange range, bool eventBreakdown, int priceColumnCount)
    {
        breakdownList.Columns[0].Width = eventBreakdown && range.Mode != RangeMode.Day && !range.IsCustomStart
            ? 134
            : range.IsCustomStart
            ? 150
            : range.Mode == RangeMode.Day
                ? 86
                : 104;
        breakdownList.Columns[1].Width = 96;
        breakdownList.Columns[2].Width = 96;
        breakdownList.Columns[3].Width = 96;
        breakdownList.Columns[4].Width = 104;
        breakdownList.Columns[5].Width = 88;
        for (var i = 0; i < priceColumnCount; i++)
        {
            breakdownList.Columns[6 + i].Width = 108;
        }

        breakdownList.Columns[6 + priceColumnCount].Width = 126;
    }

    private static string FormatQuotaSnapshotForBucket(
        SelectedRange range,
        TokenUsageBucket bucket,
        IReadOnlyList<CodexQuotaSnapshot> quotaSnapshots,
        bool eventBreakdown)
    {
        if (quotaSnapshots.Count == 0)
        {
            return "-";
        }

        var bucketEnd = eventBreakdown
            ? range.Mode == RangeMode.Day || range.IsCustomStart
                ? bucket.StartLocal.AddSeconds(2)
                : bucket.StartLocal.Add(MultiDayBreakdownInterval)
            : bucket.StartLocal.AddDays(1);
        var snapshot = quotaSnapshots
            .Where(item => item.SnapshotLocal >= bucket.StartLocal && item.SnapshotLocal < bucketEnd)
            .LastOrDefault();
        snapshot ??= quotaSnapshots
            .Where(item => item.SnapshotLocal <= bucket.StartLocal)
            .LastOrDefault();
        if (snapshot is null)
        {
            return "-";
        }

        return $"{FormatQuotaRemaining(snapshot.FiveHourUsedPercent)} / {FormatQuotaRemaining(snapshot.WeekUsedPercent)}";
    }

    private static string FormatQuotaRemaining(decimal? usedPercent)
    {
        if (usedPercent is null)
        {
            return "-";
        }

        return $"{Math.Max(0m, 100m - usedPercent.Value):N0}%";
    }

    private SelectedRange GetSelectedRange()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var selectedDay = new DateTimeOffset(
            datePicker.Value.Year,
            datePicker.Value.Month,
            datePicker.Value.Day,
            0,
            0,
            0,
            CodexUsageReader.BeijingOffset);
        var selectedDateTime = new DateTimeOffset(
            datePicker.Value.Year,
            datePicker.Value.Month,
            datePicker.Value.Day,
            datePicker.Value.Hour,
            datePicker.Value.Minute,
            datePicker.Value.Second,
            CodexUsageReader.BeijingOffset);

        if (customStartLocal is not null)
        {
            var startFromNow = customStartLocal.Value;
            var customEnd = now < startFromNow ? startFromNow : now;
            return new SelectedRange(
                startFromNow,
                customEnd,
                $"当前起算 {startFromNow:MM-dd HH:mm:ss}",
                "事件明细（起点后）",
                RangeMode.Day,
                IsCustomStart: true);
        }

        if (CurrentMode() == RangeMode.Cycle)
        {
            var cycle = SelectedCycle();
            if (cycle is null)
            {
                return new SelectedRange(
                    now,
                    now,
                    "额度周期",
                    "按天明细（额度周期）",
                    RangeMode.Cycle);
            }

            var cycleEnd = cycle.PeriodEnd > now ? now : cycle.PeriodEnd;
            if (cycleEnd < cycle.PeriodStart)
            {
                cycleEnd = cycle.PeriodStart;
            }

            return new SelectedRange(
                cycle.PeriodStart,
                cycleEnd,
                cycle.IsCurrent ? "当前周期" : $"周期 {cycle.PeriodStart:MM-dd HH:mm}",
                "按天明细（额度周期）",
                RangeMode.Cycle);
        }

        DateTimeOffset start;
        DateTimeOffset periodEnd;
        string title;
        string breakdownTitleText;

        switch (CurrentMode())
        {
            case RangeMode.Week:
                periodEnd = selectedDateTime > now ? now : selectedDateTime;
                start = periodEnd.AddDays(-7);
                title = periodEnd >= now.AddSeconds(-2) ? "近一周" : $"7天至 {periodEnd:MM-dd HH:mm}";
                breakdownTitleText = "按天明细（7天窗口）";
                break;
            case RangeMode.Month:
                start = new DateTimeOffset(selectedDay.Year, selectedDay.Month, 1, 0, 0, 0, CodexUsageReader.BeijingOffset);
                periodEnd = start.AddMonths(1);
                title = start.Year == now.Year && start.Month == now.Month ? "本月" : start.ToString("yyyy-MM");
                breakdownTitleText = "按天明细（本月）";
                break;
            default:
                start = selectedDay;
                periodEnd = start.AddDays(1);
                title = start.Date == now.Date ? "今天" : start.ToString("yyyy-MM-dd");
                breakdownTitleText = "事件明细（当天）";
                break;
        }

        var end = periodEnd > now ? now : periodEnd;
        if (end < start)
        {
            end = start;
        }

        return new SelectedRange(start, end, title, breakdownTitleText, CurrentMode());
    }

    private static bool ShouldIncludeLiveToday(SelectedRange range)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
        return range.IsCustomStart ||
               range.Mode == RangeMode.Day && range.Start == todayStart ||
               range.Mode == RangeMode.Cycle && range.End >= now.AddSeconds(-2);
    }

    private void UpdateRangeControls()
    {
        var mode = CurrentMode();
        if (mode == RangeMode.Cycle)
        {
            UpdateCycleOptions(keepSelection: true);
        }

        currentButton.Text = mode switch
        {
            RangeMode.Week => "近一周",
            RangeMode.Month => "本月",
            RangeMode.Cycle => "当前周期",
            _ => "今天"
        };
        datePicker.CustomFormat = mode switch
        {
            RangeMode.Week => "yyyy-MM-dd HH:mm",
            RangeMode.Month => "yyyy-MM",
            _ => "yyyy-MM-dd"
        };
        datePicker.Width = mode == RangeMode.Week ? 170 : 140;
        datePicker.Visible = mode != RangeMode.Cycle;
        cycleBox.Visible = mode == RangeMode.Cycle;

        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var range = GetSelectedRange();
        if (mode == RangeMode.Cycle)
        {
            previousButton.Enabled = cycleBox.SelectedIndex >= 0 && cycleBox.SelectedIndex < cycleBox.Items.Count - 1;
            nextButton.Enabled = cycleBox.SelectedIndex > 0;
            currentButton.Enabled = cycleBox.SelectedIndex != 0 && cycleBox.Items.Count > 0;
            UpdateWeekPickerState();
            UpdateStartNowButtonState();
            return;
        }

        var currentStart = mode switch
        {
            RangeMode.Week => now.AddDays(-7),
            RangeMode.Month => new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, CodexUsageReader.BeijingOffset),
            _ => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
        };
        nextButton.Enabled = mode == RangeMode.Week
            ? range.End < now.AddSeconds(-2)
            : range.Start < currentStart;
        previousButton.Enabled = true;
        currentButton.Enabled = true;
        UpdateWeekPickerState();
        UpdateStartNowButtonState();
    }

    private void UpdateCycleOptions(bool keepSelection)
    {
        if (CurrentSource() != UsageSource.Codex)
        {
            quotaCycles = Array.Empty<CodexQuotaCycle>();
            suppressCycleRefresh = true;
            cycleBox.Items.Clear();
            suppressCycleRefresh = false;
            return;
        }

        var selected = keepSelection ? SelectedCycle() : null;
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        quotaCycles = CodexQuotaCycleReader.ReadWeeklyCycles(currentQuotaEstimate, now);

        suppressCycleRefresh = true;
        cycleBox.BeginUpdate();
        cycleBox.Items.Clear();
        foreach (var cycle in quotaCycles)
        {
            cycleBox.Items.Add(cycle);
        }

        if (cycleBox.Items.Count > 0)
        {
            var selectedIndex = selected is null
                ? 0
                : quotaCycles.ToList().FindIndex(item => SameCycle(item, selected));
            cycleBox.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        }

        cycleBox.EndUpdate();
        suppressCycleRefresh = false;
    }

    private CodexQuotaCycle? SelectedCycle()
    {
        return cycleBox.SelectedItem as CodexQuotaCycle;
    }

    private static bool SameCycle(CodexQuotaCycle first, CodexQuotaCycle second)
    {
        return first.PeriodStart == second.PeriodStart &&
               first.PeriodEnd == second.PeriodEnd &&
               first.ResetAt == second.ResetAt;
    }

    private void UpdateWeekPickerState()
    {
        var show = CurrentMode() == RangeMode.Week && customStartLocal is null;
        weekPickerButton.Visible = show;
        weekPickerButton.Enabled = show;
    }

    private bool IsTodayDaySelection()
    {
        if (CurrentMode() != RangeMode.Day)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        return datePicker.Value.Date == now.Date;
    }

    private RangeMode CurrentMode()
    {
        return rangeModeBox.SelectedIndex switch
        {
            1 => RangeMode.Week,
            2 => RangeMode.Month,
            3 => RangeMode.Cycle,
            _ => RangeMode.Day
        };
    }

    private UsageSource CurrentSource()
    {
        return sourceTabs.SelectedIndex switch
        {
            1 => UsageSource.ClaudeCode,
            2 => UsageSource.ZCode,
            _ => UsageSource.Codex
        };
    }

    private IUsageSourceReader CurrentReader()
    {
        return UsageSourceReaders.For(CurrentSource());
    }

    private static CodexQuotaEstimate? ReadQuotaForRefresh(
        IUsageSourceReader reader,
        bool includeLiveToday,
        CodexQuotaEstimate? cachedQuota)
    {
        if (!reader.SupportsQuota)
        {
            return null;
        }

        _ = includeLiveToday;
        return FreshQuotaOrNull(cachedQuota) ?? CodexUsageReader.ReadCachedQuotaEstimate();
    }

    private static CodexQuotaEstimate? FreshQuotaOrNull(CodexQuotaEstimate? quota)
    {
        if (quota is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        return QuotaFreshness.IsFresh(quota.SnapshotLocal, now) ? quota : null;
    }

    private static IReadOnlyList<CodexQuotaSnapshot> ReadQuotaSnapshotsForRefresh(
        SelectedRange range,
        bool includeLiveToday,
        CodexQuotaEstimate? quota)
    {
        var snapshots = CodexUsageReader.ReadCachedQuotaSnapshots(range.Start, range.End)
            .Where(CodexUsageReader.IsGeneralCodexQuotaSnapshot)
            .ToList();
        if (!includeLiveToday || quota is null ||
            quota.SnapshotLocal < range.Start ||
            quota.SnapshotLocal >= range.End)
        {
            return snapshots;
        }

        return snapshots
            .Append(new CodexQuotaSnapshot(
                quota.SnapshotLocal,
                quota.LimitId,
                quota.LimitName,
                quota.FiveHour?.UsedPercent,
                quota.FiveHour?.ResetAtLocal,
                quota.Week?.UsedPercent,
                quota.Week?.ResetAtLocal))
            .GroupBy(item => item.SnapshotLocal)
            .Select(group => group.OrderByDescending(item => item.WeekUsedPercent ?? -1m).First())
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset value)
    {
        var daysSinceMonday = ((int)value.DayOfWeek + 6) % 7;
        var date = value.Date.AddDays(-daysSinceMonday);
        return new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
    }

    private static IReadOnlyList<TokenUsageBucket> BuildBreakdownRows(
        IUsageSourceReader reader,
        SelectedRange range,
        TokenUsageSummary summary)
    {
        if (range.Mode == RangeMode.Day)
        {
            return reader.ReadCachedDetailRows(range.Start, range.End);
        }

        if (range.Mode is RangeMode.Week or RangeMode.Cycle)
        {
            return BuildIntervalBreakdownRows(reader, range, summary, MultiDayBreakdownInterval);
        }

        return summary.DailyBuckets;
    }

    private static IReadOnlyList<TokenUsageBucket> BuildIntervalBreakdownRows(
        IUsageSourceReader reader,
        SelectedRange range,
        TokenUsageSummary summary,
        TimeSpan interval)
    {
        var detailRows = reader.ReadCachedDetailRows(range.Start, range.End);
        if (detailRows.Count == 0)
        {
            return summary.DailyBuckets;
        }

        var buckets = new Dictionary<DateTimeOffset, TokenUsageBucket>();
        var detailDates = new HashSet<DateOnly>();
        foreach (var row in detailRows)
        {
            var hour = StartOfInterval(row.StartLocal, interval);
            detailDates.Add(DateOnly.FromDateTime(row.StartLocal.DateTime));
            if (!buckets.TryGetValue(hour, out var bucket))
            {
                bucket = new TokenUsageBucket { StartLocal = hour };
                buckets[hour] = bucket;
            }

            AddBucketValues(bucket, row);
        }

        var rows = buckets.Values
            .Where(bucket => bucket.Events > 0)
            .ToList();
        foreach (var dailyBucket in summary.DailyBuckets)
        {
            var date = DateOnly.FromDateTime(dailyBucket.StartLocal.DateTime);
            if (!detailDates.Contains(date))
            {
                rows.Add(dailyBucket);
            }
        }

        return rows
            .OrderBy(bucket => bucket.StartLocal)
            .ToList();
    }

    private static DateTimeOffset StartOfInterval(DateTimeOffset value, TimeSpan interval)
    {
        var dayStart = StartOfDay(value);
        var ticks = (value - dayStart).Ticks / interval.Ticks * interval.Ticks;
        return dayStart.AddTicks(ticks);
    }

    private static TimeSpan EstimateCodingTimeForRange(
        IUsageSourceReader reader,
        SelectedRange range,
        IReadOnlyList<TokenUsageBucket> breakdownRows,
        bool includeLiveToday,
        bool cacheOnly = false)
    {
        if (range.Mode == RangeMode.Day || range.IsCustomStart)
        {
            return EstimateCodingTime(breakdownRows);
        }

        var cachedRows = reader.ReadCachedDetailRows(range.Start, range.End);
        if (cachedRows.Count > 0)
        {
            return EstimateCodingTime(cachedRows);
        }

        if (cacheOnly)
        {
            return TimeSpan.Zero;
        }

        var total = TimeSpan.Zero;
        for (var segmentStart = range.Start; segmentStart < range.End;)
        {
            var nextDay = StartOfDay(segmentStart).AddDays(1);
            var segmentEnd = nextDay < range.End ? nextDay : range.End;
            var dayRows = cacheOnly
                ? reader.ReadCachedDetailRows(segmentStart, segmentEnd)
                : reader.ReadDetailRows(segmentStart, segmentEnd, includeLiveToday);
            total += EstimateCodingTime(dayRows);
            segmentStart = segmentEnd;
        }

        return total;
    }

    private static bool UsesEventBreakdown(SelectedRange range, IReadOnlyList<TokenUsageBucket> rows)
    {
        return range.IsCustomStart ||
               range.Mode == RangeMode.Day ||
               rows.Any(row => IsEventBucket(range, row));
    }

    private static bool IsEventBucket(SelectedRange range, TokenUsageBucket bucket)
    {
        if (range.IsCustomStart || range.Mode == RangeMode.Day)
        {
            return true;
        }

        if (range.Mode is RangeMode.Week or RangeMode.Cycle)
        {
            return bucket.LastTokenEventLocal is null ||
                   bucket.LastTokenEventLocal.Value < bucket.StartLocal.Add(MultiDayBreakdownInterval);
        }

        return false;
    }

    private static void AddBucketValues(TokenUsageBucket target, TokenUsageBucket source)
    {
        target.Events += source.Events;
        target.InputTokens += source.InputTokens;
        target.CachedInputTokens += source.CachedInputTokens;
        target.UncachedInputTokens += source.UncachedInputTokens;
        target.OutputTokens += source.OutputTokens;
        target.ReasoningOutputTokens += source.ReasoningOutputTokens;
        target.TotalTokens += source.TotalTokens;
        if (source.LastTokenEventLocal is not null &&
            (target.LastTokenEventLocal is null || source.LastTokenEventLocal > target.LastTokenEventLocal))
        {
            target.LastTokenEventLocal = source.LastTokenEventLocal;
        }
    }

    private static string FormatBucketLabel(SelectedRange range, DateTimeOffset start, bool eventBreakdown)
    {
        if (range.IsCustomStart)
        {
            return start.ToString("yyyy-MM-dd HH:mm:ss");
        }

        if (range.Mode == RangeMode.Day)
        {
            return start.ToString("HH:mm:ss");
        }

        return eventBreakdown
            ? start.ToString("MM-dd HH:mm")
            : start.ToString("yyyy-MM-dd");
    }

    private static string FormatTokenMillions(long value)
    {
        return $"{value / 1_000_000d:N3}M";
    }

    private static string FormatTokenAdaptive(long value)
    {
        if (value >= 1_000_000)
        {
            return FormatTokenMillions(value);
        }

        if (value >= 10_000)
        {
            return $"{value / 1_000d:N1}K";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:N2}K";
        }

        return value.ToString("N0");
    }

    private static string FormatBreakdownToken(long value, bool useEventScale)
    {
        return useEventScale
            ? FormatTokenAdaptive(value)
            : FormatTokenMillions(value);
    }

    private static string FormatMoney(decimal value, PriceProfile profile)
    {
        return value switch
        {
            >= 100 => $"{profile.CurrencySymbol}{value:N0}",
            >= 10 => $"{profile.CurrencySymbol}{value:N2}",
            >= 1 => $"{profile.CurrencySymbol}{value:N3}",
            _ => $"{profile.CurrencySymbol}{value:N4}"
        };
    }

    private static string FormatCost(decimal value, PriceProfile profile)
    {
        return string.Equals(profile.CurrencySymbol, "Credits", StringComparison.OrdinalIgnoreCase)
            ? FormatCredits(value)
            : FormatMoney(value, profile);
    }

    private static string FormatCredits(decimal value)
    {
        if (value >= 100_000_000m)
        {
            return $"{value / 100_000_000m:N2}亿";
        }

        if (value >= 1_000_000m)
        {
            return $"{value / 1_000_000m:N2}M";
        }

        return $"{value:N0}";
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

    private static TimeSpan EstimateCodingTime(IReadOnlyList<TokenUsageBucket> rows)
    {
        if (rows.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var ordered = rows
            .Where(row => row.Events > 0)
            .Select(row => row.StartLocal)
            .OrderBy(value => value)
            .ToList();
        if (ordered.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var active = TimeSpan.Zero;
        var sessionStart = ordered[0];
        var previous = ordered[0];
        var maxIdle = TimeSpan.FromMinutes(10);

        for (var i = 1; i < ordered.Count; i++)
        {
            var current = ordered[i];
            if (current - previous > maxIdle)
            {
                active += previous - sessionStart;
                sessionStart = current;
            }

            previous = current;
        }

        active += previous - sessionStart;
        return active;
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return "-";
        }

        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        }

        return $"{Math.Max(1, (int)Math.Round(value.TotalMinutes))}m";
    }

    private sealed record SelectedRange(
        DateTimeOffset Start,
        DateTimeOffset End,
        string Title,
        string BreakdownTitle,
        RangeMode Mode,
        bool IsCustomStart = false);

    private sealed record QueryResult(
        TokenUsageSummary Summary,
        IReadOnlyList<TokenUsageBucket> BreakdownRows,
        TimeSpan CodingTime,
        CodexQuotaEstimate? Quota,
        IReadOnlyList<CodexQuotaSnapshot> QuotaSnapshots);

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

    private sealed record ListScrollAnchor(int Index, string Text);

    private sealed class NoHorizontalScrollListView : ListView
    {
        private const int SB_HORZ = 0;
        private const int WM_PAINT = 0x000F;
        private const int WM_SIZE = 0x0005;
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_WINDOWPOSCHANGED = 0x0047;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        public NoHorizontalScrollListView()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HideHorizontalScrollBar();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            HideHorizontalScrollBar();
        }

        protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
        {
            base.OnColumnWidthChanged(e);
            HideHorizontalScrollBar();
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg is WM_PAINT or WM_SIZE or WM_NCCALCSIZE or WM_WINDOWPOSCHANGED)
            {
                HideHorizontalScrollBar();
            }
        }

        private void HideHorizontalScrollBar()
        {
            if (IsHandleCreated)
            {
                ShowScrollBar(Handle, SB_HORZ, false);
            }
        }
    }
}
