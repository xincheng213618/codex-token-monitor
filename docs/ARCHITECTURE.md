# Codex Token Monitor 架构说明

本文面向想了解或修改本项目的开发者。文档中的类名均为源码中的实际命名，便于对照阅读。

## 1. 项目分层

```text
CodexTokenMonitor.slnx
├─ src/
│  ├─ CodexTokenMonitor.Core/        # 无 UI 的核心逻辑（net8.0）
│  │   ├─ 日志读取：CodexUsageReader、ClaudeUsageReader、ZCodeUsageReader、
│  │   │            WorkBuddyUsageReader、LiveFileTailReader
│  │   ├─ 额度读取：CodexAppServerQuotaReader、CodexCliLocator
│  │   ├─ 额度分析：CodexQuotaCycle、QuotaEstimateCalculator、QuotaPace、
│  │   │            QuotaSnapshotLookup、QuotaFreshness
│  │   ├─ 统计聚合：UsageSourceReader、UsageModules、UsageBreakdownBuilder、
│  │   │            UsageModels（TokenUsageBucket/Summary/Event）
│  │   ├─ 设置存储：PriceSettings、SubscriptionPlans、SubscriptionPlanImporter、
│  │   │            ResetOpportunities
│  │   └─ 数据交换：CodexDataTransferService
│  ├─ CodexTokenMonitor.Wpf/        # WPF 界面（net8.0-windows10.0.19041.0）
│  │   ├─ MainWindow、QuotaEstimateWindow、QuotaCostCurveWindow、CacheDetailsWindow
│  │   ├─ WpfTokenTimelineControl、QuotaCostCurveControl、QuotaCostCurveCalculator
│  │   ├─ BackgroundCacheWarmer、LastDisplayStore、WeekWindowPicker
│  │   └─ 价格设置窗口（PriceSettingsWindow、PricePresetEditorWindow）
│  └─ CodexTokenMonitor.LegacyDialogs/   # 被 WPF 编译包含的 WinForms 对话框
│       └─ ResetOpportunityForm、SubscriptionPlanForm
└─ tests/CodexTokenMonitor.Core.Tests/   # xUnit 核心回归测试
```

依赖关系：`Wpf → Core`，`Wpf` 通过 `Compile Include` 把 `LegacyDialogs` 的两个窗体直接编入。

## 2. 数据源与读取

### 2.1 五种来源

`UsageSource` 枚举：`Codex`、`ClaudeCode`、`ZCode`、`WorkBuddy`、`Dsh`。每种来源由 `IUsageSourceReader` 实现包装，对外暴露统一接口（`ReadRange`、`ReadDetailRows`、`ReadCachedRange`、`RefreshCachedDay`、`ClearCache` 等）。`UsageSourceReaders.For(source)` 按来源取单例。

只有 Codex 支持额度（`SupportsQuota == true`）。

### 2.2 Codex 日志读取（CodexUsageReader）

输入是 `~/.codex/sessions`、`~/.codex/archived_sessions` 下的 JSONL 会话文件，从中提取 `token_count` 事件（时间戳、input、cached input、output、reasoning output、total）。

要点：

- **增量读取**：`LiveFileTailReader` 为每个文件维护读取游标，只读上次之后追加的完整行；文件被截断/替换时游标失效并回退到开头。`ReadNewLinesWhile` 支持“某行被拒绝则整批不提交”，供依赖顺序的事件处理重试。
- **子代理过滤**：`SubagentReplayFilter` 识别 `thread_source=subagent` 或带 `forked_from_id + parent_thread_id` 的子任务会话文件；其开头的“父任务时间戳重放”段不产生 token_count，过滤直到出现协作引导标记后的 `task_started`/`inter_agent_communication_metadata` 或时间戳跳跃（≥1 秒）才认为进入子任务自身流量。
- **事件去重**：`UsageEventMerger` 以“事件自带 key 或 时间+各 token 数组合”为稳定键去重，多次扫描同一事件不会重复计数；跨电脑导入也复用同一稳定键。
- **长上下文**：单事件 input > 272,000 token（`UsageTelemetryRules.OpenAiLongContextThresholdTokens`）计入 `LongContext*` 统计。
- **特殊额度 ID**：`codex_bengalfox`（GPT-5.3-Codex-Spark）按 Spark 上下文窗口上限 128k 单独处理。

