using System.Globalization;

namespace CodexTokenMonitor;

internal sealed class PriceSettingsForm : Form
{
    private readonly TextBox gptNameBox = new();
    private readonly NumericUpDown gptInputBox = new();
    private readonly NumericUpDown gptCachedBox = new();
    private readonly NumericUpDown gptOutputBox = new();
    private readonly NumericUpDown deepSeekInputBox = new();
    private readonly NumericUpDown deepSeekCachedBox = new();
    private readonly NumericUpDown deepSeekOutputBox = new();
    private readonly NumericUpDown xiaomiInputBox = new();
    private readonly NumericUpDown xiaomiCachedBox = new();
    private readonly NumericUpDown xiaomiOutputBox = new();
    private readonly DataGridView presetGrid = new();
    private readonly ComboBox presetTargetBox = new();

    public PriceSettingsForm()
    {
        BuildUi();
        LoadSettings(PriceSettingsStore.Current);
    }

    private void BuildUi()
    {
        Text = "价格设置";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1080, 760);
        MinimumSize = new Size(940, 640);
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
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
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = "主界面第一栏按来源取主模型：Codex=GPT，Claude Code=Claude，ZCode=GLM；第二、三栏固定 DeepSeek / Xiaomi。选中行可以置顶或上下移动。",
            ForeColor = Color.FromArgb(55, 65, 81),
            Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);

        root.Controls.Add(BuildActivePanel(), 0, 1);
        root.Controls.Add(BuildPresetToolbar(), 0, 2);
        ConfigurePresetGrid();
        root.Controls.Add(presetGrid, 0, 3);
        root.Controls.Add(BuildActions(), 0, 4);
    }

    private Control BuildActivePanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));

        layout.Controls.Add(BuildGptPanel(), 0, 0);
        layout.Controls.Add(BuildProviderPanel("DeepSeek V4 Pro (CNY / 1M tokens)", deepSeekInputBox, deepSeekCachedBox, deepSeekOutputBox), 1, 0);
        layout.Controls.Add(BuildProviderPanel("Xiaomi MiMo V2.5 Pro (Credits / token)", xiaomiInputBox, xiaomiCachedBox, xiaomiOutputBox), 2, 0);
        return layout;
    }

    private Control BuildGptPanel()
    {
        var panel = CreatePanel("GPT-5.5 (USD / 1M tokens)");
        panel.RowCount = 3;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var nameLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 6)
        };
        nameLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        nameLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameLayout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "价格档",
            ForeColor = Color.FromArgb(55, 65, 81),
            Margin = new Padding(0, 7, 10, 0)
        }, 0, 0);
        gptNameBox.Dock = DockStyle.Top;
        gptNameBox.Margin = new Padding(0, 3, 0, 0);
        nameLayout.Controls.Add(gptNameBox, 1, 0);
        panel.Controls.Add(nameLayout, 0, 1);
        panel.SetColumnSpan(nameLayout, 3);

        AddPriceRow(panel, 2, gptInputBox, gptCachedBox, gptOutputBox);
        return panel;
    }

    private static Control BuildProviderPanel(
        string title,
        NumericUpDown inputBox,
        NumericUpDown cachedBox,
        NumericUpDown outputBox)
    {
        var panel = CreatePanel(title);
        panel.RowCount = 2;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddPriceRow(panel, 1, inputBox, cachedBox, outputBox);
        return panel;
    }

    private static TableLayoutPanel CreatePanel(string title)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(14, 10, 14, 12),
            Margin = new Padding(0, 0, 12, 0),
            BackColor = Color.White
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));

        var titleLabel = new Label
        {
            AutoSize = true,
            Text = title,
            Font = new Font("Microsoft YaHei UI", 9.8f, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 41, 55),
            Margin = new Padding(0, 0, 0, 8)
        };
        panel.Controls.Add(titleLabel, 0, 0);
        panel.SetColumnSpan(titleLabel, 3);
        return panel;
    }

    private static void AddPriceRow(
        TableLayoutPanel panel,
        int row,
        NumericUpDown inputBox,
        NumericUpDown cachedBox,
        NumericUpDown outputBox)
    {
        panel.Controls.Add(CreateField("Input", inputBox), 0, row);
        panel.Controls.Add(CreateField("Cached", cachedBox), 1, row);
        panel.Controls.Add(CreateField("Output", outputBox), 2, row);
    }

    private static Control CreateField(string label, NumericUpDown box)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 12, 0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = label,
            ForeColor = Color.FromArgb(87, 99, 116),
            Margin = new Padding(0, 0, 0, 3)
        }, 0, 0);

        ConfigurePriceBox(box);
        layout.Controls.Add(box, 0, 1);
        return layout;
    }

    private Control BuildPresetToolbar()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
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
            Text = "价格库",
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 41, 55),
            Margin = new Padding(0, 6, 12, 0)
        });

        var applyButton = CreateActionButton("置顶显示", Color.FromArgb(21, 128, 106), Color.White);
        applyButton.Margin = new Padding(0, 0, 12, 0);
        applyButton.Click += (_, _) => ApplySelectedPreset();
        layout.Controls.Add(applyButton);

        var upButton = CreateActionButton("上移", Color.FromArgb(229, 234, 242), Color.FromArgb(55, 65, 81));
        upButton.Margin = new Padding(0, 0, 8, 0);
        upButton.Click += (_, _) => MoveSelectedPreset(-1);
        layout.Controls.Add(upButton);

        var downButton = CreateActionButton("下移", Color.FromArgb(229, 234, 242), Color.FromArgb(55, 65, 81));
        downButton.Margin = new Padding(0, 0, 12, 0);
        downButton.Click += (_, _) => MoveSelectedPreset(1);
        layout.Controls.Add(downButton);

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "表格价格和顺序可编辑；保存后会保留。部分价格变动频繁，默认档用于对比，不替代官方账单。",
            ForeColor = Color.FromArgb(87, 99, 116),
            Margin = new Padding(0, 6, 0, 0)
        });
        return panel;
    }

    private void ConfigurePresetGrid()
    {
        presetGrid.Dock = DockStyle.Fill;
        presetGrid.AllowUserToAddRows = true;
        presetGrid.AllowUserToDeleteRows = true;
        presetGrid.RowHeadersVisible = false;
        presetGrid.BackgroundColor = Color.White;
        presetGrid.BorderStyle = BorderStyle.FixedSingle;
        presetGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        presetGrid.Columns.Add("provider", "Provider");
        presetGrid.Columns.Add("model", "Model");
        presetGrid.Columns.Add("currency", "Currency");
        presetGrid.Columns.Add("unit", "Unit");
        presetGrid.Columns.Add("divisor", "Divisor");
        presetGrid.Columns.Add("input", "Input");
        presetGrid.Columns.Add("cached", "Cached");
        presetGrid.Columns.Add("output", "Output");
        presetGrid.Columns.Add("source", "Source");
        presetGrid.Columns[0].FillWeight = 12;
        presetGrid.Columns[1].FillWeight = 20;
        presetGrid.Columns[2].FillWeight = 8;
        presetGrid.Columns[3].FillWeight = 14;
        presetGrid.Columns[4].FillWeight = 10;
        presetGrid.Columns[5].FillWeight = 8;
        presetGrid.Columns[6].FillWeight = 8;
        presetGrid.Columns[7].FillWeight = 8;
        presetGrid.Columns[8].FillWeight = 20;
    }

    private static void ConfigurePriceBox(NumericUpDown box)
    {
        box.Minimum = 0;
        box.Maximum = 1_000_000;
        box.DecimalPlaces = 4;
        box.Increment = 0.01m;
        box.Width = 112;
        box.TextAlign = HorizontalAlignment.Right;
        box.ThousandsSeparator = true;
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
        saveButton.Click += (_, _) => SaveSettings();
        layout.Controls.Add(saveButton);

        var cancelButton = CreateActionButton("取消", Color.FromArgb(229, 234, 242), Color.FromArgb(55, 65, 81));
        cancelButton.Click += (_, _) => DialogResult = DialogResult.Cancel;
        layout.Controls.Add(cancelButton);

        var defaultsButton = CreateActionButton("恢复默认价格库", Color.FromArgb(229, 234, 242), Color.FromArgb(55, 65, 81));
        defaultsButton.Click += (_, _) => LoadSettings(PriceSettingsStore.Defaults());
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

    private void LoadSettings(PriceSettings settings)
    {
        gptNameBox.Text = settings.GptName;
        gptInputBox.Value = Clamp(settings.GptUncachedInputPerMillion, gptInputBox);
        gptCachedBox.Value = Clamp(settings.GptCachedInputPerMillion, gptCachedBox);
        gptOutputBox.Value = Clamp(settings.GptOutputPerMillion, gptOutputBox);
        deepSeekInputBox.Value = Clamp(settings.DeepSeekUncachedInputPerMillion, deepSeekInputBox);
        deepSeekCachedBox.Value = Clamp(settings.DeepSeekCachedInputPerMillion, deepSeekCachedBox);
        deepSeekOutputBox.Value = Clamp(settings.DeepSeekOutputPerMillion, deepSeekOutputBox);
        xiaomiInputBox.Value = Clamp(settings.XiaomiUncachedInputCreditsPerToken, xiaomiInputBox);
        xiaomiCachedBox.Value = Clamp(settings.XiaomiCachedInputCreditsPerToken, xiaomiCachedBox);
        xiaomiOutputBox.Value = Clamp(settings.XiaomiOutputCreditsPerToken, xiaomiOutputBox);

        presetGrid.Rows.Clear();
        foreach (var preset in settings.Presets)
        {
            AddPresetRow(preset);
        }
    }

    private void AddPresetRow(PricePreset preset)
    {
        presetGrid.Rows.Add(
            preset.Provider,
            preset.Model,
            preset.CurrencySymbol,
            preset.UnitLabel,
            preset.Divisor.ToString("0.####", CultureInfo.InvariantCulture),
            preset.UncachedInput.ToString("0.####", CultureInfo.InvariantCulture),
            preset.CachedInput.ToString("0.####", CultureInfo.InvariantCulture),
            preset.Output.ToString("0.####", CultureInfo.InvariantCulture),
            preset.Source);
    }

    private void ApplySelectedPreset()
    {
        var row = presetGrid.CurrentRow;
        if (row is null || row.IsNewRow)
        {
            return;
        }

        var preset = TryReadPreset(row);
        if (preset is null)
        {
            MessageBox.Show(this, "选中行价格格式不正确。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var index = row.Index;
        presetGrid.Rows.RemoveAt(index);
        presetGrid.Rows.Insert(0,
            preset.Provider,
            preset.Model,
            preset.CurrencySymbol,
            preset.UnitLabel,
            preset.Divisor.ToString("0.####", CultureInfo.InvariantCulture),
            preset.UncachedInput.ToString("0.####", CultureInfo.InvariantCulture),
            preset.CachedInput.ToString("0.####", CultureInfo.InvariantCulture),
            preset.Output.ToString("0.####", CultureInfo.InvariantCulture),
            preset.Source);
        presetGrid.ClearSelection();
        presetGrid.Rows[0].Selected = true;
        presetGrid.CurrentCell = presetGrid.Rows[0].Cells[0];
    }

    private void MoveSelectedPreset(int offset)
    {
        var row = presetGrid.CurrentRow;
        if (row is null || row.IsNewRow)
        {
            return;
        }

        var targetIndex = row.Index + offset;
        if (targetIndex < 0 || targetIndex >= presetGrid.Rows.Count || presetGrid.Rows[targetIndex].IsNewRow)
        {
            return;
        }

        var preset = TryReadPreset(row);
        if (preset is null)
        {
            return;
        }

        presetGrid.Rows.RemoveAt(row.Index);
        presetGrid.Rows.Insert(targetIndex,
            preset.Provider,
            preset.Model,
            preset.CurrencySymbol,
            preset.UnitLabel,
            preset.Divisor.ToString("0.####", CultureInfo.InvariantCulture),
            preset.UncachedInput.ToString("0.####", CultureInfo.InvariantCulture),
            preset.CachedInput.ToString("0.####", CultureInfo.InvariantCulture),
            preset.Output.ToString("0.####", CultureInfo.InvariantCulture),
            preset.Source);
        presetGrid.ClearSelection();
        presetGrid.Rows[targetIndex].Selected = true;
        presetGrid.CurrentCell = presetGrid.Rows[targetIndex].Cells[0];
    }

    private void SaveSettings()
    {
        try
        {
            PriceSettingsStore.Save(new PriceSettings
            {
                GptName = gptNameBox.Text,
                GptUncachedInputPerMillion = gptInputBox.Value,
                GptCachedInputPerMillion = gptCachedBox.Value,
                GptOutputPerMillion = gptOutputBox.Value,
                DeepSeekUncachedInputPerMillion = deepSeekInputBox.Value,
                DeepSeekCachedInputPerMillion = deepSeekCachedBox.Value,
                DeepSeekOutputPerMillion = deepSeekOutputBox.Value,
                XiaomiUncachedInputCreditsPerToken = xiaomiInputBox.Value,
                XiaomiCachedInputCreditsPerToken = xiaomiCachedBox.Value,
                XiaomiOutputCreditsPerToken = xiaomiOutputBox.Value,
                Presets = ReadPresetRows()
            });
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private List<PricePreset> ReadPresetRows()
    {
        var result = new List<PricePreset>();
        foreach (DataGridViewRow row in presetGrid.Rows)
        {
            if (row.IsNewRow)
            {
                continue;
            }

            var preset = TryReadPreset(row);
            if (preset is not null)
            {
                result.Add(preset);
            }
        }

        return result.Count == 0 ? PricePreset.Defaults().Select(item => item.Clone()).ToList() : result;
    }

    private static PricePreset? TryReadPreset(DataGridViewRow row)
    {
        var provider = CellText(row, 0);
        var model = CellText(row, 1);
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return new PricePreset
        {
            Provider = provider,
            Model = model,
            CurrencySymbol = string.IsNullOrWhiteSpace(CellText(row, 2)) ? "$" : CellText(row, 2),
            UnitLabel = string.IsNullOrWhiteSpace(CellText(row, 3)) ? "1M tokens" : CellText(row, 3),
            Divisor = ParseDecimal(CellText(row, 4), 1_000_000m),
            UncachedInput = ParseDecimal(CellText(row, 5), 0m),
            CachedInput = ParseDecimal(CellText(row, 6), 0m),
            Output = ParseDecimal(CellText(row, 7), 0m),
            Source = CellText(row, 8)
        };
    }

    private static string CellText(DataGridViewRow row, int index)
    {
        return Convert.ToString(row.Cells[index].Value, CultureInfo.InvariantCulture)?.Trim() ?? "";
    }

    private static decimal ParseDecimal(string text, decimal fallback)
    {
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static decimal Clamp(decimal value, NumericUpDown box)
    {
        if (value < box.Minimum)
        {
            return box.Minimum;
        }

        return value > box.Maximum ? box.Maximum : value;
    }
}
