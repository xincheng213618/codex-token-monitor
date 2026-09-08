using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace CodexTokenMonitor;

internal partial class SubscriptionPlanWindow : Window
{
    private readonly ObservableCollection<SubscriptionPlanRow> rows = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private bool isClosed;

    public SubscriptionPlanWindow()
    {
        InitializeComponent();
        PlansGrid.ItemsSource = rows;
        rows.CollectionChanged += (_, args) =>
        {
            if (args.OldItems is not null)
                foreach (SubscriptionPlanRow row in args.OldItems) row.PropertyChanged -= RowPropertyChanged;
            if (args.NewItems is not null)
                foreach (SubscriptionPlanRow row in args.NewItems) row.PropertyChanged += RowPropertyChanged;
            UpdateRecordSummary();
        };
        PlansGrid.SelectionChanged += (_, _) => UpdateRecordSummary();
        MonthlyPlanBox.ItemsSource = new[] { "Pro 20x", "Pro 5x", "Plus", "Pro", "Business" };
        LoadRows(SubscriptionPlanStore.Load());
        Loaded += async (_, _) => await ReadAccountPlanAsync();
        Closed += (_, _) =>
        {
            isClosed = true;
            lifetimeCancellation.Cancel();
        };
    }

    private void LoadRows(IReadOnlyList<SubscriptionPlanRecord> records)
    {
        rows.Clear();
        foreach (var record in records.OrderBy(item => item.StartLocal))
        {
            rows.Add(new SubscriptionPlanRow
            {
                Id = record.Id,
                StartText = FormatLocal(record.StartLocal),
                EndText = FormatLocal(record.EndLocal),
                PlanName = record.PlanName,
                AmountText = record.AmountCny.ToString("0.##", CultureInfo.InvariantCulture)
            });
        }
        var latest = records.MaxBy(item => item.EndLocal);
        var now = BeijingClock.Now;
        MonthStartPicker.Value = latest?.EndLocal.ToOffset(CodexUsageReader.BeijingOffset).DateTime ?? new DateTime(now.Year, now.Month, 2);
        MonthlyPlanBox.Text = latest?.PlanName ?? "Pro 20x";
        MonthlyAmountBox.Text = Math.Clamp(latest?.AmountCny ?? 1380m, 0m, 1_000_000m).ToString("0.##", CultureInfo.InvariantCulture);
        UpdateRecordSummary();
    }

    private void RowPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateRecordSummary();

