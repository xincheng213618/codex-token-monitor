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
│  │   └─ 数据交换：CodexDataTransferService、CodexDataSharingServer/Client
│  └─ CodexTokenMonitor.Wpf/        # 原生 WPF 界面与设置窗口（net8.0-windows10.0.19041.0）
│      ├─ MainWindow、MainWindow.DataTransfer、MainWindow.DataSharing
│      ├─ QuotaEstimateWindow、QuotaCostCurveWindow、CacheDetailsWindow
│      ├─ WpfTokenTimelineControl、QuotaCostCurveControl、QuotaCostCurveCalculator
│      ├─ BackgroundCacheWarmer、LastDisplayStore、WeekWindowPicker
│      ├─ PriceSettingsWindow、PricePresetEditorWindow
│      ├─ ResetOpportunityWindow、SubscriptionPlanWindow
│      └─ Themes/MonitorTheme.xaml、CostCardControl
└─ tests/CodexTokenMonitor.Core.Tests/   # xUnit 核心回归测试
```

依赖关系：`Wpf → Core`。主界面和设置窗口均使用 WPF，已移除旧 WinForms 窗体的直接编译引用及 `UseWindowsForms`。

局域网共享使用 `Microsoft.AspNetCore.App` 中的 Kestrel，显式监听 IPv4 端口（默认 36666），由主窗口持有服务生命周期。启动时按保存的 `AutoStart` 设置开启监听，默认开启；`DataSharingWindow` 提供该选项、手动启停和客户端同步操作。程序不会定时主动发起跨电脑同步。所有请求使用 `X-Codex-Sharing-Key` 请求头鉴权，不启用 CORS，不接受客户端给定的文件路径。`GET /api/health` 返回协议版本与设备名，`GET /api/week` 导出最近 8 个北京时间日期的 v2 JSON 数据包，`POST /api/week` 接收原始 JSON 数据包并返回新增/已有事件与快照数。下载包含服务器已合并的其他设备数据。

网络读写使用独立临时文件和流式传输；包上限 256 MB，每次传输限时 3 分钟，单个服务器同时只处理一个数据包，多余传输返回 409。`MainWindow.DataSharing.cs` 在已有 `usageQueryGate` 内调用导出/导入，导入先验证整包属于最近 8 个北京时间日期，再通过现有稳定键合并。网络成功返回与 UI 刷新分离，UI 刷新通过 Dispatcher 排队，避免缓存锁与主线程相互等待。共享配置位于 `%LOCALAPPDATA%\CodexTokenMonitor\data-sharing-v1.json`；自包含发布携带网络运行时，Lite 发布要求另装 ASP.NET Core 8 Runtime。

## 2. 数据源与读取

### 2.1 五种来源

`UsageSource` 枚举：`Codex`、`ClaudeCode`、`ZCode`、`WorkBuddy`、`Dsh`。每种来源由 `IUsageSourceReader` 实现包装，对外暴露统一接口（`ReadRange`、`ReadDetailRows`、`ReadCachedRange`、`RefreshCachedDay`、`ClearCache` 等）。`UsageSourceReaders.For(source)` 按来源取单例。

只有 Codex 支持额度（`SupportsQuota == true`）。

### 2.2 Codex 日志读取（CodexUsageReader）

输入是 `~/.codex/sessions`、`~/.codex/archived_sessions` 下的 JSONL 会话文件，从中提取 `token_count` 事件（时间戳、input、cached input、cache write input、output、reasoning output、total）。

要点：

- **增量读取**：`LiveFileTailReader` 为每个文件维护读取游标，只读上次之后追加的完整行；文件被截断/替换时游标失效并回退到开头。`ReadNewLinesWhile` 支持“某行被拒绝则整批不提交”，供依赖顺序的事件处理重试；取消发生在缓冲区中途时也不会提交半行游标。Codex 进入实时读取前会按同一日级回看边界裁剪长期未更新或已删除文件的游标及子代理过滤器，避免会话数量增长导致进程内状态无限累积。
- **子代理过滤**：`SubagentReplayFilter` 识别 `thread_source=subagent` 或带 `forked_from_id + parent_thread_id` 的子任务会话文件；其开头的“父任务时间戳重放”段不产生 token_count，过滤直到出现协作引导标记后的 `task_started`/`inter_agent_communication_metadata` 或时间戳跳跃（≥1 秒）才认为进入子任务自身流量。
- **事件去重**：`UsageEventMerger` 以“事件自带 key 或 时间+各 token 数组合”为稳定键去重，多次扫描同一事件不会重复计数；跨电脑导入也复用同一稳定键。
- **长上下文**：单事件 input > 272,000 token（`UsageTelemetryRules.OpenAiLongContextThresholdTokens`）计入 `LongContext*` 统计。
- **特殊额度 ID**：`codex_bengalfox`（GPT-5.3-Codex-Spark）按 Spark 上下文窗口上限 128k 单独处理。

### 2.3 Claude Code / ZCode / WorkBuddy / DSH

Claude Code / ZCode / WorkBuddy 结构类似：各自读取本地日志目录，解析出与 Codex 相同的 `TokenUsageEvent` 字段（input/cached/cache write/output/reasoning/total），复用同一套聚合与缓存。Claude 的 `cache_creation_input_tokens` 映射为 cache write。

DSH（DeepSeek Harness，`DshUsageReader`）比较特殊：

- 输入是 `~/.dsh/sessions/--<normalized-cwd>--/<session-id>/session.jsonl.zstd`：**zstd 多帧拼接**的 JSONL（一个 header 帧 + 每批追加一个帧，帧内行不跨帧）。
- 用 `ZstdSharp.Port`（纯托管，适合单文件发布）解压：按帧 magic（`28 B5 2F FD`）流式切分并逐帧、逐行消费，**容忍不完整尾帧**（写入中的帧失败时保留已解码前缀），不会把整份压缩文件和完整解压文本同时载入内存。
- 每个模型调用对应一条 `assistant/chunk { "type": "usage" }` 记录（usage chunk 永远不会被打包压缩），字段：`inputTokens`（未缓存）、`cacheReadTokens`、`cacheWriteTokens`、`outputTokens`、`reasoningTokens`，顶层 `time` 为毫秒时间戳。
- 字段映射：`InputTokens = inputTokens + cacheReadTokens + cacheWriteTokens`、`CachedInputTokens = cacheReadTokens`、`CacheWriteInputTokens = cacheWriteTokens`、`UncachedInputTokens = inputTokens`、`OutputTokens = outputTokens`、`ReasoningOutputTokens = reasoningTokens`、`TotalTokens = input + output`。
- 稳定 key 为 `(会话 id, seq)`，重复扫描/导入不重复计数；`assistant/message` 里的 `message.source` 记录 provider/model（如 `deepseek-official` / `deepseek-v4-flash`），`message.usage` 仅在无 usage chunk 时作为后备（当前版本不产生）。
- 价格组映射到 DSH 组（默认首选 DeepSeek V4 Flash 峰谷合并档），每条事件按北京时间进入峰谷子汇总，后台预热会自动覆盖该来源。

## 3. 缓存与统计管线

```text
日志文件 ──(增量读取)──> TokenUsageEvent ──(去重合并)──> SQLite 缓存
                                                          │
  读取范围 ──> ReadRange/ReadDetailRows ──> TokenUsageSummary / TokenUsageBucket 明细
                                                          │
        UsageBreakdownBuilder：按 RangeMode 决定明细粒度
        （Day=原始明细 / Week、Cycle=固定间隔桶 / Month=每日桶）
