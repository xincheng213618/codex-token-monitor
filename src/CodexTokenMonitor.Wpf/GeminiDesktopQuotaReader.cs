using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace CodexTokenMonitor;

internal static class GeminiDesktopQuotaReader
{
    private static readonly object Gate = new();
    private static readonly Condition GroupHeading = new OrCondition(GeminiDesktopQuotaParser.GroupNames
        .Select(name => (Condition)new PropertyCondition(AutomationElement.NameProperty, name))
        .ToArray());
    private static readonly Condition TextElement = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
    private static DateTimeOffset nextRead;

    public static GeminiRateLimitSnapshot? Synchronize(bool force = false) =>
        Synchronize(force, out _);

    public static GeminiRateLimitSnapshot? Synchronize(bool force, out double? unanchoredResetRemainingPercent)
    {
        unanchoredResetRemainingPercent = null;
        if (UsageLogPaths.GetOverrideRoot(UsageSource.Gemini) is not null) return null;
        lock (Gate)
        {
            var now = BeijingClock.Now;
            if (!force && now < nextRead) return null;
            nextRead = now.AddSeconds(30);
            var previous = GeminiRateLimitLog.ReadLatest(now);
            var processes = Process.GetProcessesByName("Antigravity");
            try
            {
                foreach (var process in processes)
                {
                    if (ReadWindow(process, now, previous, out var detectedRemaining) is not { } result)
                    {
                        if (detectedRemaining is not null)
                            unanchoredResetRemainingPercent ??= detectedRemaining;
                        continue;
                    }
                    if (result.FiveHour is { } five && five != previous?.FiveHour ||
                        result.SevenDay is { } week && week != previous?.SevenDay)
                        GeminiRateLimitLog.SaveCurrent(result);
                    return result;
                }
                return null;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
    }

    private static GeminiRateLimitSnapshot? ReadWindow(
        Process process,
        DateTimeOffset now,
        GeminiRateLimitSnapshot? previous,
        out double? unanchoredResetRemainingPercent)
    {
        unanchoredResetRemainingPercent = null;
        try
        {
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero) return null;

            var root = AutomationElement.FromHandle(handle);
            foreach (AutomationElement heading in root.FindAll(TreeScope.Descendants, GroupHeading))
            {
                if (heading.Current.IsOffscreen) continue;
                var section = heading;
                for (var depth = 0; depth < 4 && section is not null; depth++)
                {
                    var text = section.FindAll(TreeScope.Descendants, TextElement)
                        .Cast<AutomationElement>().Select(element => element.Current.Name).ToArray();
                    if (GeminiDesktopQuotaParser.Parse(text, now, previous) is { } result)
                        return result;
                    if (unanchoredResetRemainingPercent is null &&
                        GeminiDesktopQuotaParser.TryDetectUnanchoredWeekReset(text, previous, out var rem))
                    {
                        unanchoredResetRemainingPercent = rem;
                    }
                    section = TreeWalker.ControlViewWalker.GetParent(section);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or
                                   COMException or UnauthorizedAccessException)
        {
            // The desktop window or quota panel can close during enumeration.
        }
        return null;
    }
}
