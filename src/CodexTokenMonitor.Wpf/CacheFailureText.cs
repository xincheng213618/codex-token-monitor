namespace CodexTokenMonitor;

internal static class CacheFailureText
{
    public static string Summary(IReadOnlyList<CacheWarning> warnings, bool hasPreviousResult) =>
        $"读取失败：{Reason(warnings)}；" + (hasPreviousResult
            ? "当前显示上次成功结果，恢复缓存后可重试。"
            : "本次统计暂不可用，恢复缓存后可重试。");

    public static string Reason(IReadOnlyList<CacheWarning> warnings) => warnings.FirstOrDefault()?.Kind switch
    {
        CacheWarningKind.Locked => "缓存正被占用",
        CacheWarningKind.Corrupt => "缓存文件损坏",
        CacheWarningKind.AccessDenied => "无法访问缓存文件",
        _ => "缓存读写失败"
    };

    public static string Detail(IReadOnlyList<CacheWarning> warnings) =>
        string.Join(Environment.NewLine, warnings.Select(warning =>
            $"{warning.Operation}: {warning.Path}{Environment.NewLine}{warning.Message}"));
}