```

- **SQLite 缓存**（`UsageCacheStore`）：按“日”为单位，存每日汇总（`CachedDayRecord`）和明细事件（`CachedUsageEvent`）；`IsComplete` 标记完整日，且明细事件数量必须覆盖汇总事件数，否则会整日重扫并替换明细。五个来源的文件扫描会同步传播完整性状态；普通文件异常或 DSH 截断 zstd 尾帧产生的可读前缀不会被永久标记为 complete，后续读取仍会补扫。读取缓存记录时会校验并归一化非负计数、缓存读取/创建输入上限、派生字段和长上下文/峰时子计数；发现损坏记录会拒绝缓存命中并回到补扫路径。新增缓存创建列时会把旧日标记为 incomplete，让后台从仍保留的源日志重建。整日范围的缓存快速读取同样要求 `IsComplete` 且明细覆盖汇总，遇到不完整日会从已落盘明细重建，避免缓存汇总绕过完整性状态。历史日查询直接读缓存，今天（未完整日）叠加实时读取。用量与额度快照的缓存命中读取和可取消扫描都接受生命周期令牌；写入事务在提交前检查取消并回滚当前日，不会留下半写入缓存。事件、额度快照和导入稳定键在批量写入时复用参数化命令，减少历史预热与数据包导入的短生命周期分配。
- **时间边界**：所有来源读取器的实时汇总/明细入口先将输入范围转换为北京时间（UTC+8），再进行自然日切分、缓存命中和结果元数据填充；缓存层的范围查询使用同一规则，避免跨午夜输入造成漏日或多日。
- **显示缓存**：`UsageSourceModule` 内维护容量 8 的 LRU 显示缓存（`DisplayCacheKey = 起止时间 + 模式 + 自定义起点`），快速来回切换历史窗口。
- **Coding Time**：`UsageBreakdownBuilder.EstimateCodingTime` 把相邻事件间隔 ≤10 分钟的视为同一活跃会话，累加会话时长。
- **后台预热**：`BackgroundCacheWarmer` 每 120 秒检查一次，按 7 天一批从 2026-01-01 回填 Codex 用量、quota 快照、quota 时间线三类缓存；状态通过 `CacheWarmStatus` 暴露给“缓存详情”窗口，可暂停/恢复。未完成日期预检先统一到北京时间，实时读取、时间线物化都绑定同一取消令牌，并在 SQLite 事务提交前检查；每个任务结束后还会回查持久化 incomplete 状态，只有真正完成的日期才计入 UI 进度，若本轮仍有待补扫项则明确显示待重试数量。
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

读取超时或协议失败时会关闭 stdin，并在必要时终止临时 helper 的整个进程树，避免 `.cmd` 启动链留下常驻 app-server 子进程。

## 5. 额度历史、周期识别与估算

- **额度快照**：每次读取到的 5h/7d 百分比、重置时间、limitId 作为 `CodexQuotaSnapshot` 存入快照缓存（SQLite + `quota-history-v2.jsonl`）；写入和读取会丢弃清洗后没有任何可用窗口的快照，旧缓存即使错误标记为完成也会在完整性预检时触发补扫；额度日志暂时不可读时，扫描状态也会阻止历史日冻结为 complete。`QuotaSnapshotLookup` 把一条排序后的额度时间线复用到明细表所有行（按行时间锚点就近取快照）。事件行按事件时间匹配；周/周期的 10 分钟聚合行按自身桶宽匹配和预取，避免把下一段的快照显示到当前行。历史 JSONL 的内部读取缓存使用只读值类型保存快照和窗口，避免长期运行时为每条记录分配多个对象。
- **异常快照剔除**：`CodexQuotaCycleReader.MarkTransientResetOutliers` 检测“短暂重置段”（≤5 个快照或 ≤3 分钟的孤立 reset run，且其周用量峰值 ≤5%）并标记 `IsAnomaly`，周期与曲线计算会忽略它们。
- **7d 周期识别**（`CodexQuotaCycleReader.ReadWeeklyCycles`）：把快照按周 reset 时间聚类成周期；`StartsNewQuotaCycle` 判断新周期开始的信号：用量硬性回落到 ≤2%（且之前 ≥10%）、reset 时间前移、或 reset 变化且用量明显回落。周期起点用 `resetAt - 7d` 锚定；周期边界按额度窗口身份缓存 2 分钟，当前周期范围仍在界面查询时实时截断到现在。
- 周期对象只保留周期边界、reset、快照数量和最大周用量百分比，不把几十万条历史快照继续挂在 UI 模块和周期缓存上；需要逐条快照的曲线/明细路径仍从 SQLite/历史源按范围读取。
- **额度估算**（`QuotaEstimateCalculator`）：把“周期内 token 消耗的估算费用”与“额度百分比变化”关联，反推 100% 额度对应的费用/token 数；`MinimumStableQuotaDeltaPercent = 3%` 以下的变化视为不稳定，不参与估算。费用和 token 上限缩放统一通过 `QuotaMath` 做百分比校验、下溢/除零防护和上限饱和。
- **重置评估**（`QuotaPaceAnalyzer`）：对每个 5h/7d 窗口比较“已用 % vs 按时间线性推进的应耗 %”，输出预计耗尽时间、自然重置前剩余、重置浪费评估和评级（`QuotaPaceReport`）。
- **额度费用曲线**（`QuotaCostCurveCalculator`）：对每个已知周期，取周期内明细行累计费用（按日志中的实际模型及当前价格库），与同一时刻的周额度快照配对，得到 `(时间, 已用%, 累计费用)` 曲线；最多 1800 点/周期，剔除额度回退点，基线归零，并按套餐分组。当前周期读取当天实时明细时复用主窗口的 I/O 闸门，与自动刷新和后台预热串行，避免并发扫描同一批活动日志；周期识别和快照聚合接收同一取消令牌，费用区间的插值点索引按曲线复用。

## 6. 跨电脑数据交换

`CodexDataTransferService`：

- 导出范围：`All` / `Today` / `ThisWeek`（今天、本周均按北京时间，本周从周一 00:00 起）。
- 数据包为 JSON：`format = codex-token-monitor-transfer`、`version = 1`、`packageId`、`sourceDeviceId`（首次生成并持久化）、`sourceDeviceName`、`exportedAtLocal`、usageEvents（含稳定 key）、quotaSnapshots。
- 导入幂等：事件按 `UsageEventMerger.GetStableKey` 合并，快照按键合并，重复/重叠导入不重复计数。
- 导入先从文件流完整校验所有包，JSON 反序列化接收生命周期取消令牌；token 事件与额度快照会在稳定键合并前做计数和可用窗口校验，无窗口的额度快照直接拒绝。导入阶段对用量/额度缓存写入传播失败，避免报告未落盘的新增数量；导出从 SQLite 游标直接写入唯一临时文件，完成后再替换目标路径。
- 扩展名 `.codex.json`（兼容旧 `.codex-data.json`）。

## 7. 设置存储

所有本地状态位于 `%LOCALAPPDATA%\CodexTokenMonitor\`，主要用 SQLite：

| 内容 | 存储 | 说明 |
| --- | --- | --- |
| 用量缓存 | SQLite | 每日汇总 + 明细事件 |
| 额度快照 | SQLite + quota-history-v2.jsonl | 历史额度时间线 |
| 价格 | SQLite | `PriceSettings`：三档快速价格 + 按来源分组的 `PricePreset` 列表 |
| 套餐 | SQLite | `SubscriptionPlanRecord`（起止、名称、金额），默认 5 月 Plus、自 6 月起每月 2 日 Pro 20x / ¥1,380，保存在独立 monitor-settings.sqlite3 |
| 重置机会 | SQLite | `ResetOpportunityRecord`（获得/过期/已用/备注） |
| 上次显示 | `wpf-last-display-v2.json` | `LastDisplayStore` 防抖落盘，启动时恢复 |

- 套餐导入：`SubscriptionPlanImporter` 从本地 Codex sqlite 数据库尝试识别套餐记录。
- 重置卡同步：`ResetOpportunityStore.SyncFromCodexAsync` 读取 `~/.codex/auth.json` 的 access_token，GET `https://chatgpt.com/backend-api/wham/rate-limit-reset-credits`，把返回的 credits 写入本地。主窗口首轮用量刷新后会静默同步，用户也可在重置设置中手动同步；成功同步立即持久化，取消设置窗口不会撤销已同步结果。