    private void UpdateRecordSummary()
    {
        var records = rows.Where(row =>
            !string.IsNullOrWhiteSpace(row.StartText) || !string.IsNullOrWhiteSpace(row.EndText) ||
            !string.IsNullOrWhiteSpace(row.PlanName) || !string.IsNullOrWhiteSpace(row.AmountText)).ToArray();
        var current = new List<(SubscriptionPlanRow Row, DateTimeOffset Start, DateTimeOffset End)>();
        var now = BeijingClock.Now;
        foreach (var row in records)
        {
            if (TryReadPeriodForDisplay(row, out var start, out var end) && start <= now && end > now)
                current.Add((row, start, end));
        }
        CurrentPlanText.Text = current.Count == 0
            ? "暂无生效套餐"
            : "当前 · " + string.Join(" / ", current.Select(item => string.IsNullOrWhiteSpace(item.Row.PlanName) ? "未命名套餐" : item.Row.PlanName.Trim()).Distinct());
        if (current.Count == 1)
        {
            var item = current[0];
            var cost = decimal.TryParse(item.Row.AmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount >= 0
                ? $" · ¥{amount:0.##}" : "";
            CurrentPlanDetailText.Text = $"{item.Start:MM-dd}–{item.End:MM-dd}{cost}";
        }
        else
        {
            CurrentPlanDetailText.Text = current.Count > 1 ? $"当前有 {current.Count:N0} 条生效记录" : "可在下方添加购买记录";
        }
        CurrentPlanText.ToolTip = CurrentPlanText.Text;
        CurrentPlanDetailText.ToolTip = CurrentPlanDetailText.Text;
        PlanRecordCountText.Text = $"{records.Length:N0} 条";
        PlansEmptyState.Visibility = records.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeletePlanButton.IsEnabled = PlansGrid.SelectedItems.OfType<SubscriptionPlanRow>().Any();
    }

    private static bool TryReadPeriodForDisplay(SubscriptionPlanRow row, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = end = default;
        try
        {
            start = ParseLocal(row.StartText, 1, "开始");
            end = string.IsNullOrWhiteSpace(row.EndText) ? start.AddMonths(1) : ParseLocal(row.EndText, 1, "结束");
            return end > start;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private void AddMonthlyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitEdits()) return;
        try
        {
            if (MonthStartPicker.Value is not { } startValue)
                throw new InvalidOperationException("请选择套餐开始时间。");
            if (!decimal.TryParse(MonthlyAmountBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ||
                amount < 0 || amount > 1_000_000m)
                throw new InvalidOperationException("月费必须是 0 到 1,000,000 之间的数字。");
            var records = ReadRows();
            var start = new DateTimeOffset(DateTime.SpecifyKind(startValue, DateTimeKind.Unspecified), CodexUsageReader.BeijingOffset);
            var record = SubscriptionPlanStore.CreateMonthly(start, MonthlyPlanBox.Text, amount);
            if (records.Any(item => item.StartLocal < record.EndLocal && item.EndLocal > record.StartLocal))
                throw new InvalidOperationException("这个月与已有套餐重叠，请调整开始时间，或直接修改下方记录。");
            records.Add(record);
            LoadRows(records);
            var addedRow = rows.First(item => item.Id == record.Id);
            PlansGrid.SelectedItem = addedRow;
            PlansGrid.ScrollIntoView(addedRow);
            StatusText.Text = "已添加一个月，点击保存后生效。";
        }
        catch (Exception ex)
        {
            ShowValidation(ex.Message);
        }
    }

    private async Task ReadAccountPlanAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var plan = await Task.Run(() => CodexAppServerQuotaReader.ReadPlanTypeAsync(timeout.Token), timeout.Token);
            if (isClosed) return;
            AccountStatusText.Text = plan is null
                ? "未读取到账户套餐；已沿用购买记录，默认 Pro 20x / ¥1380。"
                : $"账户套餐：{plan}。具体倍率、人民币月费和付款日期沿用购买记录，可在下方修改。";
            // A generic 'pro' response cannot distinguish the paid 5x and 20x tiers.
            if (plan is not null && !MonthlyPlanBox.Text.StartsWith(plan, StringComparison.OrdinalIgnoreCase))
                MonthlyPlanBox.Text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan.Replace('_', ' '));
        }
        catch (OperationCanceledException)
        {
            if (!isClosed) AccountStatusText.Text = "账户读取超时；仍可直接使用上次套餐和月费添加一个月。";
        }
        catch (Exception)
        {
            if (!isClosed) AccountStatusText.Text = "暂时无法读取账户套餐；已沿用上次购买记录。";
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitEdits()) return;
        foreach (var row in PlansGrid.SelectedItems.OfType<SubscriptionPlanRow>().ToArray())
            rows.Remove(row);
        StatusText.Text = "删除将在保存后生效。";
    }

    private void DefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitEdits()) return;
        LoadRows(SubscriptionPlanStore.Defaults());
        StatusText.Text = "已载入示例，点击保存后生效。";
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitEdits()) return;
        try
        {
            var current = ReadRows();
            var import = SubscriptionPlanImporter.TryImportFromCodex();
            LoadRows(MergeRows(current, import.Records));
            StatusText.Text = $"{import.Message} 点击保存后生效。";
            MessageBox.Show(this, $"{import.Message}\n\n导入结果仅在当前窗口预览，点击“保存”后才会生效。",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowValidation(ex.Message);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitEdits()) return;
        try
        {
            SubscriptionPlanStore.Save(ReadRows());
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowValidation(ex.Message);
        }
    }

    private List<SubscriptionPlanRecord> ReadRows()
    {
        var records = new List<SubscriptionPlanRecord>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (string.IsNullOrWhiteSpace(row.StartText) && string.IsNullOrWhiteSpace(row.EndText) &&
                string.IsNullOrWhiteSpace(row.PlanName) && string.IsNullOrWhiteSpace(row.AmountText)) continue;
            var start = ParseLocal(row.StartText, index + 1, "开始");
            var end = string.IsNullOrWhiteSpace(row.EndText) ? start.AddMonths(1) : ParseLocal(row.EndText, index + 1, "结束");
            if (end <= start)
                throw new InvalidOperationException($"第 {index + 1} 行的套餐结束时间必须晚于开始时间。");
            if (!decimal.TryParse(string.IsNullOrWhiteSpace(row.AmountText) ? "0" : row.AmountText,
                    NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount < 0)
                throw new InvalidOperationException($"第 {index + 1} 行的金额必须是大于等于 0 的数字。");
            records.Add(new SubscriptionPlanRecord
            {
                Id = row.Id,
                StartLocal = start,
                EndLocal = end,
                PlanName = string.IsNullOrWhiteSpace(row.PlanName) ? "未命名套餐" : row.PlanName.Trim(),
                AmountCny = amount
            });
        }
        return records;
    }

    private bool CommitEdits()
    {
        if (PlansGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
            PlansGrid.CommitEdit(DataGridEditingUnit.Row, true)) return true;
        ShowValidation("请先完成当前单元格的编辑。");
        return false;
    }

    private void ShowValidation(string message)
    {
        StatusText.Text = message;
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static IReadOnlyList<SubscriptionPlanRecord> MergeRows(
        IEnumerable<SubscriptionPlanRecord> current, IEnumerable<SubscriptionPlanRecord> imported) =>
        current.Concat(imported)
            .GroupBy(item => $"{item.StartLocal:O}|{item.EndLocal:O}|{item.PlanName}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.AmountCny).First())
            .OrderBy(item => item.StartLocal)
            .ToList();

    private static string FormatLocal(DateTimeOffset value) =>
        value.ToOffset(CodexUsageReader.BeijingOffset).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseLocal(string? text, int row, string field)
    {
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var value))
            throw new InvalidOperationException($"第 {row} 行的{field}时间格式不正确，请使用 yyyy-MM-dd HH:mm。");
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), CodexUsageReader.BeijingOffset);
    }

    public sealed class SubscriptionPlanRow : INotifyPropertyChanged
    {
        private string startText = "";
        private string endText = "";
        private string planName = "";
        private string amountText = "";
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string StartText
        {
            get => startText;
            set { startText = value; OnPropertyChanged(); }
        }
        public string EndText
        {
            get => endText;
            set { endText = value; OnPropertyChanged(); }
        }
        public string PlanName
        {
            get => planName;
            set { planName = value; OnPropertyChanged(); }
        }
        public string AmountText
        {
            get => amountText;
            set { amountText = value; OnPropertyChanged(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
