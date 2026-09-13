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
    private readonly AnalysisQuerySession querySession;
    private bool isClosed;
    private bool hasLoadedSettings;
    private bool hasSuccessfulLoad;
    private bool isLoading;
    private bool isSaving;
    private bool isSyncing;

    public ResetOpportunityWindow(MonitorRuntime? runtime = null)
    {
        querySession = new AnalysisQuerySession(runtime);
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
        UpdateEditingState();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            isClosed = true;
            lifetimeCancellation.Cancel();
            querySession.Dispose();
        };
    }

    private async Task LoadAsync()
    {
        if (isLoading || isSaving || isSyncing || querySession.IsStopping) return;
        isLoading = true;
        hasLoadedSettings = false;
        UpdateEditingState();
        SetStatus("正在读取重置机会设置…");
        try
        {
            var result = await querySession.RunAsync("读取重置机会设置", _ => ResetOpportunityStore.Load(forceReload: true));
            if (isClosed || querySession.IsStopping) return;
            if (result.CacheWarnings.Count > 0)
            {
                ShowLoadFailure(CacheFailureText.Detail(result.CacheWarnings));
                return;
            }
            LoadRows(result.Value);
            hasLoadedSettings = true;
            hasSuccessfulLoad = true;
            SetStatus("同步会立即保存；手动修改在点击保存后生效。");
        }
        catch (OperationCanceledException) when (querySession.IsStopping || isClosed) { }
        catch (Exception ex)
        {
            if (!isClosed && !querySession.IsStopping)
                ShowLoadFailure(ex.ToString());
        }
        finally
        {
            isLoading = false;
            if (!isClosed && !querySession.IsStopping) UpdateEditingState();
        }
    }

    private async void RetryLoadButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private bool CanEdit => hasLoadedSettings && !isLoading && !isSaving && !isSyncing && !querySession.IsStopping && !isClosed;

    private void UpdateEditingState()
    {
        ResetEditor.IsEnabled = CanEdit;
        SaveButton.IsEnabled = CanEdit;
        RetryLoadButton.Visibility = hasLoadedSettings ? Visibility.Collapsed : Visibility.Visible;
        RetryLoadButton.IsEnabled = !isLoading && !isSaving && !isSyncing && !querySession.IsStopping;
    }

    private void SetStatus(string text, string? detail = null)
    {
        StatusText.Text = text;
        StatusText.ToolTip = detail;
    }

    private void ShowLoadFailure(string detail)
    {
        SetStatus("重置机会设置读取失败，编辑和保存已停用。修复后点击“重新读取”。", detail);
        if (hasSuccessfulLoad) return;
        AvailableCountText.Text = "暂不可用";
        ExpirySummaryText.Text = "读取失败";
        ExpirySummaryText.ToolTip = detail;
        ResetRecordCountText.Text = "读取失败";
        ResetRecordCountText.ToolTip = detail;
        ResetEmptyState.Visibility = Visibility.Collapsed;
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
        if (!CanEdit) return;
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
        if (!CanEdit) return;
        if (!CommitEdits()) return;
        foreach (var row in ResetGrid.SelectedItems.OfType<ResetOpportunityRow>().ToArray())
            rows.Remove(row);
        StatusText.Text = "删除将在保存后生效。";
    }

    private void DefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit) return;
        if (!CommitEdits()) return;
        LoadRows(ResetOpportunityStore.Defaults());
        StatusText.Text = "已载入示例，点击保存后生效。";
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit) return;
        if (!CommitEdits()) return;
        isSyncing = true;
        UpdateEditingState();
        SetStatus("正在从 Codex 同步…");
        try
        {
            var result = await querySession.Runtime.Run("设置窗口同步重置机会", async runtimeToken =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(runtimeToken, lifetimeCancellation.Token);
                return await Task.Run(() => ResetOpportunityStore.SyncFromCodexAsync(linked.Token), linked.Token);
            });
            if (isClosed || querySession.IsStopping) return;
            if (result.Success) LoadRows(result.Records);
            SetStatus(result.Success ? result.Message : $"同步失败，已保留当前编辑。{result.Message}", result.Success ? null : result.Message);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested || isClosed || querySession.IsStopping)
        {
        }
        catch (Exception ex)
        {
            if (!isClosed && !querySession.IsStopping)
                SetStatus("同步失败，已保留当前编辑。", ex.ToString());
        }
        finally
        {
            isSyncing = false;
            if (!isClosed && !querySession.IsStopping) UpdateEditingState();
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit) return;
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
            isSaving = true;
            UpdateEditingState();
            SetStatus("正在保存重置机会设置…");
            await querySession.RunAsync("保存重置机会设置", _ =>
            {
                ResetOpportunityStore.Save(records);
                return true;
            }, requiresSharedIo: true);
            if (isClosed || querySession.IsStopping) return;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (querySession.IsStopping || isClosed) { }
        catch (Exception ex)
        {
            if (!isClosed && !querySession.IsStopping)
            {
                if (isSaving)
                    SetStatus("保存失败，已保留当前编辑。修复后可再次点击保存。", ex.ToString());
                else
                    ShowValidation(ex.Message);
            }
        }
        finally
        {
            isSaving = false;
            if (!isClosed && !querySession.IsStopping) UpdateEditingState();
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