## 8. UI 结构（MainWindow）

```text
├─ 应用标题与设置操作；下一行是来源 Tab（Codex / Claude Code / ZCode / WorkBuddy / DSH）与缓存详情
├─ 额度面板（仅 Codex）：5h 额度 / 7d 额度 / 当前套餐 / 重置过期 / 重置评估 + [估算]
├─ 范围选择条：模式（天/周/月/周期）｜<｜日期 或 周期下拉｜选7天｜>｜今天｜从当前算｜刷新日
├─ 汇总卡：TOTAL TOKENS + 按价格预设横向排列的费用卡
├─ 单行九项指标：Input / Cached / Cache Write / Uncached / Output / Reasoning / Cache Ratio / Events / Coding Time
└─ 明细区：可拖高的时间轴（ScottPlot） + 明细表（DataGrid，冻结前两列、行/列虚拟化）
```

- 每来源模块（`UsageSourceModule` 子类）保存自己的模式、选中日期、自定义起点和显示缓存；切换 Tab 时恢复各自状态。
- 查询结果的全量 `DetailRows` 只在当前查询期间供时间轴使用；模块的上次显示结果和周期 LRU 缓存只保留汇总/分桶，避免长期持有大范围事件明细。
- `UsageTimelineBuilder` 在送入 WPF 图表前按分钟/10 分钟/小时预聚合事件，图表控件不再长期保存全量明细行。
- `UsageSummaryBuilder` 从已有分桶重建区间汇总时使用 `MergeFrom`，会保留长上下文与 DeepSeek 峰时子计数，不把聚合桶误当成单条事件。
- 自动刷新开关控制前台定时刷新；所有结束于当前时刻的范围（天/周/月/当前周期/自定义起点）都会叠加当天实时日志，历史范围不重扫用量但 Codex 顶部实时额度仍独立刷新，后台预热独立运行并只回填历史日。
- 当前天/周/月范围会保存“跟随当前时间”标记；跨过午夜后自动滚动到新的当天/周/月，用户主动选择的历史范围不会被自动改写。
- 时间轴“总 Token/缓存输入”柱状图 + “累计总 Token”右轴折线，`TimelineRow` 高度可拖拽调节。
- 历史查询发现每日汇总与事件明细数量不一致时，所有来源读取器（Codex / Claude / ZCode / WorkBuddy / DSH）都会从当天起补扫并合并已有明细；本次返回结果与回填后的缓存同步重建，避免首轮刷新仍显示旧汇总。缓存层统一将时间边界归一到北京时间，避免不同 `DateTimeOffset` 偏移导致自然日错配。
- `Themes/MonitorTheme.xaml` 统一窗口底色、文字、强调色、按钮、输入框和表格样式；重置机会和套餐设置已使用原生 WPF 窗口并绑定主窗口 owner。
- `CostCardControl.xaml/.xaml.cs` 封装费用卡片展示，调用方仍负责费用计算和格式化。
- `MainWindow.DataTransfer.cs` 收纳导入导出、拖放和当前明细 CSV 构建；它是主窗口的 partial，仍共享其状态，属于分文件整理。CSV 由 WPF 层组装展示分桶、费用和额度列，Core 层 `CsvWriter` 负责统一处理逗号、引号和换行转义。

