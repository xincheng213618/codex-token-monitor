using System.Globalization;

namespace CodexTokenMonitor;

internal sealed class ResetOpportunityForm : Form
{
    private readonly DataGridView resetGrid = new();
    private readonly Label statusLabel = new();

    public ResetOpportunityForm()
    {
        BuildUi();
        LoadRows(ResetOpportunityStore.Load());
    }

    private void BuildUi()
    {
        Text = "重置机会设置";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(880, 460);
        MinimumSize = new Size(760, 380);
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "手动记录 Codex 可用重置机会。默认有效期按获得时间 + 30 天生成，也可以直接修改。",
            ForeColor = Color.FromArgb(55, 65, 81),
            Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);

        resetGrid.Dock = DockStyle.Fill;
        resetGrid.AllowUserToAddRows = true;
        resetGrid.AllowUserToDeleteRows = true;
        resetGrid.RowHeadersVisible = false;
        resetGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        resetGrid.BackgroundColor = Color.White;
        resetGrid.BorderStyle = BorderStyle.FixedSingle;
        resetGrid.Columns.Add("granted", "获得时间");
        resetGrid.Columns.Add("expires", "过期时间");
        resetGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "used",
            HeaderText = "已使用"
        });
        resetGrid.Columns.Add("note", "备注");
        resetGrid.Columns[0].FillWeight = 25;
        resetGrid.Columns[1].FillWeight = 25;
        resetGrid.Columns[2].FillWeight = 12;
        resetGrid.Columns[3].FillWeight = 38;
        resetGrid.CellEndEdit += (_, e) => AutoFillExpiry(e.RowIndex, e.ColumnIndex);
        resetGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (resetGrid.IsCurrentCellDirty)
            {
                resetGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        root.Controls.Add(resetGrid, 0, 1);

        root.Controls.Add(BuildActions(), 0, 2);

        statusLabel.AutoSize = true;
        statusLabel.Text = "";
        statusLabel.ForeColor = Color.FromArgb(75, 85, 99);
        statusLabel.Margin = new Padding(0, 8, 0, 0);
        root.Controls.Add(statusLabel, 0, 3);
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

        var todayButton = CreateActionButton("新增今天", Color.FromArgb(229, 234, 242), Color.FromArgb(55, 65, 81));
        todayButton.Click += (_, _) => AddTodayRow();
        layout.Controls.Add(todayButton);

        var syncButton = CreateActionButton("从 Codex 同步", Color.FromArgb(229, 234, 242), Color.FromArgb(55, 65, 81));
        syncButton.Click += async (_, _) => await SyncFromCodexAsync(syncButton);
        layout.Controls.Add(syncButton);

        var defaultsButton = CreateActionButton("恢复示例", Color.FromArgb(229, 234, 242), Color.FromArgb(55, 65, 81));
        defaultsButton.Click += (_, _) => LoadRows(ResetOpportunityStore.Defaults());
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

    private void LoadRows(IReadOnlyList<ResetOpportunityRecord> records)
    {
        resetGrid.Rows.Clear();
        foreach (var record in records.OrderBy(item => item.GrantedLocal))
        {
            resetGrid.Rows.Add(
                FormatLocal(record.GrantedLocal),
                FormatLocal(record.ExpiresLocal),
                record.IsUsed,
                record.Note);
        }
    }

    private void AddTodayRow()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        resetGrid.Rows.Add(
            FormatLocal(now),
            FormatLocal(now.AddDays(30)),
            false,
            "手动新增");
    }

    private async Task SyncFromCodexAsync(Button syncButton)
    {
        syncButton.Enabled = false;
        statusLabel.Text = "正在从 Codex 同步...";
        try
        {
            var result = await ResetOpportunityStore.SyncFromCodexAsync();
            statusLabel.Text = result.Message;
            if (result.Success)
            {
                LoadRows(result.Records);
            }
            else
            {
                MessageBox.Show(this, result.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            syncButton.Enabled = true;
        }
    }

    private void AutoFillExpiry(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || columnIndex != 0)
        {
            return;
        }

        var row = resetGrid.Rows[rowIndex];
        var expiresText = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(expiresText))
        {
            return;
        }

        var grantedText = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture);
        if (TryParseLocal(grantedText, out var granted))
        {
            row.Cells[1].Value = FormatLocal(granted.AddDays(30));
        }
    }

    private void SaveRows()
    {
        try
        {
            var records = new List<ResetOpportunityRecord>();
            foreach (DataGridViewRow row in resetGrid.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }

                var grantedText = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture);
                var expiresText = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture);
                var usedValue = row.Cells[2].Value;
                var note = Convert.ToString(row.Cells[3].Value, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(grantedText) &&
                    string.IsNullOrWhiteSpace(expiresText) &&
                    usedValue is null &&
                    string.IsNullOrWhiteSpace(note))
                {
                    continue;
                }

                var granted = ParseLocal(grantedText);
                var expires = string.IsNullOrWhiteSpace(expiresText)
                    ? granted.AddDays(30)
                    : ParseLocal(expiresText);
                if (expires <= granted)
                {
                    throw new InvalidOperationException("过期时间必须晚于获得时间。");
                }

                records.Add(new ResetOpportunityRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    GrantedLocal = granted,
                    ExpiresLocal = expires,
                    IsUsed = Convert.ToBoolean(usedValue ?? false, CultureInfo.InvariantCulture),
                    Note = note?.Trim() ?? ""
                });
            }

            ResetOpportunityStore.Save(records);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string FormatLocal(DateTimeOffset value)
    {
        return value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseLocal(string? text)
    {
        if (!TryParseLocal(text, out var value))
        {
            throw new InvalidOperationException($"时间格式不正确：{text}");
        }

        return value;
    }

    private static bool TryParseLocal(string? text, out DateTimeOffset value)
    {
        value = default;
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dateTime))
        {
            return false;
        }

        value = new DateTimeOffset(
            dateTime.Year,
            dateTime.Month,
            dateTime.Day,
            dateTime.Hour,
            dateTime.Minute,
            dateTime.Second,
            CodexUsageReader.BeijingOffset);
        return true;
    }
}