### 2.3 Claude Code / ZCode / WorkBuddy / DSH

Claude Code / ZCode / WorkBuddy 结构类似：各自读取本地日志目录，解析出与 Codex 相同的 `TokenUsageEvent` 字段（input/cached/output/reasoning/total），复用同一套聚合与缓存。

DSH（DeepSeek Harness，`DshUsageReader`）比较特殊：

- 输入是 `~/.dsh/sessions/--<normalized-cwd>--/<session-id>/session.jsonl.zstd`：**zstd 多帧拼接**的 JSONL（一个 header 帧 + 每批追加一个帧，帧内行不跨帧）。
- 用 `ZstdSharp.Port`（纯托管，适合单文件发布）解压：按帧 magic（`28 B5 2F FD`）切分后逐帧解压，**容忍不完整尾帧**（写入中的帧失败时保留已解码前缀）。
- 每个模型调用对应一条 `assistant/chunk { "type": "usage" }` 记录（usage chunk 永远不会被打包压缩），字段：`inputTokens`（未缓存）、`cacheReadTokens`、`cacheWriteTokens`、`outputTokens`、`reasoningTokens`，顶层 `time` 为毫秒时间戳。
- 字段映射：`InputTokens = inputTokens + cacheReadTokens`、`CachedInputTokens = cacheReadTokens`（与现有缓存命中率口径一致）、`OutputTokens = outputTokens`、`ReasoningOutputTokens = reasoningTokens`、`TotalTokens = 三者之和`。
- 稳定 key 为 `(会话 id, seq)`，重复扫描/导入不重复计数；`assistant/message` 里的 `message.source` 记录 provider/model（如 `deepseek-official` / `deepseek-v4-flash`），`message.usage` 仅在无 usage chunk 时作为后备（当前版本不产生）。
- 价格组映射到 DSH 组（默认首选 DeepSeek V4 Pro 档），后台预热会自动覆盖该来源。

## 3. 缓存与统计管线

```text
日志文件 ──(增量读取)──> TokenUsageEvent ──(去重合并)──> SQLite 缓存
                                                          │
  读取范围 ──> ReadRange/ReadDetailRows ──> TokenUsageSummary / TokenUsageBucket 明细
                                                          │
        UsageBreakdownBuilder：按 RangeMode 决定明细粒度
        （Day=原始明细 / Week、Cycle=固定间隔桶 / Month=每日桶）
```

- **SQLite 缓存**（`UsageCacheStore`）：按“日”为单位，存每日汇总（`CachedDayRecord`）和明细事件（`CachedUsageEvent`）；`IsComplete` 标记完整日。历史日查询直接读缓存，今天（未完整日）叠加实时读取。
- **显示缓存**：`UsageSourceModule` 内维护容量 8 的 LRU 显示缓存（`DisplayCacheKey = 起止时间 + 模式 + 自定义起点`），快速来回切换历史窗口。
- **Coding Time**：`UsageBreakdownBuilder.EstimateCodingTime` 把相邻事件间隔 ≤10 分钟的视为同一活跃会话，累加会话时长。
- **后台预热**：`BackgroundCacheWarmer` 每 120 秒检查一次，按 7 天一批从 2026-01-01 回填 Codex 用量、quota 快照、quota 时间线三类缓存；状态通过 `CacheWarmStatus` 暴露给“缓存详情”窗口，可暂停/恢复。
- **I/O 闸门**：后台预热与前台刷新共用闸门，避免同时扫描同一批文件。

## 4. 额度读取（实时）

`CodexAppServerQuotaReader.ReadCurrent()`：

