namespace CodexTokenMonitor;

// A stable reference for comparing included subscription cycles. This is not
// the API rate card or an assertion that the subscription has a dollar balance.
internal static class CodexSubscriptionPricing
{
    public const string Description = "订阅额度采用固定折算基准：Sol 输入 $5 / 缓存 $0.50 / 输出 $30（每百万 Token）；" +
        "不随 API 促销降价变化。其他模型沿用价格库参考价。GPT-6.2 的缓存写入按未缓存输入折算，提速档另乘倍率。" +
        "折算金额用于比较周期，不是官方美元余额。";

    public static PriceProfile GetProfile(string modelId, PriceProfile apiProfile)
    {
        return CodexModelCost.ModelKey(modelId) switch
        {
            "gpt-5.6-sol" or "gpt-5.6" => new PriceProfile("Sol 订阅额度参考", "$", 5m, .5m, 30m, 1_000_000m,
                CacheWriteInputPerMillion: 6.25m),
            // https://learn.chatgpt.com/docs/pricing#token-rates
            "gpt-6.2-sol" or "gpt-6.2-astra" => apiProfile with { CacheWriteInputPerMillion = apiProfile.UncachedInputPerMillion },
            _ => apiProfile
        };
    }
}
