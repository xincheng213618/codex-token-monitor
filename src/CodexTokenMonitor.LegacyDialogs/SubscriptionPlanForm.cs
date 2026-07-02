using System.Globalization;

namespace CodexTokenMonitor;

internal sealed class SubscriptionPlanForm : Form
{
    private readonly DataGridView plansGrid = new();

    public SubscriptionPlanForm()
    {
        BuildUi();
        LoadRows(SubscriptionPlanStore.Load());
    }

    private void BuildUi()
    {
        Text = "套餐设置";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(780, 460);
        MinimumSize = new Size(700, 380);
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "记录实际购买的套餐，统计区间会按时间重叠自动摊分费用。",
            ForeColor = Color.FromArgb(55, 65, 81),
            Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);

        plansGrid.Dock = DockStyle.Fill;
        plansGrid.AllowUserToAddRows = true;
        plansGrid.AllowUserToDeleteRows = true;
        plansGrid.RowHeadersVisible = false;
        plansGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        plansGrid.BackgroundColor = Color.White;
        plansGrid.BorderStyle = BorderStyle.FixedSingle;
        plansGrid.Columns.Add("start", "开始");
        plansGrid.Columns.Add("end", "结束");
        plansGrid.Columns.Add("plan", "套餐");
        plansGrid.Columns.Add("amount", "金额 ¥");
        plansGrid.Columns[0].FillWeight = 26;
        plansGrid.Columns[1].FillWeight = 26;
        plansGrid.Columns[2].FillWeight = 26;
        plansGrid.Columns[3].FillWeight = 16;
        root.Controls.Add(plansGrid, 0, 1);

        root.Controls.Add(BuildActions(), 0, 2);
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
            plansGrid.Rows.Add(
                record.StartLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                record.EndLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                record.PlanName,
                record.AmountCny.ToString("0.##", CultureInfo.InvariantCulture));
        }
    }

    private void SaveRows()
    {
        try
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
                var end = ParseLocal(endText);
                if (end <= start)
                {
                    throw new InvalidOperationException("套餐结束时间必须晚于开始时间。");
                }

                records.Add(new SubscriptionPlanRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    StartLocal = start,
                    EndLocal = end,
                    PlanName = string.IsNullOrWhiteSpace(planName) ? "未命名套餐" : planName.Trim(),
                    AmountCny = decimal.Parse(amountText ?? "0", CultureInfo.InvariantCulture)
                });
            }

            SubscriptionPlanStore.Save(records);
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
        var import = SubscriptionPlanStore.ImportFromCodex();
        LoadRows(SubscriptionPlanStore.Load());
        MessageBox.Show(this, import.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
