namespace CodexTokenMonitor;

public partial class Form1 : Form
{
    private const int BackgroundCacheIntervalMs = 120_000;
    private const decimal MinimumStableQuotaDeltaPercent = 3m;
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

    private enum UsageSource
    {
        Codex,
        ClaudeCode,
        ZCode
    }

    private enum QuotaWindowDisplayMode
    {
        FiveHour,
        Week
    }

    private readonly Label titleLabel = new();
    private readonly TabControl sourceTabs = new();
    private readonly Button planSettingsButton = new();
    private readonly Button priceSettingsButton = new();
    private readonly ComboBox rangeModeBox = new();
    private readonly DateTimePicker datePicker = new();
    private readonly ComboBox cycleBox = new();
    private readonly Button previousButton = new();
    private readonly Button nextButton = new();
    private readonly Button currentButton = new();
    private readonly Button startNowButton = new();
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
    private readonly ListView breakdownList = new();
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
    private bool isBackgroundCaching;
    private bool suppressDateRefresh;
    private bool suppressCycleRefresh;
    private bool suppressStartTimeRefresh;

    public Form1()
    {
        InitializeComponent();
        BuildUi();

        quotaCalculateButton.Click += (_, _) => UpdateQuotaLimitCalculation();
        planSettingsButton.Click += async (_, _) => await OpenSubscriptionPlanSettingsAsync();
        priceSettingsButton.Click += async (_, _) => await OpenPriceSettingsAsync();
        previousButton.Click += async (_, _) => await ShiftPeriodAsync(-1);
        nextButton.Click += async (_, _) => await ShiftPeriodAsync(1);
        currentButton.Click += async (_, _) => await JumpToCurrentPeriodAsync();
        startNowButton.Click += async (_, _) => await StartFromNowAsync();
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
        refreshTimer.Tick += async (_, _) => await RefreshUsageAsync();
        backgroundCacheTimer.Interval = BackgroundCacheIntervalMs;
        backgroundCacheTimer.Tick += async (_, _) => await WarmCacheInBackgroundAsync();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RefreshUsageAsync();
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
        MinimumSize = new Size(1000, 900);
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 960);
        Size = new Size(
            Math.Min(1180, Math.Max(1000, workingArea.Width - 80)),
            Math.Min(1100, Math.Max(1020, workingArea.Height - 40)));
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
            ColumnCount = 4,
            RowCount = 1
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
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

        ConfigureTopButton(planSettingsButton, "套餐设置");
        planSettingsButton.Margin = new Padding(0, 0, 8, 0);
        panel.Controls.Add(planSettingsButton, 2, 0);

        ConfigureTopButton(priceSettingsButton, "价格设置");
        panel.Controls.Add(priceSettingsButton, 3, 0);

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
        summaryPanel.Padding = new Padding(22, 12, 22, 12);
        summaryPanel.Margin = new Padding(0, 0, 0, 12);
        summaryPanel.Height = 150;
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
        totalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        totalLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        totalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        layout.Controls.Add(totalLayout, 0, 0);

        totalLayout.Controls.Add(CreateCaption("TOTAL TOKENS"), 0, 0);
        totalValue.AutoSize = false;
        totalValue.Dock = DockStyle.Fill;
        totalValue.Text = "-";
        totalValue.Font = new Font("Segoe UI", 25f, FontStyle.Bold);
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
        quotaPanel.Height = 116;
        quotaPanel.Margin = new Padding(0, 0, 0, 12);
        quotaPanel.Padding = new Padding(14, 10, 14, 10);
        quotaPanel.BackColor = Color.White;
        quotaPanel.Visible = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        quotaPanel.Controls.Add(layout);

