using System.Windows;
using System.Windows.Controls;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private DayTimeZoneSettings dayTimeZone = DayTimeZoneSettings.Load();

    private DateTimeOffset RangeNow(UsageSourceModule module) => DateTimeOffset.UtcNow.ToOffset(
        module.Mode == RangeMode.Day && module.CustomStartLocal is null
            ? dayTimeZone.Offset : CodexUsageReader.BeijingOffset);

    private void InitializeDayTimeZone()
    {
        DayTimeZoneBox.ItemsSource = Enumerable.Range(-48, 105)
            .Select(quarter => new DayTimeZoneOption(quarter * 15)).ToList();
        DayTimeZoneBox.SelectedValuePath = nameof(DayTimeZoneOption.Minutes);
        DayTimeZoneBox.DisplayMemberPath = nameof(DayTimeZoneOption.Label);
        DayTimeZoneBox.SelectedValue = dayTimeZone.OffsetMinutes;
        foreach (var module in usageModules.Values) module.PickerValue = RangeNow(module).Date;
    }

    private async void DayTimeZoneBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressUiEvents.IsSuppressing || DayTimeZoneBox.SelectedValue is not int minutes ||
            dayTimeZone.OffsetMinutes == minutes) return;
        await RunUiActionAsync(async () =>
        {
            var updated = new DayTimeZoneSettings { OffsetMinutes = minutes };
            try { updated.Save(); }
            catch
            {
                using var scope = suppressUiEvents.Begin();
                DayTimeZoneBox.SelectedValue = dayTimeZone.OffsetMinutes;
                throw;
            }
            var previousToday = DateTimeOffset.UtcNow.ToOffset(dayTimeZone.Offset).Date;
            dayTimeZone = updated;
            foreach (var module in usageModules.Values.Where(module => module.Mode == RangeMode.Day && module.CustomStartLocal is null))
            {
                if (module.PickerValue.Date == previousToday) module.PickerValue = RangeNow(module).Date;
                module.ClearDisplay();
            }
            UpdateRangeControls();
            await RefreshUsageAsync();
        });
    }

    private sealed record DayTimeZoneOption(int Minutes)
    {
        public string Label => DayTimeZoneSettings.Format(TimeSpan.FromMinutes(Minutes));
    }
}
