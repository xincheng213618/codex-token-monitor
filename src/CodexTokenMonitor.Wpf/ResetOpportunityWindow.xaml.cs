using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace CodexTokenMonitor;

internal partial class ResetOpportunityWindow : Window
{
    private readonly ObservableCollection<ResetOpportunityRow> rows = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private bool isClosed;

    public ResetOpportunityWindow()
    {
        InitializeComponent();
        ResetGrid.ItemsSource = rows;
        rows.CollectionChanged += (_, args) =>
        {
            if (args.OldItems is not null)
                foreach (ResetOpportunityRow row in args.OldItems) row.PropertyChanged -= RowPropertyChanged;
            if (args.NewItems is not null)
                foreach (ResetOpportunityRow row in args.NewItems) row.PropertyChanged += RowPropertyChanged;
            UpdateRecordSummary();
        };
        ResetGrid.SelectionChanged += (_, _) => UpdateRecordSummary();
        LoadRows(ResetOpportunityStore.Load());
        Closed += (_, _) =>
        {
            isClosed = true;
            lifetimeCancellation.Cancel();
        };
    }

    private void LoadRows(IReadOnlyList<ResetOpportunityRecord> records)
    {
        rows.Clear();
        foreach (var record in records.OrderBy(item => item.GrantedLocal))
        {
            rows.Add(new ResetOpportunityRow
            {
                Id = record.Id,
                ExpiresText = FormatLocal(record.ExpiresLocal),
                GrantedText = FormatLocal(record.GrantedLocal),
                IsUsed = record.IsUsed,
                Note = record.Note
            });
        }
        UpdateRecordSummary();
        StatusText.Text = "手动修改在保存后生效。";
    }

    private void RowPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateRecordSummary();

    private void UpdateRecordSummary()
    {
        var records = rows.Where(row =>
            !string.IsNullOrWhiteSpace(row.GrantedText) || !string.IsNullOrWhiteSpace(row.ExpiresText) ||
            row.IsUsed || !string.IsNullOrWhiteSpace(row.Note)).ToArray();
        var now = BeijingClock.Now;
        var expiryRows = records.Select(row => (Row: row, Expires: ReadExpiryForDisplay(row))).ToArray();
        var available = expiryRows.Where(item => !item.Row.IsUsed && item.Expires > now).ToArray();
        var nextExpiry = available.Select(item => item.Expires).OrderBy(value => value).FirstOrDefault();
        var used = records.Count(row => row.IsUsed);
        var expired = expiryRows.Count(item => !item.Row.IsUsed && item.Expires <= now);
        AvailableCountText.Text = $"{available.Length:N0} 张";
        ExpirySummaryText.Text = nextExpiry is { } expiry ? expiry.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "暂无";
        ExpirySummaryText.ToolTip = nextExpiry is { } fullExpiry ? FormatLocal(fullExpiry) : "没有待过期的可用重置卡";
        ResetRecordCountText.Text = $"{records.Length:N0} 条 · {used:N0} 已用 · {expired:N0} 过期";
        ResetRecordCountText.ToolTip = ResetRecordCountText.Text;
        ResetEmptyState.Visibility = records.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteResetButton.IsEnabled = ResetGrid.SelectedItems.OfType<ResetOpportunityRow>().Any();
    }

    private static DateTimeOffset? ReadExpiryForDisplay(ResetOpportunityRow row)
    {
        try
        {
            if (!TryParseLocal(row.GrantedText, out var granted)) return null;
            if (string.IsNullOrWhiteSpace(row.ExpiresText))
                return granted.AddDays(30);
            return TryParseLocal(row.ExpiresText, out var value) && value > granted ? value : null;
        }
        catch (ArgumentException) { return null; }
    }

