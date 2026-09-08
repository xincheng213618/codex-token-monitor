using System.Windows;
using WpfButton = System.Windows.Controls.Button;
using WpfCalendar = System.Windows.Controls.Calendar;
using WpfCalendarSelectionMode = System.Windows.Controls.CalendarSelectionMode;
using WpfGrid = System.Windows.Controls.Grid;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfRowDefinition = System.Windows.Controls.RowDefinition;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;

namespace CodexTokenMonitor;

internal static class WeekWindowPicker
{
    public static bool TryPickEndDate(Window owner, DateTime currentEndDate, out DateTime selectedEndDate)
    {
        var selected = currentEndDate.Date;

        var dialog = new Window
        {
            Title = "选择7天窗口",
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Width = 520,
            Height = 320,
            FontFamily = owner.FontFamily
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "CanvasBrush");
        dialog.SetResourceReference(Window.ForegroundProperty, "TextBrush");

        var root = new WpfGrid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new WpfRowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new WpfRowDefinition { Height = GridLength.Auto });

        var calendar = new WpfCalendar
        {
            SelectionMode = WpfCalendarSelectionMode.SingleRange,
            DisplayDate = currentEndDate.Date,
            SelectedDate = currentEndDate.Date,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = WpfVerticalAlignment.Center
        };

        var suppressSelection = false;
        void SelectSevenDaysEndingAt(DateTime endDate)
        {
            suppressSelection = true;
            calendar.SelectedDates.Clear();
            calendar.SelectedDates.AddRange(endDate.Date.AddDays(-6), endDate.Date);
            calendar.SelectedDate = endDate.Date;
            selected = endDate.Date;
            suppressSelection = false;
        }

        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (suppressSelection || calendar.SelectedDates.Count == 0)
            {
                return;
            }

            SelectSevenDaysEndingAt(calendar.SelectedDates.Cast<DateTime>().Max());
        };
        SelectSevenDaysEndingAt(currentEndDate.Date);
        WpfGrid.SetRow(calendar, 0);
        root.Children.Add(calendar);

        var actions = new WpfStackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var okButton = new WpfButton
        {
            Content = "确定",
            Width = 84,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };
        okButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        okButton.Click += (_, _) =>
        {
            dialog.DialogResult = true;
            dialog.Close();
        };
        var cancelButton = new WpfButton
        {
            Content = "取消",
            Width = 84,
            Height = 32
        };
        cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "ToolButton");
        cancelButton.Click += (_, _) =>
        {
            dialog.DialogResult = false;
            dialog.Close();
        };
        actions.Children.Add(okButton);
        actions.Children.Add(cancelButton);
        WpfGrid.SetRow(actions, 1);
        root.Children.Add(actions);

        dialog.Content = root;
        var accepted = dialog.ShowDialog() == true;
        selectedEndDate = selected;
        return accepted;
    }
}