1. 用 `CodexCliLocator.FindAll()` 发现可运行的 Codex CLI，候选顺序：
   - `~/.codex/plugins/.plugin-appserver/codex.exe`（Codex Desktop 维护的可启动副本，优先）
   - `%APPDATA%\npm\codex.cmd`（npm 独立安装）
   - PATH 中每个目录下的 `codex.exe` / `codex.cmd` / `codex.bat` / `codex`
2. 以 `codex app-server --stdio` 启动，走 JSON-RPC：`initialize`（experimentalApi: true）→ `initialized` → `account/rateLimits/read`。
3. 解析响应中的 `rateLimitsByLimitId.codex`（或回退 `rateLimits`），读取 primary/secondary 窗口的 `usedPercent`、`windowDurationMins`、`resetsAt`，按窗口时长归类 5h / 7d；老版本缺 `windowDurationMins` 时按 primary=5h / secondary=7d 的传统布局，并用“距重置 1~8 天”判断单窗口是否为周窗口。
4. 结果带 20 秒成功 / 10 秒失败内存缓存；当前选中命令失败后自动切换到下一个候选（多版本 Codex 并存时容错）。
5. 全部失败时，回退到本机会话日志中捕获到的额度记录（`CodexUsageReader` 的 quota 解析路径）。

## 5. 额度历史、周期识别与估算

- **额度快照**：每次读取到的 5h/7d 百分比、重置时间、limitId 作为 `CodexQuotaSnapshot` 存入快照缓存（SQLite + `quota-history-v2.jsonl`），`QuotaSnapshotLookup` 把一条排序后的额度时间线复用到明细表所有行（按行时间锚点就近取快照）。
- **异常快照剔除**：`CodexQuotaCycleReader.MarkTransientResetOutliers` 检测“短暂重置段”（≤5 个快照或 ≤3 分钟的孤立 reset run，且其周用量峰值 ≤5%）并标记 `IsAnomaly`，周期与曲线计算会忽略它们。
- **7d 周期识别**（`CodexQuotaCycleReader.ReadWeeklyCycles`）：把快照按周 reset 时间聚类成周期；`StartsNewQuotaCycle` 判断新周期开始的信号：用量硬性回落到 ≤2%（且之前 ≥10%）、reset 时间前移、或 reset 变化且用量明显回落。周期起点用 `resetAt - 7d` 锚定，周期缓存 30 秒。
- **额度估算**（`QuotaEstimateCalculator`）：把“周期内 token 消耗的估算费用”与“额度百分比变化”关联，反推 100% 额度对应的费用/token 数；`MinimumStableQuotaDeltaPercent = 3%` 以下的变化视为不稳定，不参与估算。
- **重置评估**（`QuotaPaceAnalyzer`）：对每个 5h/7d 窗口比较“已用 % vs 按时间线性推进的应耗 %”，输出预计耗尽时间、自然重置前剩余、重置浪费评估和评级（`QuotaPaceReport`）。
- **额度费用曲线**（`QuotaCostCurveCalculator`）：对每个已知周期，取周期内明细行累计费用（按第一个 Codex 对比档价格），与同一时刻的周额度快照配对，得到 `(时间, 已用%, 累计费用)` 曲线；最多 1800 点/周期，剔除额度回退点，基线归零，并按套餐分组。

## 6. 跨电脑数据交换

`CodexDataTransferService`：

- 导出范围：`All` / `Today` / `ThisWeek`（今天、本周均按北京时间，本周从周一 00:00 起）。
- 数据包为 JSON：`format = codex-token-monitor-transfer`、`version = 1`、`packageId`、`sourceDeviceId`（首次生成并持久化）、`sourceDeviceName`、`exportedAtLocal`、usageEvents（含稳定 key）、quotaSnapshots。
- 导入幂等：事件按 `UsageEventMerger.GetStableKey` 合并，快照按键合并，重复/重叠导入不重复计数。
- 扩展名 `.codex.json`（兼容旧 `.codex-data.json`）。