## 9. 线程模型

- 所有文件扫描、SQLite 读写都在后台任务中执行（`Task.Run`），UI 线程只做绑定与展示。
- `LiveFileTailReader` 每文件一把锁；额度读取有 `SyncRoot` 锁 + 20s/10s 缓存。
- 用量读取器会把窗口生命周期取消令牌传入 JSONL/Zstd 文件扫描、尾读和后台预热，关闭或 supersede 的查询不会继续完整扫完大日志。
- 主窗口额度刷新也在后台读取缓存额度和实时额度，并复用用量 I/O 闸门；缓存快照/估算读取、app-server 的标准输入/输出等待和额度明细扫描接收同一个生命周期令牌，窗口关闭后会结束临时额度进程并丢弃尚未回到 UI 的结果。
- 额度估算窗口的历史估算与实时曲线并行加载；窗口关闭或任一任务失败后会取消同批任务，并等待、观察另一任务，避免后台异常脱离 UI 生命周期。
- `LastDisplayStore` 使用防抖写（`SemaphoreSlim` 写闸门 + 版本号，取消过期保存）。
- 查询带 `requestVersion`，过期请求的结果会被丢弃，避免快速切换时旧结果覆盖新结果。

## 10. 测试

`tests/CodexTokenMonitor.Core.Tests`（xUnit，`dotnet test -c Release`）覆盖：

