# 真实价格参考估算

Codex / ZCode 的汇总区在“实际模型 · 标准 API 等价”后展示“真实价格（估算）”。它计算当前统计范围的参考价值，不是付款记录。

- 数据：Real API Pricing 的 2026-09-27 快照，固定提交 `c79088522db653986becfc42a892a7dafa62c6eb`，共 53 个 OpenAI / Zhipu 订阅套餐与模型组合。嵌入程序，运行时不依赖本地网站或联网。
- Codex：优先按当前套餐设置的 Plus、Pro 5x、Pro 20x 匹配；不存在该套餐模型样本时，采用同模型已收录的最高真实单价，并在说明中标明“跨套餐参考”。套餐未识别时也取最高参考价，不猜测 Pro 档位。
- ZCode：免费用量按网站 Zhipu / GLM Coding Plan 中每个模型的最高真实单价折算。最高指单位 Token 价格，不是套餐名 Max。保留源表的标准负载假设；结果不代表免费账户产生了支出。
- 公式：逐模型 `TotalTokens / 1,000,000 × 参考单价`，再求和。包含缓存与输出，不重复加上 reasoning。人民币单价由原币月费与源表月 Token 计算，避免往返汇率舍入。
- 未匹配模型或缺模型记录不当作免费：显示部分金额及 `*`，全部未覆盖显示“暂无数据”。悬停可看每个模型的价格、套餐、置信度、来源日期及未覆盖量；复制摘要也保留这些说明。
- 使用同一参考快照重估所选历史用量；不会根据额外重置或 Fast 自动调整参考单价，也不会修改标准 API 价库。

上游及逐项证据：https://github.com/FeiZhuLulu/real-api-pricing

保留上游 [MIT 许可](third-party/real-api-pricing/LICENSE.txt) 与 [来源声明](third-party/real-api-pricing/SOURCES.md)。其中 awesome-coding-plan 数据的作者标识为 mahonzhan@gmail.com，按 CC BY 4.0 署名；本次提取 OpenAI / Zhipu 数值、保留来源描述并新增人民币单价换算，不代表原作者认可本软件的估算。

更新数据时先在上游副本执行 `npm run data`，再在本项目 PowerShell 中执行：

```powershell
.\scripts\Import-RealApiPricing.ps1 -RepositoryPath 'C:\Users\17917\Desktop\real-api-pricing'
```
