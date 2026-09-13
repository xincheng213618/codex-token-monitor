using System.Windows;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private void ShowCacheWarnings(UsageSourceModule module, IReadOnlyList<CacheWarning> warnings)
    {
        if (isClosed || warnings.Count == 0) return;

        ShowUsageReadFailure(module, CacheFailureText.Reason(warnings), CacheFailureText.Detail(warnings));
    }

    private void ShowUsageReadFailure(UsageSourceModule module, string reason, string? detail)
    {
        if (isClosed) return;
        if (!module.TryGetDisplay(out _, out _)) ApplyEmptyModuleState(module);
        displayViewModel.ShowReadFailure(reason, detail);
    }
}