## 7. 设置存储

所有本地状态位于 `%LOCALAPPDATA%\CodexTokenMonitor\`，主要用 SQLite：

| 内容 | 存储 | 说明 |
| --- | --- | --- |
| 用量缓存 | SQLite | 每日汇总 + 明细事件 |
| 额度快照 | SQLite + quota-history-v2.jsonl | 历史额度时间线 |
| 价格 | SQLite | `PriceSettings`：三档快速价格 + 按来源分组的 `PricePreset` 列表 |
| 套餐 | SQLite | `SubscriptionPlanRecord`（起止、名称、金额），默认两条示例 |
| 重置机会 | SQLite | `ResetOpportunityRecord`（获得/过期/已用/备注） |
| 上次显示 | `wpf-last-display-v2.json` | `LastDisplayStore` 防抖落盘，启动时恢复 |

- 套餐导入：`SubscriptionPlanImporter` 从本地 Codex sqlite 数据库尝试识别套餐记录。
- 重置卡同步：`ResetOpportunityStore.SyncFromCodexAsync` 读取 `~/.codex/auth.json` 的 access_token，GET `https://chatgpt.com/backend-api/wham/rate-limit-reset-credits`，把返回的 credits 写入本地；只有用户主动点击同步才发起该请求。

## 8. UI 结构（MainWindow）

```text
├─ 顶部工具条：来源 Tab（Codex / Claude Code / ZCode / WorkBuddy / DSH）｜缓存详情｜数据管理▾｜重置设置｜套餐设置｜价格设置
├─ 额度面板（仅 Codex）：5h 额度 / 7d 额度 / 当前套餐 / 重置过期 / 重置评估 + [估算]
├─ 范围选择条：模式（天/周/月/周期）｜<｜日期 或 周期下拉｜选7天｜>｜今天｜从当前算｜刷新日
├─ 汇总卡：TOTAL TOKENS + 按价格预设横向排列的费用卡
├─ 指标卡：Input / Cached / Uncached / Output / Reasoning / Cache Ratio / Events / Coding Time
└─ 明细区：可拖高的时间轴（ScottPlot） + 明细表（DataGrid，冻结首列、行/列虚拟化）
```

- 每来源模块（`UsageSourceModule` 子类）保存自己的模式、选中日期、自定义起点和显示缓存；切换 Tab 时恢复各自状态。
- 自动刷新开关控制前台定时刷新；后台预热独立运行。
- 时间轴“总 Token/缓存输入”柱状图 + “累计总 Token”右轴折线，`TimelineRow` 高度可拖拽调节。

## 9. 线程模型

- 所有文件扫描、SQLite 读写都在后台任务中执行（`Task.Run`），UI 线程只做绑定与展示。
- `LiveFileTailReader` 每文件一把锁；额度读取有 `SyncRoot` 锁 + 20s/10s 缓存。
- `LastDisplayStore` 使用防抖写（`SemaphoreSlim` 写闸门 + 版本号，取消过期保存）。
- 查询带 `requestVersion`，过期请求的结果会被丢弃，避免快速切换时旧结果覆盖新结果。

## 10. 测试

`tests/CodexTokenMonitor.Core.Tests`（xUnit，`dotnet test -c Release`）覆盖：

- 各来源读取器与增量读取（`LiveFileTailReaderTests`、`UsageSourceModuleTests`、`DshUsageReaderTests`：zstd 多帧样例、稳定 key 去重、时间过滤、截断尾帧容错）
- app-server 额度响应解析（`CodexAppServerQuotaReaderTests`）、CLI 发现（`CodexCliLocatorTests`）
- 数据包导入/导出与幂等（`CodexDataTransferServiceTests`）
- 缓存、价格、额度查询、用量模型与子代理过滤（`UsageCacheStoreTests`、`PriceSettingsTests`、`QuotaSnapshotLookupTests`、`UsageModelTests`、`SubagentReplayFilterTests`）