- 各来源读取器与增量读取（`LiveFileTailReaderTests`、`UsageSourceModuleTests`、`DshUsageReaderTests`：zstd 多帧样例、稳定 key 去重、时间过滤、截断尾帧容错）
- app-server 额度响应解析（`CodexAppServerQuotaReaderTests`）、CLI 发现（`CodexCliLocatorTests`）
- 数据包导入/导出与幂等（`CodexDataTransferServiceTests`）
- 缓存一致性、实时范围策略、分桶汇总、价格、额度查询、用量模型、时间轴预聚合与子代理过滤（`UsageCacheStoreTests`、`ReaderCacheConsistencyTests`、`UsageRangePolicyTests`、`UsageSummaryBuilderTests`、`PriceSettingsTests`、`QuotaSnapshotLookupTests`、`UsageModelTests`、`UsageTimelineBuilderTests`、`SubagentReplayFilterTests`）

GitHub Actions 在每次 push / PR 中先构建解决方案，再执行这组 Core 回归测试。


## Codex 模型归因与费用（2026-09-05）

`CodexModelContext` 按文件维护 `turn_context` / `thread_settings_applied` 中的模型上下文，Token 记录自身的模型优先。增量游标回到文件开头时一并重置模型上下文。`TokenUsageEvent.ModelId` 进入 SQLite 明细、日汇总 `ModelUsage`、显示快照与 v2 交换数据包，所有聚合使用事件/桶合并方法保留模型维度。Codex 从新的 v4 缓存重新统计，不读取或迁移旧统计缓存。