        layout.Controls.Add(BuildQuotaCard("5h", quota5hValue, quota5hDetail), 0, 0);
        layout.Controls.Add(BuildQuotaCard("周", quotaWeekValue, quotaWeekDetail), 1, 0);
        layout.Controls.Add(BuildQuotaCard("套餐", planSpendValue, planSpendDetail), 2, 0);
        layout.Controls.Add(BuildQuotaCalculationCard(), 3, 0);
        return quotaPanel;
    }

    private static Control BuildQuotaCard(string title, Label valueLabel, Label detailLabel)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8, 0, 8, 0),
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        var titleLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = title,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Bold),
            ForeColor = Color.FromArgb(90, 103, 119),
            Margin = new Padding(0)
        };
        layout.Controls.Add(titleLabel, 0, 0);

        valueLabel.Dock = DockStyle.Fill;
        valueLabel.AutoSize = false;
        valueLabel.Text = "-";
        valueLabel.TextAlign = ContentAlignment.MiddleLeft;
        valueLabel.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        valueLabel.ForeColor = Color.FromArgb(29, 40, 55);
        valueLabel.Margin = new Padding(0);
        layout.Controls.Add(valueLabel, 0, 1);

        detailLabel.Dock = DockStyle.Fill;
        detailLabel.AutoSize = false;
        detailLabel.Text = "-";
        detailLabel.TextAlign = ContentAlignment.MiddleLeft;
        detailLabel.Font = new Font("Microsoft YaHei UI", 8.6f);
        detailLabel.ForeColor = Color.FromArgb(87, 99, 116);
        detailLabel.Margin = new Padding(0);
        detailLabel.AutoEllipsis = true;
        layout.Controls.Add(detailLabel, 0, 2);

        return layout;
    }

    private Control BuildQuotaCalculationCard()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8, 0, 0, 0),
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = "估算",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Bold),
            ForeColor = Color.FromArgb(90, 103, 119),
            Margin = new Padding(0)
        };
        layout.Controls.Add(titleLabel, 0, 0);

        quotaCalculateButton.Text = "计算";
        quotaCalculateButton.Dock = DockStyle.Left;
        quotaCalculateButton.Width = 86;
        quotaCalculateButton.Height = 30;
        quotaCalculateButton.Margin = new Padding(0, 2, 0, 2);
        quotaCalculateButton.BackColor = Color.FromArgb(21, 128, 106);
        quotaCalculateButton.ForeColor = Color.White;
        quotaCalculateButton.FlatStyle = FlatStyle.Flat;
        quotaCalculateButton.FlatAppearance.BorderSize = 0;
        layout.Controls.Add(quotaCalculateButton, 0, 1);

        quotaLimitValue.Dock = DockStyle.Fill;
        quotaLimitValue.AutoSize = false;
        quotaLimitValue.Text = "";
        quotaLimitValue.TextAlign = ContentAlignment.TopLeft;
        quotaLimitValue.Font = new Font("Microsoft YaHei UI", 8.6f);
        quotaLimitValue.ForeColor = Color.FromArgb(87, 99, 116);
        quotaLimitValue.Margin = new Padding(0, 2, 0, 0);
        quotaLimitValue.AutoEllipsis = true;
        layout.Controls.Add(quotaLimitValue, 0, 2);

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
        timelineChart.Margin = new Padding(0, 0, 0, 12);
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
    }

    private async Task ClearCacheAsync()
    {
        if (isRefreshing)
        {
            return;
        }

        backgroundCacheCts?.Cancel();
        var source = CurrentSource();
        await usageQueryGate.WaitAsync();
        bool deleted;
        try
        {
            deleted = source switch
            {
                UsageSource.Codex => CodexUsageReader.ClearCache(),
                UsageSource.ClaudeCode => ClaudeUsageReader.ClearCache(),
                UsageSource.ZCode => ZCodeUsageReader.ClearCache(),
                _ => false
            };
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

    private async Task OpenSubscriptionPlanSettingsAsync()
    {
        using var form = new SubscriptionPlanForm();
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            await RefreshUsageAsync();
        }
    }

    private async Task RefreshUsageAsync()
    {
        if (isRefreshing)
        {
            return;
        }

        isRefreshing = true;
        refreshButton.Enabled = false;
        clearCacheButton.Enabled = false;
        SetStatus("正在刷新...");

        try
        {
            var range = GetSelectedRange();
            var source = CurrentSource();
            var includeLiveToday = ShouldIncludeLiveToday(range);
            var cachedQuota = currentQuotaEstimate;
            if (isBackgroundCaching)
            {
                backgroundCacheCts?.Cancel();
                SetStatus("正在刷新（暂停缓存）...");
            }

            await usageQueryGate.WaitAsync();
            QueryResult result;
            try
            {
                result = await Task.Run(() =>
                {
                    if (range.IsCustomStart)
                    {
                        var transientRows = BuildTransientBreakdownRows(source, range);
                        var transientSummary = CreateSummaryFromRows(range, transientRows);
                        var transientQuota = source == UsageSource.Codex
                            ? CodexUsageReader.ReadQuotaEstimate()
                            : null;
                        var transientQuotaSnapshots = source == UsageSource.Codex
                            ? CodexUsageReader.ReadQuotaSnapshots(range.Start, range.End)
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
                        var dayRows = includeLiveToday
                            ? ReadSourceDetailRows(source, range.Start, range.End, includeLiveToday)
                            : ReadSourceCachedDetailRows(source, range.Start, range.End);
                        var daySummary = includeLiveToday
                            ? CreateSummaryFromRows(range, dayRows)
                            : ReadSourceCachedRange(source, range.Start, range.End);
                        var dayQuota = ReadQuotaForRefresh(source, includeLiveToday, cachedQuota);
                        var dayQuotaSnapshots = source == UsageSource.Codex
                            ? ReadQuotaSnapshotsForRefresh(range, includeLiveToday, dayQuota)
                            : Array.Empty<CodexQuotaSnapshot>();
                        return new QueryResult(
                            daySummary,
                            dayRows,
                            EstimateCodingTime(dayRows),
                            dayQuota,
                            dayQuotaSnapshots);
                    }

                    var summary = source switch
                    {
                        UsageSource.Codex => includeLiveToday
                            ? CodexUsageReader.ReadRange(range.Start, range.End, includeLiveToday)
                            : CodexUsageReader.ReadCachedRange(range.Start, range.End),
                        UsageSource.ClaudeCode => includeLiveToday
                            ? ClaudeUsageReader.ReadRange(range.Start, range.End, includeLiveToday)
                            : ClaudeUsageReader.ReadCachedRange(range.Start, range.End),
                        UsageSource.ZCode => includeLiveToday
                            ? ZCodeUsageReader.ReadRange(range.Start, range.End, includeLiveToday)
                            : ZCodeUsageReader.ReadCachedRange(range.Start, range.End),
                        _ => throw new InvalidOperationException("Unknown usage source.")
                    };
                    var rows = BuildBreakdownRows(source, range, summary);
                    var codingTime = EstimateCodingTimeForRange(source, range, rows, includeLiveToday, cacheOnly: !includeLiveToday);
                    var quota = ReadQuotaForRefresh(source, includeLiveToday, cachedQuota);
                    var quotaSnapshots = source == UsageSource.Codex
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
                source => GetIncompleteCacheDays(source, BackgroundCacheStart, lastHistoricalDay)
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
                    await usageQueryGate.WaitAsync(token);
                    try
                    {
                        await Task.Run(() => CodexUsageReader.WarmQuotaSnapshotDay(day), token);
                    }
                    finally
                    {
                        usageQueryGate.Release();
                    }

                    completed++;
                    await Task.Delay(20, token);
                }

                foreach (var source in sources)
                {
                    if (!pending[source].Contains(date))
                    {
                        continue;
                    }

                    token.ThrowIfCancellationRequested();
                    while (isRefreshing)
                    {
                        await Task.Delay(250, token);
                    }

                    SetStatus($"缓存 {completed + 1}/{total} {SourceTitle(source)} {day:MM-dd}");
                    await usageQueryGate.WaitAsync(token);
                    try
                    {
                        await Task.Run(() => WarmSourceDay(source, day), token);
                    }
                    finally
                    {
                        usageQueryGate.Release();
                    }

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

    private static IReadOnlyList<DateTimeOffset> GetIncompleteCacheDays(
        UsageSource source,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive)
    {
        return source switch
        {
            UsageSource.Codex => CodexUsageReader.GetIncompleteHistoricalDays(startInclusive, endInclusive),
            UsageSource.ClaudeCode => ClaudeUsageReader.GetIncompleteHistoricalDays(startInclusive, endInclusive),
            UsageSource.ZCode => ZCodeUsageReader.GetIncompleteHistoricalDays(startInclusive, endInclusive),
            _ => Array.Empty<DateTimeOffset>()
        };
    }

    private static void WarmSourceDay(UsageSource source, DateTimeOffset dayStart)
    {
        var dayEnd = dayStart.AddDays(1);
        _ = source switch
        {
            UsageSource.Codex => CodexUsageReader.ReadRange(dayStart, dayEnd, includeLiveToday: false),
            UsageSource.ClaudeCode => ClaudeUsageReader.ReadRange(dayStart, dayEnd, includeLiveToday: false),
            UsageSource.ZCode => ZCodeUsageReader.ReadRange(dayStart, dayEnd, includeLiveToday: false),
            _ => throw new InvalidOperationException("Unknown usage source.")
        };

        _ = source switch
        {
            UsageSource.Codex => CodexUsageReader.ReadDetailRows(dayStart, dayEnd, includeLiveToday: false),
            UsageSource.ClaudeCode => ClaudeUsageReader.ReadDetailRows(dayStart, dayEnd, includeLiveToday: false),
            UsageSource.ZCode => ZCodeUsageReader.ReadDetailRows(dayStart, dayEnd, includeLiveToday: false),
            _ => throw new InvalidOperationException("Unknown usage source.")
        };
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
        Text = $"{SourceTitle(source)} Token 额度监控器 - {range.Title}";
        var displayPresets = PriceSettingsStore.DisplayPresetsForSource(SourceKey(source), count: 0);
        var tablePresets = displayPresets.Take(3).ToList();
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
        currentQuotaSnapshots = source == UsageSource.Codex
            ? quotaSnapshots
            : Array.Empty<CodexQuotaSnapshot>();
        ApplyQuotaSummary(source, quota);
        if (source == UsageSource.Codex && range.Mode == RangeMode.Cycle)
        {
            UpdateCycleOptions(keepSelection: true);
        }

        breakdownList.BeginUpdate();
        breakdownList.Items.Clear();
        var showTimeline = breakdownRows.Count > 0;
        if (timelineRowStyle is not null)
        {
            timelineRowStyle.Height = showTimeline ? 150 : 0;
        }

        timelineChart.Visible = showTimeline;
        if (showTimeline)
        {
            timelineChart.SetData(range.Start, range.End, breakdownRows);
        }
        else
        {
            timelineChart.ClearData();
        }

        var eventBreakdown = UsesEventBreakdown(range, breakdownRows);
        breakdownList.Columns[0].Text = eventBreakdown ? "时间" : "日期";
        breakdownList.Columns[1].Text = "Total";
        breakdownList.Columns[2].Text = "Input";
        breakdownList.Columns[3].Text = "Cached";
        breakdownList.Columns[4].Text = "Uncached";
        breakdownList.Columns[5].Text = "Output";
        breakdownList.Columns[6].Text = FormatPresetColumnTitle(tablePresets.ElementAtOrDefault(0), "价格1");
        breakdownList.Columns[7].Text = FormatPresetColumnTitle(tablePresets.ElementAtOrDefault(1), "价格2");
        breakdownList.Columns[8].Text = FormatPresetColumnTitle(tablePresets.ElementAtOrDefault(2), "价格3");
        breakdownList.Columns[9].Text = "额度(5h/7d)";
        ApplyBreakdownColumnWidths(range, eventBreakdown);
        foreach (var bucket in breakdownRows)
        {
            var rowIsEvent = IsEventBucket(range, bucket);
            var item = new ListViewItem(FormatBucketLabel(range, bucket.StartLocal, rowIsEvent));
            item.SubItems.Add(FormatBreakdownToken(bucket.TotalTokens, rowIsEvent));
            item.SubItems.Add(FormatBreakdownToken(bucket.InputTokens, rowIsEvent));
            item.SubItems.Add(FormatBreakdownToken(bucket.CachedInputTokens, rowIsEvent));
            item.SubItems.Add(FormatBreakdownToken(bucket.UncachedInputTokens, rowIsEvent));
            item.SubItems.Add(FormatTokenAdaptive(bucket.OutputTokens));
            AddPresetCost(item, bucket, tablePresets.ElementAtOrDefault(0));
            AddPresetCost(item, bucket, tablePresets.ElementAtOrDefault(1));
            AddPresetCost(item, bucket, tablePresets.ElementAtOrDefault(2));
            item.SubItems.Add(source == UsageSource.Codex
                ? FormatQuotaSnapshotForBucket(range, bucket, quotaSnapshots, rowIsEvent)
                : "-");
            breakdownList.Items.Add(item);
        }
        breakdownList.EndUpdate();
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
            Width = 218,
            Height = 118,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14, 6, 10, 4),
            Margin = new Padding(0, 0, 14, 0),
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

    private static string SourceKey(UsageSource source)
    {
        return source switch
        {
            UsageSource.ClaudeCode => "claude",
            UsageSource.ZCode => "zcode",
            _ => "codex"
        };
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
        currentQuotaEstimate = show ? quota : null;
        quotaPanel.Visible = show;
        quotaPanel.Height = show ? 116 : 0;
        quotaPanel.Margin = show ? new Padding(0, 0, 0, 12) : new Padding(0);
        quotaCalculateButton.Enabled = show && quota is not null;
        ApplyCurrentPlanSummary();
        if (!show || quota is null)
        {
            quota5hValue.Text = "-";
            quota5hDetail.Text = "";
            quotaWeekValue.Text = "-";
            quotaWeekDetail.Text = "";
            quotaLimitValue.Text = "";
            return;
        }

        ApplyQuotaWindow(quota5hValue, quota5hDetail, quota.FiveHour, QuotaWindowDisplayMode.FiveHour);
        ApplyQuotaWindow(quotaWeekValue, quotaWeekDetail, quota.Week, QuotaWindowDisplayMode.Week);
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
        planSpendValue.Text = FormatCny(amount);
        planSpendDetail.Text = string.Join(" / ", active.Select(item => item.PlanName).Distinct());
    }

    private static void ApplyQuotaWindow(
        Label valueLabel,
        Label detailLabel,
        CodexQuotaWindowEstimate? window,
        QuotaWindowDisplayMode mode)
    {
        if (window is null)
        {
            valueLabel.Text = "-";
            detailLabel.Text = "";
            return;
        }

        var remainingPercent = Math.Max(0m, 100m - window.UsedPercent);
        valueLabel.Text = $"{remainingPercent:N0}%";
        var resetAt = window.ResetAtLocal ?? window.WindowEndLocal;
        detailLabel.Text = mode == QuotaWindowDisplayMode.FiveHour
            ? $"{resetAt:HH:mm} · {FormatMoney(window.UsedGptCost, PriceProfiles.Gpt55StandardLong)}"
            : $"{resetAt:MM-dd HH:mm} · {FormatQuotaLimit(window)}";
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

    private void ApplyBreakdownColumnWidths(SelectedRange range, bool eventBreakdown)
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
        breakdownList.Columns[6].Width = 92;
        breakdownList.Columns[7].Width = 108;
        breakdownList.Columns[8].Width = 132;
        breakdownList.Columns[9].Width = 126;
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
                : bucket.StartLocal.AddHours(1)
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

    private static string SourceTitle(UsageSource source)
    {
        return source switch
        {
            UsageSource.ClaudeCode => "Claude Code",
            UsageSource.ZCode => "ZCode",
            _ => "Codex"
        };
    }

    private static CodexQuotaEstimate? ReadQuotaForRefresh(
        UsageSource source,
        bool includeLiveToday,
        CodexQuotaEstimate? cachedQuota)
    {
        if (source != UsageSource.Codex)
        {
            return null;
        }

        return includeLiveToday ? CodexUsageReader.ReadQuotaEstimate() : cachedQuota;
    }

    private static IReadOnlyList<CodexQuotaSnapshot> ReadQuotaSnapshotsForRefresh(
        SelectedRange range,
        bool includeLiveToday,
        CodexQuotaEstimate? quota)
    {
        var snapshots = CodexUsageReader.ReadCachedQuotaSnapshots(range.Start, range.End);
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
        UsageSource source,
        SelectedRange range,
        TokenUsageSummary summary)
    {
        if (range.Mode == RangeMode.Day)
        {
            return ReadSourceCachedDetailRows(source, range.Start, range.End);
        }

        if (range.Mode is RangeMode.Week or RangeMode.Cycle)
        {
            return BuildHourlyBreakdownRows(source, range, summary);
        }

        return summary.DailyBuckets;
    }

    private static IReadOnlyList<TokenUsageBucket> BuildHourlyBreakdownRows(
        UsageSource source,
        SelectedRange range,
        TokenUsageSummary summary)
    {
        var detailRows = ReadSourceCachedDetailRows(source, range.Start, range.End);
        if (detailRows.Count == 0)
        {
            return summary.DailyBuckets;
        }

        var buckets = new Dictionary<DateTimeOffset, TokenUsageBucket>();
        var detailDates = new HashSet<DateOnly>();
        foreach (var row in detailRows)
        {
            var hour = StartOfHour(row.StartLocal);
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

    private static DateTimeOffset StartOfHour(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, 0, 0, value.Offset);
    }

    private static TimeSpan EstimateCodingTimeForRange(
        UsageSource source,
        SelectedRange range,
        IReadOnlyList<TokenUsageBucket> breakdownRows,
        bool includeLiveToday,
        bool cacheOnly = false)
    {
        if (range.Mode == RangeMode.Day || range.IsCustomStart)
        {
            return EstimateCodingTime(breakdownRows);
        }

        var cachedRows = ReadSourceCachedDetailRows(source, range.Start, range.End);
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
                ? ReadSourceCachedDetailRows(source, segmentStart, segmentEnd)
                : ReadSourceDetailRows(source, segmentStart, segmentEnd, includeLiveToday);
            total += EstimateCodingTime(dayRows);
            segmentStart = segmentEnd;
        }

        return total;
    }

    private static TokenUsageSummary ReadSourceCachedRange(
        UsageSource source,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        return source switch
        {
            UsageSource.Codex => CodexUsageReader.ReadCachedRange(startLocal, endLocal),
            UsageSource.ClaudeCode => ClaudeUsageReader.ReadCachedRange(startLocal, endLocal),
            UsageSource.ZCode => ZCodeUsageReader.ReadCachedRange(startLocal, endLocal),
            _ => new TokenUsageSummary { StartLocal = startLocal, EndLocal = endLocal }
        };
    }

    private static IReadOnlyList<TokenUsageBucket> ReadSourceCachedDetailRows(
        UsageSource source,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        return source switch
        {
            UsageSource.Codex => CodexUsageReader.ReadCachedDetailRows(startLocal, endLocal),
            UsageSource.ClaudeCode => ClaudeUsageReader.ReadCachedDetailRows(startLocal, endLocal),
            UsageSource.ZCode => ZCodeUsageReader.ReadCachedDetailRows(startLocal, endLocal),
            _ => Array.Empty<TokenUsageBucket>()
        };
    }

    private static IReadOnlyList<TokenUsageBucket> ReadSourceDetailRows(
        UsageSource source,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday)
    {
        return source switch
        {
            UsageSource.Codex => CodexUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday),
            UsageSource.ClaudeCode => ClaudeUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday),
            UsageSource.ZCode => ZCodeUsageReader.ReadDetailRows(startLocal, endLocal, includeLiveToday),
            _ => Array.Empty<TokenUsageBucket>()
        };
    }

    private static IReadOnlyList<TokenUsageBucket> BuildTransientBreakdownRows(
        UsageSource source,
        SelectedRange range)
    {
        return source switch
        {
            UsageSource.Codex => CodexUsageReader.ReadTransientDetailRows(range.Start, range.End),
            UsageSource.ClaudeCode => ClaudeUsageReader.ReadTransientDetailRows(range.Start, range.End),
            UsageSource.ZCode => ZCodeUsageReader.ReadTransientDetailRows(range.Start, range.End),
            _ => Array.Empty<TokenUsageBucket>()
        };
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
                   bucket.LastTokenEventLocal.Value < bucket.StartLocal.AddHours(1);
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
            ? start.ToString("MM-dd HH:00")
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
}
