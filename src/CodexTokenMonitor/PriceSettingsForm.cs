using System.Globalization;

namespace CodexTokenMonitor;

internal sealed class PriceSettingsForm : Form
{
    private static readonly string[] Groups = { "Codex", "Claude Code", "ZCode" };

    private readonly TabControl groupTabs = new();
    private readonly Dictionary<string, DataGridView> groupGrids = new(StringComparer.OrdinalIgnoreCase);

    public PriceSettingsForm()
    {
        BuildUi();
        LoadSettings(PriceSettingsStore.Current);
    }

    private void BuildUi()
    {
        Text = "价格设置";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1260, 760);
        MinimumSize = new Size(1040, 640);
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = "主界面按当前分组顺序展示价格；选中行可以置顶或上下移动。",
            ForeColor = Color.FromArgb(55, 65, 81),
            Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);

        root.Controls.Add(BuildToolbar(), 0, 1);
        root.Controls.Add(BuildGroupTabs(), 0, 2);
        root.Controls.Add(BuildActions(), 0, 3);
    }

    private Control BuildToolbar()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 46,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(12, 7, 12, 7)
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
            Margin = new Padding(0, 6, 14, 0)
        });

        var pinButton = CreateActionButton("置顶显示", Color.FromArgb(21, 128, 106), Color.White);
        pinButton.Margin = new Padding(0, 0, 10, 0);
        pinButton.Click += (_, _) => PinSelectedPreset();
        layout.Controls.Add(pinButton);

        var upButton = CreateActionButton("上移", Color.FromArgb(229, 234, 242), Color.FromArgb(55, 65, 81));
        upButton.Margin = new Padding(0, 0, 8, 0);
        upButton.Click += (_, _) => MoveSelectedPreset(-1);
        layout.Controls.Add(upButton);

        var downButton = CreateActionButton("下移", Color.FromArgb(229, 234, 242), Color.FromArgb(55, 65, 81));
        downButton.Margin = new Padding(0);
        downButton.Click += (_, _) => MoveSelectedPreset(1);
        layout.Controls.Add(downButton);
        return panel;
    }

    private Control BuildGroupTabs()
    {
        groupTabs.Dock = DockStyle.Fill;
        groupTabs.Margin = new Padding(0);

        foreach (var group in Groups)
        {
            var page = new TabPage(group)
            {
                BackColor = Color.FromArgb(246, 248, 251),
                Padding = new Padding(0)
            };
            var grid = CreateGrid();
            groupGrids[group] = grid;
            page.Controls.Add(grid);
            groupTabs.TabPages.Add(page);
        }

        return groupTabs;
    }

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false
        };

        grid.Columns.Add("provider", "Provider");
        grid.Columns.Add("model", "Model");
        grid.Columns.Add("currency", "Currency");
        grid.Columns.Add("unit", "Unit");
        grid.Columns.Add("divisor", "Divisor");
        grid.Columns.Add("input", "Input");
        grid.Columns.Add("cached", "Cached");
        grid.Columns.Add("output", "Output");
        grid.Columns.Add("source", "Source");
        grid.Columns[0].FillWeight = 13;
        grid.Columns[1].FillWeight = 24;
        grid.Columns[2].FillWeight = 8;
        grid.Columns[3].FillWeight = 16;
        grid.Columns[4].FillWeight = 10;
        grid.Columns[5].FillWeight = 8;
        grid.Columns[6].FillWeight = 8;
        grid.Columns[7].FillWeight = 8;
        grid.Columns[8].FillWeight = 22;
        return grid;
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
        foreach (var grid in groupGrids.Values)
        {
            grid.Rows.Clear();
        }

        foreach (var preset in settings.Presets)
        {
            var group = NormalizeGroupName(preset.Group);
            if (!groupGrids.TryGetValue(group, out var grid))
            {
                grid = groupGrids["Codex"];
            }

            AddPresetRow(grid, preset);
        }
    }

    private static void AddPresetRow(DataGridView grid, PricePreset preset)
    {
        grid.Rows.Add(
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

    private void PinSelectedPreset()
    {
        var grid = CurrentGrid();
        var row = grid.CurrentRow;
        if (row is null || row.IsNewRow || row.Index == 0)
        {
            return;
        }

        MoveRow(grid, row.Index, 0);
    }

    private void MoveSelectedPreset(int offset)
    {
        var grid = CurrentGrid();
        var row = grid.CurrentRow;
        if (row is null || row.IsNewRow)
        {
            return;
        }

        var targetIndex = row.Index + offset;
        if (targetIndex < 0 || targetIndex >= grid.Rows.Count || grid.Rows[targetIndex].IsNewRow)
        {
            return;
        }

        MoveRow(grid, row.Index, targetIndex);
    }

    private void MoveRow(DataGridView grid, int sourceIndex, int targetIndex)
    {
        var group = CurrentGroup();
        var preset = TryReadPreset(grid.Rows[sourceIndex], group);
        if (preset is null)
        {
            MessageBox.Show(this, "选中行价格格式不正确。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        grid.Rows.RemoveAt(sourceIndex);
        grid.Rows.Insert(targetIndex,
            preset.Provider,
            preset.Model,
            preset.CurrencySymbol,
            preset.UnitLabel,
            preset.Divisor.ToString("0.####", CultureInfo.InvariantCulture),
            preset.UncachedInput.ToString("0.####", CultureInfo.InvariantCulture),
            preset.CachedInput.ToString("0.####", CultureInfo.InvariantCulture),
            preset.Output.ToString("0.####", CultureInfo.InvariantCulture),
            preset.Source);
        grid.ClearSelection();
        grid.Rows[targetIndex].Selected = true;
        grid.CurrentCell = grid.Rows[targetIndex].Cells[0];
    }

    private void SaveSettings()
    {
        try
        {
            var presets = ReadPresetRows();
            if (presets.Count == 0)
            {
                presets = PricePreset.Defaults().Select(item => item.Clone()).ToList();
            }

            var settings = PriceSettingsStore.Current.Clone();
            settings.DisplayOrderVersion = PriceSettingsStore.Defaults().DisplayOrderVersion;
            settings.Presets = presets;
            ApplyLegacyProfiles(settings, presets);
            PriceSettingsStore.Save(settings);
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
        foreach (var group in Groups)
        {
            var grid = groupGrids[group];
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }

                var preset = TryReadPreset(row, group);
                if (preset is not null)
                {
                    result.Add(preset);
                }
            }
        }

        return result;
    }

    private static PricePreset? TryReadPreset(DataGridViewRow row, string group)
    {
        var model = CellText(row, 1);
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return new PricePreset
        {
            Group = group,
            Provider = CellText(row, 0),
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

    private static void ApplyLegacyProfiles(PriceSettings settings, IReadOnlyList<PricePreset> presets)
    {
        var codex = presets
            .Where(item => string.Equals(NormalizeGroupName(item.Group), "Codex", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var current = PriceSettingsStore.Current;

        var gpt = codex.FirstOrDefault(item =>
            item.Provider.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            item.Model.Contains("GPT", StringComparison.OrdinalIgnoreCase));
        if (gpt is not null)
        {
            settings.GptName = gpt.Model;
            settings.GptUncachedInputPerMillion = gpt.UncachedInput;
            settings.GptCachedInputPerMillion = gpt.CachedInput;
            settings.GptOutputPerMillion = gpt.Output;
        }
        else
        {
            settings.GptName = current.GptName;
            settings.GptUncachedInputPerMillion = current.GptUncachedInputPerMillion;
            settings.GptCachedInputPerMillion = current.GptCachedInputPerMillion;
            settings.GptOutputPerMillion = current.GptOutputPerMillion;
        }

        var deepSeek = codex.FirstOrDefault(item => item.Provider.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase));
        settings.DeepSeekUncachedInputPerMillion = deepSeek?.UncachedInput ?? current.DeepSeekUncachedInputPerMillion;
        settings.DeepSeekCachedInputPerMillion = deepSeek?.CachedInput ?? current.DeepSeekCachedInputPerMillion;
        settings.DeepSeekOutputPerMillion = deepSeek?.Output ?? current.DeepSeekOutputPerMillion;

        var xiaomi = codex.FirstOrDefault(item => item.Provider.Contains("Xiaomi", StringComparison.OrdinalIgnoreCase));
        settings.XiaomiUncachedInputCreditsPerToken = xiaomi?.UncachedInput ?? current.XiaomiUncachedInputCreditsPerToken;
        settings.XiaomiCachedInputCreditsPerToken = xiaomi?.CachedInput ?? current.XiaomiCachedInputCreditsPerToken;
        settings.XiaomiOutputCreditsPerToken = xiaomi?.Output ?? current.XiaomiOutputCreditsPerToken;
    }

    private DataGridView CurrentGrid()
    {
        var group = CurrentGroup();
        return groupGrids.TryGetValue(group, out var grid) ? grid : groupGrids["Codex"];
    }

    private string CurrentGroup()
    {
        return groupTabs.SelectedTab?.Text ?? "Codex";
    }

    private static string NormalizeGroupName(string group)
    {
        return group.Trim().ToLowerInvariant() switch
        {
            "claude" or "claude code" or "claudecode" => "Claude Code",
            "zcode" or "glm" or "z.ai" or "zai" or "智谱" => "ZCode",
            _ => "Codex"
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
}
