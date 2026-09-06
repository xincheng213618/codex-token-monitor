using System.Globalization;

namespace CodexTokenMonitor;

internal sealed class SubscriptionPlanForm : Form
{
    private readonly DataGridView plansGrid = new();
    private readonly DateTimePicker monthStart = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Width = 170 };
    private readonly ComboBox monthlyPlan = new() { Width = 110 };
    private readonly NumericUpDown monthlyAmount = new() { Width = 95, Maximum = 1000000, DecimalPlaces = 2, Value = 1380 };
    private readonly Label accountStatus = new() { AutoSize = true, Text = "正在读取 Codex 账户套餐…", ForeColor = Color.FromArgb(101, 117, 138), Margin = new Padding(0, 0, 0, 10) };
    private readonly CancellationTokenSource lifetime = new();

    public SubscriptionPlanForm()
    {
        BuildUi();
        LoadRows(SubscriptionPlanStore.Load());
        Shown += async (_, _) => await ReadAccountPlanAsync();
        FormClosed += (_, _) => lifetime.Cancel();
    }

    private void BuildUi()
    {
        Text = "套餐设置";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 540);
        MinimumSize = new Size(780, 440);
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 5,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "默认按一个自然月记录；月费沿用上次购买金额。统计区间内的费用按时间摊分。",
            ForeColor = Color.FromArgb(55, 65, 81),
            Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);
        root.Controls.Add(accountStatus, 0, 1);
        root.Controls.Add(BuildMonthlyEntry(), 0, 2);

        plansGrid.Dock = DockStyle.Fill;
        plansGrid.AllowUserToAddRows = true;
        plansGrid.AllowUserToDeleteRows = true;
        plansGrid.RowHeadersVisible = false;
        plansGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        plansGrid.BackgroundColor = Color.White;
        plansGrid.BorderStyle = BorderStyle.FixedSingle;
        plansGrid.Columns.Add("start", "开始");
        plansGrid.Columns.Add("end", "结束（留空为一个月）");
        plansGrid.Columns.Add("plan", "套餐");
        plansGrid.Columns.Add("amount", "金额 ¥");
        plansGrid.Columns[0].FillWeight = 26;
        plansGrid.Columns[1].FillWeight = 26;
        plansGrid.Columns[2].FillWeight = 26;
        plansGrid.Columns[3].FillWeight = 16;
        root.Controls.Add(plansGrid, 0, 3);

        root.Controls.Add(BuildActions(), 0, 4);
    }

    private Control BuildMonthlyEntry()
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        monthlyPlan.Items.AddRange(new object[] { "Pro 20x", "Pro 5x", "Plus", "Pro", "Business" });
        monthlyPlan.Text = "Pro 20x";
        foreach (var pair in new (string Label, Control Editor)[] { ("开始", monthStart), ("套餐", monthlyPlan), ("月费 ¥", monthlyAmount) })
        {
            row.Controls.Add(new Label { Text = pair.Label, AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            pair.Editor.Margin = new Padding(0, 0, 12, 0);
            row.Controls.Add(pair.Editor);
        }
        var add = CreateActionButton("添加一个月", Color.FromArgb(21, 128, 106), Color.White);
        add.Click += (_, _) => AddMonthlyRecord();
        row.Controls.Add(add);
        return row;
    }

    private void AddMonthlyRecord()
    {
        try
        {
            plansGrid.EndEdit();
            var records = ReadRows();
            var start = new DateTimeOffset(DateTime.SpecifyKind(monthStart.Value, DateTimeKind.Unspecified), CodexUsageReader.BeijingOffset);
            var record = SubscriptionPlanStore.CreateMonthly(start, monthlyPlan.Text, monthlyAmount.Value);
            if (records.Any(p => p.StartLocal < record.EndLocal && p.EndLocal > record.StartLocal))
                throw new InvalidOperationException("这个月与已有套餐重叠，请调整开始时间，或直接修改下方记录。");
            records.Add(record);
            LoadRows(records);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private async Task ReadAccountPlanAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var plan = await Task.Run(() => CodexAppServerQuotaReader.ReadPlanTypeAsync(timeout.Token), timeout.Token);
            if (IsDisposed) return;
            accountStatus.Text = plan is null
                ? "未读取到账户套餐；已沿用购买记录，默认 Pro 20x / ¥1380。"
                : $"账户套餐：{plan}。具体倍率、人民币月费和付款日期沿用购买记录，可在下方修改。";
            // Generic 'pro' does not distinguish the user's paid 5x/20x tier.
            if (plan is not null && !monthlyPlan.Text.StartsWith(plan, StringComparison.OrdinalIgnoreCase))
                monthlyPlan.Text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan.Replace('_', ' '));
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) accountStatus.Text = "账户读取超时；仍可直接使用上次套餐和月费添加一个月。";
        }
        catch (Exception)
        {
            if (!IsDisposed) accountStatus.Text = "暂时无法读取账户套餐；已沿用上次购买记录。";
        }
    }

    private Control BuildActions()
    {
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 12, 0, 0)
        };

        var saveButton = CreateActionButton("保存", Color.FromArgb(21, 128, 106), Color.White);
        saveButton.Click += (_, _) => SaveRows();
        layout.Controls.Add(saveButton);

        var cancelButton = CreateActionButton("取消", Color.FromArgb(229, 234, 242), Color.FromArgb(55, 65, 81));
        cancelButton.Click += (_, _) => DialogResult = DialogResult.Cancel;
        layout.Controls.Add(cancelButton);

        var importButton = CreateActionButton("从 Codex 导入", Color.FromArgb(229, 234, 242), Color.FromArgb(55, 65, 81));
        importButton.Click += (_, _) => ImportFromCodex();
        layout.Controls.Add(importButton);

        var defaultsButton = CreateActionButton("恢复示例", Color.FromArgb(229, 234, 242), Color.FromArgb(55, 65, 81));
        defaultsButton.Click += (_, _) => LoadRows(SubscriptionPlanStore.Defaults());
        layout.Controls.Add(defaultsButton);
        return layout;
    }

    private static Button CreateActionButton(string text, Color backColor, Color foreColor)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(88, 34),
            BackColor = backColor,
            ForeColor = foreColor,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(10, 0, 0, 0)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void LoadRows(IReadOnlyList<SubscriptionPlanRecord> records)
    {
        plansGrid.Rows.Clear();
        foreach (var record in records.OrderBy(item => item.StartLocal))
        {
            var index = plansGrid.Rows.Add(
                record.StartLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                record.EndLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                record.PlanName,
                record.AmountCny.ToString("0.##", CultureInfo.InvariantCulture));
            plansGrid.Rows[index].Tag = record.Id;
        }
        var latest = records.MaxBy(p => p.EndLocal);
        var now = BeijingClock.Now;
        monthStart.Value = latest?.EndLocal.DateTime ?? new DateTime(now.Year, now.Month, 2);
        monthlyPlan.Text = latest?.PlanName ?? "Pro 20x";
        monthlyAmount.Value = Math.Clamp(latest?.AmountCny ?? 1380m, monthlyAmount.Minimum, monthlyAmount.Maximum);
    }

    private void SaveRows()
    {
        try
        {
            SubscriptionPlanStore.Save(ReadRows());
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ImportFromCodex()
    {
        try
        {
            var import = SubscriptionPlanImporter.TryImportFromCodex();
            LoadRows(MergeRows(ReadRows(), import.Records));
            MessageBox.Show(
                this,
                $"{import.Message}\n\n导入结果仅在当前窗口预览，点击“保存”后才会生效。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private List<SubscriptionPlanRecord> ReadRows()
    {
        var records = new List<SubscriptionPlanRecord>();
        foreach (DataGridViewRow row in plansGrid.Rows)
        {
            if (row.IsNewRow)
            {
                continue;
            }

            var startText = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture);
            var endText = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture);
            var planName = Convert.ToString(row.Cells[2].Value, CultureInfo.InvariantCulture);
            var amountText = Convert.ToString(row.Cells[3].Value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(startText) &&
                string.IsNullOrWhiteSpace(endText) &&
                string.IsNullOrWhiteSpace(planName) &&
                string.IsNullOrWhiteSpace(amountText))
            {
                continue;
            }

            var start = ParseLocal(startText);
            var end = string.IsNullOrWhiteSpace(endText) ? start.AddMonths(1) : ParseLocal(endText);
            if (end <= start)
            {
                throw new InvalidOperationException("套餐结束时间必须晚于开始时间。");
            }

            records.Add(new SubscriptionPlanRecord
            {
                Id = row.Tag as string ?? Guid.NewGuid().ToString("N"),
                StartLocal = start,
                EndLocal = end,
                PlanName = string.IsNullOrWhiteSpace(planName) ? "未命名套餐" : planName.Trim(),
                AmountCny = decimal.Parse(amountText ?? "0", CultureInfo.InvariantCulture)
            });
        }

        return records;
    }

    private static IReadOnlyList<SubscriptionPlanRecord> MergeRows(
        IEnumerable<SubscriptionPlanRecord> current,
        IEnumerable<SubscriptionPlanRecord> imported)
    {
        return current
            .Concat(imported)
            .GroupBy(item => $"{item.StartLocal:O}|{item.EndLocal:O}|{item.PlanName}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.AmountCny).First())
            .OrderBy(item => item.StartLocal)
            .ToList();
    }

    private static DateTimeOffset ParseLocal(string? text)
    {
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var value))
        {
            throw new InvalidOperationException($"时间格式不正确：{text}");
        }

        return new DateTimeOffset(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            value.Second,
            CodexUsageReader.BeijingOffset);
    }
}
