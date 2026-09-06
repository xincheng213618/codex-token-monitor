using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

// A stable reference for comparing included subscription cycles. This is not
// the API rate card or an assertion that the subscription has a dollar balance.
internal static class CodexSubscriptionPricing
{
    public const string Description = "订阅额度采用固定折算基准：Sol 输入 $5 / 缓存 $0.50 / 输出 $30（每百万 Token）；" +
        "不随 API 促销降价变化。其他模型沿用价格库参考价，Fast 另乘倍率。折算金额用于比较周期，不是官方美元余额。";

    public static PriceProfile GetProfile(string modelId, PriceProfile apiProfile)
    {
        var model = Regex.Replace(CodexModelCost.NormalizeModelId(modelId), @"-\d{4}-\d{2}-\d{2}$", "");
        return model is "gpt-5.6-sol" or "gpt-5.6"
            ? new PriceProfile("Sol 订阅额度参考", "$", 5m, .5m, 30m, 1_000_000m,
                CacheWriteInputPerMillion: 6.25m)
            : apiProfile;
    }
}
