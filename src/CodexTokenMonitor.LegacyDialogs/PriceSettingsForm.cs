using System.Globalization;

namespace CodexTokenMonitor;

internal sealed class PriceSettingsForm : Form
{
    private const int PrioritySlotCount = 3;
    private static readonly Color PageBackColor = Color.FromArgb(246, 248, 251);
    private static readonly Color CardBackColor = Color.White;
    private static readonly Color PrimaryColor = Color.FromArgb(21, 128, 106);
    private static readonly Color PrimaryTextColor = Color.White;
    private static readonly Color TextColor = Color.FromArgb(31, 41, 55);
    private static readonly Color MutedTextColor = Color.FromArgb(92, 105, 122);
    private static readonly Color SecondaryButtonColor = Color.FromArgb(229, 234, 242);

    private readonly TabControl groupTabs = new();
    private readonly Dictionary<string, GroupPageState> groupPages = new(StringComparer.OrdinalIgnoreCase);
    private bool syncingPrioritySelectors;

    public PriceSettingsForm()
    {
        BuildUi();
        LoadSettings(PriceSettingsStore.Current);
    }

    private void BuildUi()
    {
        Text = "价格设置";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1120, 590);
        MinimumSize = new Size(960, 560);
        BackColor = PageBackColor;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        KeyPreview = true;
        KeyDown += PriceSettingsForm_KeyDown;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildGroupTabs(), 0, 1);
        root.Controls.Add(BuildActions(), 0, 2);
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 12)
        };
        header.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = "价格设置",
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = TextColor,
            Margin = new Padding(0)
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = "先设置主界面优先展示的价格；只有维护价格数据时，才需要展开完整价格库。",
            ForeColor = MutedTextColor,
            Margin = new Padding(0, 5, 0, 0)
        }, 0, 1);
        return header;
    }

    private Control BuildGroupTabs()
    {
        groupTabs.Dock = DockStyle.Fill;
        groupTabs.Margin = new Padding(0);
        groupTabs.SelectedIndexChanged += (_, _) => RefreshCurrentGroup();

        foreach (var group in PricePresetGroups.All)
        {
            var page = new TabPage(group)
            {
                BackColor = PageBackColor,
                Padding = new Padding(0)
            };
            var state = BuildGroupPage(group);
            groupPages[group] = state;
            page.Controls.Add(state.Root);
            groupTabs.TabPages.Add(page);
        }

        return groupTabs;
    }

    private GroupPageState BuildGroupPage(string group)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = PageBackColor,
            Margin = new Padding(0),
            Padding = new Padding(0, 10, 0, 0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var grid = CreateGrid();
        var priorityBoxes = new ComboBox[PrioritySlotCount];
        var priorityPanel = BuildPriorityPanel(priorityBoxes);
        var libraryPanel = BuildLibraryPanel(group, grid, out var searchBox, out var countLabel);
        var libraryHeader = BuildLibraryHeader(group, libraryPanel, searchBox, out var toggleButton);
        libraryPanel.Visible = false;

        root.Controls.Add(priorityPanel, 0, 0);
        root.Controls.Add(libraryHeader, 0, 1);
        root.Controls.Add(libraryPanel, 0, 2);

        var state = new GroupPageState(
            group,
            root,
            grid,
            priorityBoxes,
            searchBox,
            countLabel,
            toggleButton,
            libraryPanel);

        for (var i = 0; i < priorityBoxes.Length; i++)
        {
            var slotIndex = i;
            priorityBoxes[i].SelectedIndexChanged += (_, _) => PrioritySelectionChanged(state, slotIndex);
        }

        searchBox.TextChanged += (_, _) => ApplySearch(state);
        grid.CellDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0)
            {
                EditSelectedPreset(state);
            }
        };
        grid.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Enter)
            {
                EditSelectedPreset(state);
                eventArgs.Handled = true;
            }
            else if (eventArgs.KeyCode == Keys.Delete)
            {
                DeleteSelectedPreset(state);
                eventArgs.Handled = true;
            }
        };
        return state;
    }

    private Control BuildPriorityPanel(ComboBox[] priorityBoxes)
    {
        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = CardBackColor,
            Padding = new Padding(16, 14, 16, 16),
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 10)
        };
        card.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "主界面优先展示",
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = TextColor,
            Margin = new Padding(0)
        }, 0, 0);
        card.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "第一项是主价格。常见窗口优先显示前三项，窗口较宽时继续按价格库顺序显示。",
            ForeColor = MutedTextColor,
            Margin = new Padding(0, 4, 0, 10)
        }, 0, 1);

        var slots = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = PrioritySlotCount,
            RowCount = 1,
            Margin = new Padding(0)
        };
        for (var i = 0; i < PrioritySlotCount; i++)
        {
            slots.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / PrioritySlotCount));
            var slot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(i == 0 ? 0 : 8, 0, i == PrioritySlotCount - 1 ? 0 : 8, 0)
            };
            slot.Controls.Add(new Label
            {
                AutoSize = true,
                Text = i == 0 ? "主价格" : $"对比价格 {i + 1}",
                ForeColor = i == 0 ? PrimaryColor : MutedTextColor,
                Font = new Font(Font, i == 0 ? FontStyle.Bold : FontStyle.Regular),
                Margin = new Padding(0, 0, 0, 5)
            }, 0, 0);

            var combo = new ComboBox
            {
                Dock = DockStyle.Top,
                DropDownStyle = ComboBoxStyle.DropDownList,
                IntegralHeight = false,
                DropDownHeight = 280,
                Margin = new Padding(0),
                AccessibleName = i == 0 ? "主价格" : $"对比价格 {i + 1}"
            };
            priorityBoxes[i] = combo;
            slot.Controls.Add(combo, 0, 1);
            slots.Controls.Add(slot, i, 0);
        }

        card.Controls.Add(slots, 0, 2);
        return card;
    }

    private Control BuildLibraryHeader(
        string group,
        Control libraryPanel,
        TextBox searchBox,
        out Button toggleButton)
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = CardBackColor,
            Padding = new Padding(12, 7, 12, 7),
            Margin = new Padding(0, 0, 0, 1)
        };

        toggleButton = CreateActionButton("管理完整价格库", SecondaryButtonColor, TextColor);
        toggleButton.AutoSize = false;
        toggleButton.Size = new Size(160, 34);
        toggleButton.Dock = DockStyle.Left;
        toggleButton.Margin = new Padding(0);
        toggleButton.TextAlign = ContentAlignment.MiddleLeft;
        toggleButton.AccessibleDescription = "展开或收起完整价格库";
        toggleButton.Click += (_, _) => ToggleLibrary(group, libraryPanel, searchBox);
        header.Controls.Add(toggleButton);
        return header;
    }

    private Control BuildLibraryPanel(
        string group,
        DataGridView grid,
        out TextBox searchBox,
        out Label countLabel)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = CardBackColor,
            Padding = new Padding(12, 10, 12, 12),
            Margin = new Padding(0)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 10)
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var searchPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        searchBox = new TextBox
        {
            Width = 300,
            PlaceholderText = "搜索供应商、模型或来源",
            Margin = new Padding(0, 3, 10, 0),
            AccessibleName = "搜索价格库"
        };
        countLabel = new Label
        {
            AutoSize = true,
            ForeColor = MutedTextColor,
            Margin = new Padding(0, 8, 0, 0)
        };
        searchPanel.Controls.Add(searchBox);
        searchPanel.Controls.Add(countLabel);
        toolbar.Controls.Add(searchPanel, 0, 0);

        var addButton = CreateActionButton("新增价格", SecondaryButtonColor, TextColor);
        addButton.Click += (_, _) => AddPreset(group);
        toolbar.Controls.Add(addButton, 1, 0);

        var deleteButton = CreateActionButton("删除自定义", SecondaryButtonColor, TextColor);
        deleteButton.Click += (_, _) => DeleteSelectedPreset(groupPages[group]);
        toolbar.Controls.Add(deleteButton, 2, 0);

        var editButton = CreateActionButton("编辑所选", PrimaryColor, PrimaryTextColor);
        editButton.Click += (_, _) => EditSelectedPreset(groupPages[group]);
        toolbar.Controls.Add(editButton, 3, 0);

        panel.Controls.Add(toolbar, 0, 0);
        panel.Controls.Add(grid, 0, 1);
        return panel;
    }

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            BackgroundColor = CardBackColor,
            BorderStyle = BorderStyle.FixedSingle,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoGenerateColumns = false,
            ShowCellToolTips = true
        };
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 238, 233);
        grid.DefaultCellStyle.SelectionForeColor = TextColor;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        grid.EnableHeadersVisualStyles = false;

        AddGridColumn(grid, "order", "展示", 7);
        AddGridColumn(grid, "provider", "供应商", 14);
        AddGridColumn(grid, "model", "模型", 24);
        AddGridColumn(grid, "input", "输入", 9);
        AddGridColumn(grid, "cached", "缓存输入", 9);
        AddGridColumn(grid, "output", "输出", 9);
        AddGridColumn(grid, "unit", "计价单位", 16);
        AddGridColumn(grid, "source", "来源", 22);
        return grid;
    }

    private static void AddGridColumn(DataGridView grid, string name, string header, float fillWeight)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            FillWeight = fillWeight,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private Control BuildActions()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 12, 0, 0)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        var defaultsButton = CreateActionButton("恢复当前分组默认", SecondaryButtonColor, TextColor);
        defaultsButton.Click += (_, _) => RestoreCurrentGroupDefaults();
        left.Controls.Add(defaultsButton);
        bar.Controls.Add(left, 0, 0);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        var cancelButton = CreateActionButton("取消", SecondaryButtonColor, TextColor);
        cancelButton.DialogResult = DialogResult.Cancel;
        right.Controls.Add(cancelButton);

        var saveButton = CreateActionButton("保存", PrimaryColor, PrimaryTextColor);
        saveButton.Click += (_, _) => SaveSettings();
        right.Controls.Add(saveButton);
        bar.Controls.Add(right, 1, 0);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        return bar;
    }

    private static Button CreateActionButton(string text, Color backColor, Color foreColor)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(96, 34),
            BackColor = backColor,
            ForeColor = foreColor,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(8, 0, 0, 0),
            Padding = new Padding(10, 0, 10, 0),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void LoadSettings(PriceSettings settings)
    {
        foreach (var group in PricePresetGroups.All)
        {
            var state = groupPages[group];
            state.Grid.Rows.Clear();
            foreach (var preset in settings.PresetsForGroup(group))
            {
                AddPresetRow(state.Grid, preset.Clone());
            }

            RefreshGroup(state);
        }
    }

    private static void AddPresetRow(DataGridView grid, PricePreset preset, int? insertIndex = null)
    {
        int index;
        if (insertIndex is null)
        {
            index = grid.Rows.Add();
        }
        else
        {
            index = insertIndex.Value;
            grid.Rows.Insert(index, 1);
        }

        var row = grid.Rows[index];
        row.Tag = preset.Clone();
        UpdatePresetRow(row, preset, index);
    }

    private static void UpdatePresetRow(DataGridViewRow row, PricePreset preset, int index)
    {
        row.Tag = preset.Clone();
        row.Cells["order"].Value = index switch
        {
            0 => "主价格",
            1 => "第 2",
            2 => "第 3",
            _ => "—"
        };
        row.Cells["provider"].Value = preset.Provider;
        row.Cells["model"].Value = preset.Model;
        row.Cells["input"].Value = FormatPrice(preset.CurrencySymbol, preset.UncachedInput);
        row.Cells["cached"].Value = FormatPrice(preset.CurrencySymbol, preset.CachedInput);
        row.Cells["output"].Value = FormatPrice(preset.CurrencySymbol, preset.Output);
        row.Cells["unit"].Value = FriendlyUnit(preset);
        row.Cells["source"].Value = preset.Source;
    }

    private static string FormatPrice(string symbol, decimal value)
    {
        var separator = string.Equals(symbol, "Credits", StringComparison.OrdinalIgnoreCase) ? " " : "";
        return $"{symbol}{separator}{value.ToString("0.####", CultureInfo.InvariantCulture)}";
    }

    private static string FriendlyUnit(PricePreset preset)
    {
        if (preset.Divisor == 1_000_000m)
        {
            return preset.UnitLabel
                .Replace("USD / 1M tokens", "美元 / 百万 tokens", StringComparison.OrdinalIgnoreCase)
                .Replace("CNY / 1M tokens", "人民币 / 百万 tokens", StringComparison.OrdinalIgnoreCase);
        }

        return preset.UnitLabel.Replace("Credits / token", "积分 / token", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshCurrentGroup()
    {
        if (groupPages.TryGetValue(CurrentGroup(), out var state))
        {
            RefreshGroup(state);
        }
    }

    private void RefreshGroup(GroupPageState state)
    {
        for (var i = 0; i < state.Grid.Rows.Count; i++)
        {
            if (state.Grid.Rows[i].Tag is PricePreset preset)
            {
                UpdatePresetRow(state.Grid.Rows[i], preset, i);
            }
        }

        RefreshPrioritySelectors(state);
        ApplySearch(state);
    }

    private void RefreshPrioritySelectors(GroupPageState state)
    {
        syncingPrioritySelectors = true;
        try
        {
            var presets = ReadGroupPresets(state);
            for (var slot = 0; slot < state.PriorityBoxes.Length; slot++)
            {
                var combo = state.PriorityBoxes[slot];
                combo.BeginUpdate();
                combo.Items.Clear();
                foreach (var preset in presets)
                {
                    combo.Items.Add(new PresetChoice(PresetKey(preset), DisplayName(preset)));
                }

                combo.SelectedIndex = slot < combo.Items.Count ? slot : -1;
                combo.Enabled = combo.Items.Count > slot;
                combo.EndUpdate();
            }
        }
        finally
        {
            syncingPrioritySelectors = false;
        }
    }

    private void PrioritySelectionChanged(GroupPageState state, int slotIndex)
    {
        if (syncingPrioritySelectors || state.PriorityBoxes[slotIndex].SelectedItem is not PresetChoice choice)
        {
            return;
        }

        var sourceIndex = FindPresetRow(state.Grid, choice.Key);
        if (sourceIndex < 0 || sourceIndex == slotIndex || slotIndex >= state.Grid.Rows.Count)
        {
            return;
        }

        var source = state.Grid.Rows[sourceIndex].Tag as PricePreset;
        var target = state.Grid.Rows[slotIndex].Tag as PricePreset;
        if (source is null || target is null)
        {
            return;
        }

        state.Grid.Rows[sourceIndex].Tag = target.Clone();
        state.Grid.Rows[slotIndex].Tag = source.Clone();
        RefreshGroup(state);
        state.Grid.ClearSelection();
        state.Grid.Rows[slotIndex].Selected = true;
        state.Grid.CurrentCell = state.Grid.Rows[slotIndex].Cells["model"];
    }

    private void ToggleLibrary(string group, Control libraryPanel, Control searchBox)
    {
        var state = groupPages[group];
        var expanding = !libraryPanel.Visible;
        libraryPanel.Visible = expanding;
        state.ToggleButton.Text = expanding ? "收起完整价格库" : "管理完整价格库";

        if (expanding)
        {
            if (Height < 740)
            {
                Height = 760;
            }

            searchBox.Focus();
        }
        else if (Height > 640)
        {
            Height = 590;
        }
    }

    private void ApplySearch(GroupPageState state)
    {
        var query = state.SearchBox.Text.Trim();
        var visible = 0;
        state.Grid.CurrentCell = null;
        state.Grid.ClearSelection();
        foreach (DataGridViewRow row in state.Grid.Rows)
        {
            var preset = row.Tag as PricePreset;
            var matches = preset is not null &&
                (query.Length == 0 ||
                 preset.Provider.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 preset.Model.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 preset.Source.Contains(query, StringComparison.OrdinalIgnoreCase));
            row.Visible = matches;
            if (matches)
            {
                visible++;
            }
        }

        state.CountLabel.Text = query.Length == 0
            ? $"共 {state.Grid.Rows.Count} 项"
            : $"找到 {visible} 项";
    }

    private void AddPreset(string group)
    {
        var state = groupPages[group];
        var preset = new PricePreset
        {
            Group = group,
            CurrencySymbol = "$",
            UnitLabel = "USD / 1M tokens",
            Divisor = 1_000_000m
        };
        using var editor = new PricePresetEditorForm(preset, isNew: true);
        if (editor.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var edited = editor.Result;
        edited.Group = group;
        AddPresetRow(state.Grid, edited);
        state.SearchBox.Clear();
        RefreshGroup(state);
        SelectPreset(state, PresetKey(edited));
    }

    private void EditSelectedPreset(GroupPageState state)
    {
        var row = state.Grid.CurrentRow;
        if (row?.Tag is not PricePreset preset)
        {
            MessageBox.Show(this, "请先选择一个价格。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var editor = new PricePresetEditorForm(preset, isNew: false);
        if (editor.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var edited = editor.Result;
        edited.Group = state.Group;
        row.Tag = edited.Clone();
        state.SearchBox.Clear();
        RefreshGroup(state);
        SelectPreset(state, PresetKey(edited));
    }

    private void DeleteSelectedPreset(GroupPageState state)
    {
        var row = state.Grid.CurrentRow;
        if (row?.Tag is not PricePreset preset)
        {
            MessageBox.Show(this, "请先选择一个价格。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (IsBuiltInPreset(state.Group, preset))
        {
            MessageBox.Show(
                this,
                "内置价格不会被删除。你可以编辑它，或恢复当前分组的默认价格。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
                this,
                $"确定删除“{DisplayName(preset)}”吗？",
                Text,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        state.Grid.Rows.Remove(row);
        RefreshGroup(state);
    }

    private static bool IsBuiltInPreset(string group, PricePreset preset)
    {
        return PricePreset.DefaultsForGroup(group).Any(item =>
            string.Equals(item.Provider, preset.Provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Model, preset.Model, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectPreset(GroupPageState state, string key)
    {
        var index = FindPresetRow(state.Grid, key);
        if (index < 0)
        {
            return;
        }

        state.Grid.ClearSelection();
        state.Grid.Rows[index].Selected = true;
        state.Grid.CurrentCell = state.Grid.Rows[index].Cells["model"];
        state.Grid.FirstDisplayedScrollingRowIndex = index;
    }

    private void RestoreCurrentGroupDefaults()
    {
        var group = CurrentGroup();
        if (MessageBox.Show(
                this,
                $"恢复 {group} 分组的默认价格和展示顺序？其他分组不会受到影响。",
                Text,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        var state = groupPages[group];
        state.Grid.Rows.Clear();
        foreach (var preset in PriceSettingsStore.Defaults().PresetsForGroup(group))
        {
            AddPresetRow(state.Grid, preset.Clone());
        }

        state.SearchBox.Clear();
        RefreshGroup(state);
    }

    private void SaveSettings()
    {
        try
        {
            var presets = ReadPresetRows();
            foreach (var group in PricePresetGroups.All)
            {
                if (!presets.Any(item => string.Equals(NormalizeGroupName(item.Group), group, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"{group} 分组至少需要保留一个价格。");
                }
            }

            var settings = PriceSettingsStore.Current.Clone();
            settings.DisplayOrderVersion = PriceSettingsStore.Defaults().DisplayOrderVersion;
            settings.Presets = new();
            foreach (var group in PricePresetGroups.All)
            {
                settings.SetPresetsForGroup(group, presets
                    .Where(item => string.Equals(NormalizeGroupName(item.Group), group, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Clone())
                    .ToList());
            }

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
        return PricePresetGroups.All
            .SelectMany(group => ReadGroupPresets(groupPages[group]))
            .ToList();
    }

    private static List<PricePreset> ReadGroupPresets(GroupPageState state)
    {
        return state.Grid.Rows
            .Cast<DataGridViewRow>()
            .Select(row => row.Tag as PricePreset)
            .Where(preset => preset is not null)
            .Select(preset =>
            {
                var clone = preset!.Clone();
                clone.Group = state.Group;
                return clone;
            })
            .ToList();
    }

    private static void ApplyLegacyProfiles(PriceSettings settings, IReadOnlyList<PricePreset> presets)
    {
        var codex = presets
            .Where(item => string.Equals(NormalizeGroupName(item.Group), PricePresetGroups.Codex, StringComparison.OrdinalIgnoreCase))
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

    private void PriceSettingsForm_KeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Control && eventArgs.KeyCode == Keys.F && groupPages.TryGetValue(CurrentGroup(), out var state))
        {
            if (!state.LibraryPanel.Visible)
            {
                ToggleLibrary(state.Group, state.LibraryPanel, state.SearchBox);
            }

            state.SearchBox.Focus();
            eventArgs.SuppressKeyPress = true;
        }
    }

    private string CurrentGroup()
    {
        return groupTabs.SelectedTab?.Text ?? PricePresetGroups.Codex;
    }

    private static string NormalizeGroupName(string group)
    {
        return PricePresetGroups.Normalize(group);
    }

    private static string DisplayName(PricePreset preset)
    {
        return string.IsNullOrWhiteSpace(preset.Provider)
            ? preset.Model
            : $"{preset.Provider} · {preset.Model}";
    }

    private static string PresetKey(PricePreset preset)
    {
        return string.Join("\u001f", preset.Provider, preset.Model, preset.CurrencySymbol, preset.UnitLabel, preset.Divisor);
    }

    private static int FindPresetRow(DataGridView grid, string key)
    {
        for (var i = 0; i < grid.Rows.Count; i++)
        {
            if (grid.Rows[i].Tag is PricePreset preset && string.Equals(PresetKey(preset), key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record PresetChoice(string Key, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record GroupPageState(
        string Group,
        TableLayoutPanel Root,
        DataGridView Grid,
        ComboBox[] PriorityBoxes,
        TextBox SearchBox,
        Label CountLabel,
        Button ToggleButton,
        Control LibraryPanel);
}

internal sealed class PricePresetEditorForm : Form
{
    private readonly PricePreset original;
    private readonly TextBox providerBox = new();
    private readonly TextBox modelBox = new();
    private readonly ComboBox currencyBox = new();
    private readonly ComboBox unitBox = new();
    private readonly NumericUpDown inputBox = CreatePriceInput();
    private readonly NumericUpDown cachedBox = CreatePriceInput();
    private readonly NumericUpDown outputBox = CreatePriceInput();
    private readonly TextBox sourceBox = new();

    public PricePresetEditorForm(PricePreset preset, bool isNew)
    {
        original = preset.Clone();
        Result = preset.Clone();
        Text = isNew ? "新增价格" : "编辑价格";
        BuildUi();
        LoadPreset(preset);
    }

    public PricePreset Result { get; private set; }

    private void BuildUi()
    {
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(590, 560);
        MinimumSize = new Size(540, 520);
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "价格详情",
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 41, 55),
            Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);

        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 8
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(form, 0, "供应商", providerBox);
        AddField(form, 1, "模型名称 *", modelBox);

        currencyBox.DropDownStyle = ComboBoxStyle.DropDown;
        currencyBox.Items.AddRange(new object[] { "$", "¥", "Credits" });
        AddField(form, 2, "币种", currencyBox);

        unitBox.DropDownStyle = ComboBoxStyle.DropDown;
        unitBox.Items.AddRange(new object[] { "USD / 1M tokens", "CNY / 1M tokens", "Credits / token" });
        AddField(form, 3, "计价单位", unitBox);
        AddField(form, 4, "输入价格", inputBox);
        AddField(form, 5, "缓存输入价格", cachedBox);
        AddField(form, 6, "输出价格", outputBox);
        AddField(form, 7, "价格来源", sourceBox);
        root.Controls.Add(form, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0)
        };
        var saveButton = CreateButton("确定", Color.FromArgb(21, 128, 106), Color.White);
        saveButton.Click += (_, _) => SavePreset();
        actions.Controls.Add(saveButton);
        var cancelButton = CreateButton("取消", Color.FromArgb(229, 234, 242), Color.FromArgb(31, 41, 55));
        cancelButton.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(cancelButton);
        root.Controls.Add(actions, 0, 2);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    private static void AddField(TableLayoutPanel form, int row, string label, Control input)
    {
        form.RowStyles.Add(new RowStyle(SizeType.Percent, 12.5f));
        form.Controls.Add(new Label
        {
            AutoSize = true,
            Text = label,
            ForeColor = Color.FromArgb(55, 65, 81),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 10, 0)
        }, 0, row);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 6, 0, 6);
        form.Controls.Add(input, 1, row);
    }

    private static NumericUpDown CreatePriceInput()
    {
        return new NumericUpDown
        {
            DecimalPlaces = 4,
            ThousandsSeparator = true,
            Maximum = 1_000_000_000m,
            Increment = 0.1m
        };
    }

    private static Button CreateButton(string text, Color backColor, Color foreColor)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(88, 34),
            BackColor = backColor,
            ForeColor = foreColor,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(8, 0, 0, 0)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void LoadPreset(PricePreset preset)
    {
        providerBox.Text = preset.Provider;
        modelBox.Text = preset.Model;
        currencyBox.Text = string.IsNullOrWhiteSpace(preset.CurrencySymbol) ? "$" : preset.CurrencySymbol;
        unitBox.Text = string.IsNullOrWhiteSpace(preset.UnitLabel) ? "USD / 1M tokens" : preset.UnitLabel;
        inputBox.Value = ClampValue(inputBox, preset.UncachedInput);
        cachedBox.Value = ClampValue(cachedBox, preset.CachedInput);
        outputBox.Value = ClampValue(outputBox, preset.Output);
        sourceBox.Text = preset.Source;
    }

    private static decimal ClampValue(NumericUpDown input, decimal value)
    {
        return Math.Min(input.Maximum, Math.Max(input.Minimum, value));
    }

    private void SavePreset()
    {
        if (string.IsNullOrWhiteSpace(modelBox.Text))
        {
            MessageBox.Show(this, "请填写模型名称。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            modelBox.Focus();
            return;
        }

        var unit = string.IsNullOrWhiteSpace(unitBox.Text) ? "USD / 1M tokens" : unitBox.Text.Trim();
        Result = new PricePreset
        {
            Group = original.Group,
            Provider = providerBox.Text.Trim(),
            Model = modelBox.Text.Trim(),
            CurrencySymbol = string.IsNullOrWhiteSpace(currencyBox.Text) ? "$" : currencyBox.Text.Trim(),
            UnitLabel = unit,
            Divisor = InferDivisor(unit, original.Divisor),
            UncachedInput = inputBox.Value,
            CachedInput = cachedBox.Value,
            Output = outputBox.Value,
            Source = sourceBox.Text.Trim()
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private static decimal InferDivisor(string unit, decimal fallback)
    {
        if (unit.Contains("1M", StringComparison.OrdinalIgnoreCase) ||
            unit.Contains("million", StringComparison.OrdinalIgnoreCase) ||
            unit.Contains("百万", StringComparison.OrdinalIgnoreCase))
        {
            return 1_000_000m;
        }

        if (unit.Contains("/ token", StringComparison.OrdinalIgnoreCase) ||
            unit.Contains("每 token", StringComparison.OrdinalIgnoreCase))
        {
            return 1m;
        }

        return fallback > 0 ? fallback : 1_000_000m;
    }
}