`CodexModelCost` 根据完整 Codex 价格库解析模型 ID，与对比卡显示顺序独立。已识别且配置价格的事件相加为 API 等价费用，未计价部分显式计数，新模型暂按 0x 参与折算，并标记待填写。`EstimateCost(profile)` 保留为同 Token 的目标模型换算；官方百分比不由任一种费用反算或相加。固定 Token 容量停止输出。

共享前扫描最近 8 日期的本机用量与额度快照，“双向同步最近 8 天”依次上传、下载；双方以相同稳定键合并，模型字段不进入事件身份。v3 握手要求双方升级，传输文件保持 v2。最近 8 日期覆盖任意当前 7d 窗口，避免按周一切割漏掉前一自然周的用量。

“同步全部历史”单独手动触发：`GET /api/history/dates` 查询用量与额度表日期并集，客户端合并两边日期，拆成最多 7 个自然日一批；`POST/GET /api/history?start=yyyy-MM-dd&end=yyyy-MM-dd` 按半开区间上传、下载并合并。服务端先校验日期范围及整包事件，再写缓存。沿用请求头鉴权、单传输闸门、单包 256 MB 与每请求 3 分钟限制；总历史不受单包大小限制，可取消并安全重复合并已完成批次。历史日期与数据只查询 SQLite，不重扫原日志，依赖双方先完成后台统计。历史导入只失效显示缓存，最终最近用量同步再刷新界面，避免每批触发整段额度曲线重算。

实现参考：[CC Switch session_usage_codex.rs](https://github.com/farion1231/cc-switch/blob/db41d701879592b8eca938cbe5c5ac28dd732b9f/src-tauri/src/services/session_usage_codex.rs) 的逐记录模型上下文与用量归因思路；未复制其源码。Astra 标准档来源：[OpenAI 模型文档](https://developers.openai.com/api/docs/models/gpt-6-astra)。费用仍按用户当前价格库估算。

### 历史补扫与配置隔离

额度快照与 Token 历史各按待补日期合并成一个范围，只读一遍源文件，再按日落库；已完成历史不重扫，失败或取消不会把尚未完整扫描的天标成完成。历史扫描以记录时间筛选，文件 mtime 只用于当天尾读候选，避免跨日追加和复制日志漏统计。快照时间索引在一个范围内复用，邻近查找使用二分搜索；曲线窗口基于已统计缓存，不等待后台 I/O 闸门。

套餐和重置卡属于用户配置，使用 monitor-settings.sqlite3，与 token-cache-v4.sqlite3 的生命周期分开。无需迁移旧统计；用户当前机器的已购套餐可从仍保留的旧表一次性恢复。完整历史账本可用“同步全部历史”或导出全部/导入交换已统计事件，日常网络共享保持最近 8 天并按稳定键合并。