    private void AddTodayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitEdits()) return;
        var now = BeijingClock.Now;
        var row = new ResetOpportunityRow
        {
            GrantedText = FormatLocal(now),
            ExpiresText = FormatLocal(now.AddDays(30)),
            Note = "手动新增"
        };
        rows.Add(row);
        ResetGrid.SelectedItem = row;
        ResetGrid.ScrollIntoView(row);
        ResetGrid.Focus();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitEdits()) return;
        foreach (var row in ResetGrid.SelectedItems.OfType<ResetOpportunityRow>().ToArray())
            rows.Remove(row);
        StatusText.Text = "删除将在保存后生效。";
    }

    private void DefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitEdits()) return;
        LoadRows(ResetOpportunityStore.Defaults());
        StatusText.Text = "已载入示例，点击保存后生效。";
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitEdits()) return;
        ResetEditor.IsEnabled = false;
        SaveButton.IsEnabled = false;
        StatusText.Text = "正在从 Codex 同步…";
        try
        {
            var result = await ResetOpportunityStore.SyncFromCodexAsync(lifetimeCancellation.Token);
            if (isClosed) return;
            if (result.Success) LoadRows(result.Records);
            StatusText.Text = result.Message;
            if (!result.Success) ShowValidation(result.Message);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested || isClosed)
        {
        }
        catch (Exception ex)
        {
            if (!isClosed) ShowValidation(ex.Message);
        }
        finally
        {
            if (!isClosed)
            {
                ResetEditor.IsEnabled = true;
                SaveButton.IsEnabled = true;
            }
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitEdits()) return;
        try
        {
            var records = new List<ResetOpportunityRecord>();
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                if (string.IsNullOrWhiteSpace(row.GrantedText) && string.IsNullOrWhiteSpace(row.ExpiresText) &&
                    !row.IsUsed && string.IsNullOrWhiteSpace(row.Note)) continue;

                if (!TryParseLocal(row.GrantedText, out var granted))
                    throw new InvalidOperationException($"第 {index + 1} 行的获得时间格式不正确，请使用 yyyy-MM-dd HH:mm。");
                var expires = granted.AddDays(30);
                if (!string.IsNullOrWhiteSpace(row.ExpiresText) && !TryParseLocal(row.ExpiresText, out expires))
                    throw new InvalidOperationException($"第 {index + 1} 行的过期时间格式不正确，请使用 yyyy-MM-dd HH:mm。");
                if (expires <= granted)
                    throw new InvalidOperationException($"第 {index + 1} 行的过期时间必须晚于获得时间。");

                records.Add(new ResetOpportunityRecord
                {
                    Id = row.Id,
                    GrantedLocal = granted,
                    ExpiresLocal = expires,
                    IsUsed = row.IsUsed,
                    Note = row.Note?.Trim() ?? ""
                });
            }
            ResetOpportunityStore.Save(records);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowValidation(ex.Message);
        }
    }

    private bool CommitEdits()
    {
        if (ResetGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
            ResetGrid.CommitEdit(DataGridEditingUnit.Row, true)) return true;
        ShowValidation("请先完成当前单元格的编辑。");
        return false;
    }

    private void ShowValidation(string message)
    {
        StatusText.Text = message;
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string FormatLocal(DateTimeOffset value) =>
        value.ToOffset(CodexUsageReader.BeijingOffset).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static bool TryParseLocal(string? text, out DateTimeOffset value)
    {
        value = default;
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dateTime))
            return false;
        value = new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified), CodexUsageReader.BeijingOffset);
        return true;
    }

    public sealed class ResetOpportunityRow : INotifyPropertyChanged
    {
        private string grantedText = "";
        private string expiresText = "";
        private bool isUsed;
        private string note = "";
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string GrantedText
        {
            get => grantedText;
            set
            {
                grantedText = value;
                OnPropertyChanged();
                if (string.IsNullOrWhiteSpace(ExpiresText) && TryParseLocal(value, out var granted))
                    ExpiresText = FormatLocal(granted.AddDays(30));
            }
        }
        public string ExpiresText
        {
            get => expiresText;
            set { expiresText = value; OnPropertyChanged(); }
        }
        public bool IsUsed
        {
            get => isUsed;
            set { isUsed = value; OnPropertyChanged(); }
        }
        public string Note
        {
            get => note;
            set { note = value; OnPropertyChanged(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
