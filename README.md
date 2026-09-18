# Codex Token Monitor

一个 Windows 桌面额度监控器，用来从本地日志统计 Codex、Claude Code、ZCode、WorkBuddy、DSH（DeepSeek Harness）的 token 用量、缓存命中、估算 API 等价费用，以及 Codex 5h / 7d 额度百分比变化。

> 数据只读取本机日志和本机缓存，不会上传到远端。价格、套餐和额度分析都只是本地辅助分析，最终以官方账单和产品页面为准。

## 功能

- 统计 Codex / Claude Code / ZCode / WorkBuddy / DSH 五种来源的 token 使用量。
  - DSH（DeepSeek Harness）直接读取 `~/.dsh/sessions` 下 zstd 压缩的会话日志，统计每次模型调用的 input / 缓存读取 / 缓存创建 / output / reasoning。
- 支持按天、近 7 天窗口、按月、按 Codex 额度周期（7d 周期）查看。
- 当前周/月/周期范围若截止到现在，会叠加当天实时日志并随自动刷新更新；历史范围继续优先使用缓存。
- 支持“从当前算”，方便比较同一任务在不同 AI 工具里的消耗。
- 展示 input、cached input、cache write、uncached input、output、reasoning output、缓存命中率、事件数和 Coding Time（10 分钟空闲判定的活跃时长）。
  - Codex 与 ZCode 页均提供"实际模型 · 标准 API 等价"费用卡；ZCode 按智谱/Z.AI 价格组人民币计价，默认对比档为 GLM-5.3 Flash（输入 ¥0.80 / 缓存命中 ¥0.23 / 输出 ¥2.80 每百万 tokens，参考 bigmodel.cn，可编辑）。
- 以 ScottPlot 时间轴图表查看当天/周/月 token 峰值：总 Token 与缓存输入柱状图 + 累计总 Token 折线，可调节高度、带图例和轴标签。
- 用 SQLite 缓存历史统计，历史日期切换更快；启动后后台自动预热历史日缓存。
- 支持导出/导入 Codex 统计数据包，把多台电脑的 token 事件和额度快照合并到一台主统计电脑；重复导入会自动去重。
- 支持从主界面复制当前统计摘要，或将当前来源/范围的分桶明细、费用档和 Codex 额度快照导出为 UTF-8 CSV。
- 支持恢复上次关闭时的显示状态（来源、时间范围、查询结果）。
- Codex 额度：
  - 优先通过 Codex 本地 app-server（`codex app-server --stdio`）实时读取 5h / 7d 剩余额度百分比，自动发现可运行的 Codex CLI（Desktop 插件副本、npm 安装、PATH 上的 `codex.exe/.cmd/.bat`），不可用时回退到本机会话日志捕获的额度记录。
  - 根据当前 7d 周期的额度变化和本地 token 消耗，按 5% 额度分段分析模型构成、换算代价、消耗速度与用量预测。
  - 主界面额度面板保留“额度估算”入口；窗口内继续提供当前 5h / 7d、手动区间和历史周期，并可按需新开额度曲线或分析所选周期。
- 价格设置：
  - 默认展示 GPT-5.6 Sol、DeepSeek V4.1 Flash、小米 MiMo V2.5 Pro 三档（Codex / ZCode / Claude Code / WorkBuddy 各自成组）。DeepSeek V4.1 Flash / V4 Pro 会按北京时间自动合并峰谷计价：周一至周五 `09:00–12:00`、`14:00–18:00` 使用高峰价，其余使用空闲价。
  - 内置可编辑价格库，包含 OpenAI、DeepSeek、小米、Kimi、智谱/Z.AI、豆包、MiniMax、千问、混元、Claude、Grok 等参考档；支持新增/编辑价格预设（`$`、`¥`、Credits 三种单位）。
- 套餐设置：
  - 可记录实际购买套餐和金额。
  - 当前默认示例：`2026-05-01 - 2026-06-01 Plus ¥128`、`2026-06-02 - 2026-07-02 Pro 20x ¥1380`。
  - 会尝试从本地 Codex sqlite 数据库自动识别套餐记录；识别不到时保留手动设置。
- 重置机会设置：
  - 可手动记录 Codex rate limit reset bank 的获得时间、过期时间、是否已用。
  - 可一键从 OpenAI 账户接口同步重置卡（使用本机 `~/.codex/auth.json` 的 access_token）；程序启动完成首轮刷新后也会静默同步，同步成功立即保存。
  - 当前默认示例包含 `2026-06-16`、`2026-06-24`、`2026-06-27` 三次机会，过期默认按获得时间 + 30 天。
- 缓存详情窗口：查看后台缓存预热进度（按来源分类的完成天数、进度条、最近活动日志），可在暂停后手动恢复。

## 运行环境

- Windows 10/11
- .NET SDK 8.0+

项目主界面和设置窗口均使用 WPF，目标框架是 `net8.0-windows10.0.19041.0`，通过共享主题统一控件与配色。

## 构建

```powershell
dotnet build .\CodexTokenMonitor.slnx -c Release
```

CI（GitHub Actions，`.github/workflows/build.yml`）会在 push / PR 时在 `windows-latest` 上执行 restore、Release 构建和 Core 回归测试。

## 发布单文件 exe

便携版会把 .NET runtime 一起打进 exe，体积较大，但复制到没装 .NET 的 Windows 机器也能直接运行：

```powershell
$publishDir = Join-Path (Get-Location) 'outputs/CodexTokenMonitor'
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
dotnet publish .\src\CodexTokenMonitor.Wpf\CodexTokenMonitor.Wpf.csproj -c Release -r win-x64 --self-contained true -o .\outputs\CodexTokenMonitor
```

生成文件：

```text
outputs/CodexTokenMonitor/CodexTokenMonitor.exe
```

日常只使用这一个输出目录。一键生成和后续更新均覆盖此位置，不再按功能名称另建发布目录。

Release 发布目录只保留这个 exe，不需要旁边的 Core PDB 或 .NET runtime 文件；发布前应清理旧目录，避免旧版文件残留。

轻量版（`Lite` 配置，框架依赖）只打包应用和依赖，要求本机已安装 .NET 8 Desktop Runtime 和 ASP.NET Core 8 Runtime（局域网共享服务使用），exe 体积会小很多：

```powershell
dotnet publish .\src\CodexTokenMonitor.Wpf\CodexTokenMonitor.Wpf.csproj -c Lite -o .\outputs\CodexTokenMonitor-lite
```

生成文件：

```text
outputs/CodexTokenMonitor-lite/CodexTokenMonitor.exe
```

一键发布脚本会先执行 Core 回归测试，测试通过后才发布到 `outputs\CodexTokenMonitor`；可用 `--no-pause` / `--no-open` 控制窗口行为：

```text
outputs/一键生成CodexTokenMonitor.cmd
```

## 本地数据位置

监控器自己的缓存与设置默认放在：

```text
%LOCALAPPDATA%\CodexTokenMonitor\
```

主要包括：

- token 统计缓存（SQLite，含每日汇总、明细事件）
- quota 快照缓存（SQLite + `quota-history-v2.jsonl` 历史额度时间线）
- 价格设置、套餐设置、重置机会设置（SQLite）
- `wpf-last-display-v5.json`：上次关闭时的显示状态

读取的数据来源通常包括：

- `%USERPROFILE%\.codex\sessions` / `archived_sessions`：Codex 会话 JSONL（token_count 事件）
- `%USERPROFILE%\.codex` 下与 quota / state 相关的本地 sqlite/json 日志
- `%USERPROFILE%\.codex\auth.json`：仅“同步重置卡”时读取 access_token
- `%USERPROFILE%\.claude`（Claude Code 日志）、ZCode / WorkBuddy 的本地日志目录
- `%USERPROFILE%\.dsh\sessions`：DeepSeek Harness 会话日志（`session.jsonl.zstd`，zstd 多帧拼接的 JSONL）

不同版本客户端日志结构可能变化，所以统计器会尽量容错。

## 跨电脑合并 Codex 数据

同一个账号在两台电脑使用时，两边先启动新版，各自从 Codex 原日志重新统计。在一台开启“数据管理 → 局域网共享”，另一台填写地址和密钥。第一次等两边缓存完成后，手动点“同步全部历史”：按双方已缓存日期分批双向合并，显示进度，最后刷新最近用量。日常点“双向同步最近 8 天”即可覆盖跨自然周的当前 7d 窗口。长期离线或补录旧数据后，可再次同步全部历史；中断后重新同步也不会重复计数。全量同步读取统计缓存，不重新遍历原始会话文件。

共享服务默认随程序启动，监听端口默认 36666，可在共享窗口取消“启动程序时自动开启共享服务”；跨电脑同步由用户操作发起，不会定时自动执行。

Codex 使用新的 `token-cache-v4.sqlite3`；旧统计缓存不迁移，原始会话日志不删除。模型 ID 随事件传输，费用在本机按价格库计算，价格设置不会随用量同步。


1. 在每台电脑点击“数据管理”，按需要选择“导入其他电脑数据…”“仅导出今天数据…”“仅导出本周数据…”或“导出全部数据…”，生成 `*.codex.json` 数据包（模型统计使用 v2 数据包，两台电脑都需要新版重新统计）。今天和本周都按北京时间计算，本周从周一 00:00 开始。
2. 将这些文件复制到作为主统计端的电脑。
3. 将一个或多个数据包直接拖到程序窗口，确认后即可合并；也可以在“数据管理”中点击“导入其他电脑数据…”手动选择。

数据包包含已缓存的 Codex token 事件和 5h / 7d 额度快照，不包含价格、套餐和重置券设置。导入按稳定事件 ID（事件时间 + token 数组合键，或事件自带 key）和额度快照键合并，同一数据包可重复导入而不会重复计数；“刷新日”只重建本机日志缓存，已导入的跨电脑数据会保留。

> 只应合并同一个 Codex 账户的数据；不同账户的额度百分比没有可比性。

## 费用口径

Codex 的“实际模型 · API 等价”按每条日志的模型、输入、缓存创建、缓存读取和输出匹配价格库后汇总；鼠标悬停可查看各模型明细。新模型自动加入价格库，默认 0x（费用为 0）并标明待填写；补价后历史费用直接重算，无需重扫日志。其余卡片标注“换用”，回答相同 Token 换模型后的参考费用，不假定另一模型会产生相同长度的真实响应。

官方剩余百分比与重置时间直接读取；“组合折算”美元额度使用当前周期/区间的实际模型费用与已用百分比推算，需先合并同账号全部设备的用量。API 价格不是订阅扣额规则，模型组合变化后该金额不是固定上限；不再按总 Token 声称固定容量。


费用使用本地 token 事件估算：

```text
cost = uncached_input_millions * input_price
     + cached_input_millions * cached_input_price
     + cache_write_input_millions * cache_write_price
     + output_millions * output_price
```

注意：

- Reasoning output 已包含在 output 中，不重复计费。
- 价格档未单独填写缓存创建价时，缓存创建按普通未缓存输入价回退，避免旧价格文件漏算。
- 单事件 input 超过 27.2 万 token 时记为“长上下文事件”，在明细行单独统计。
- Codex 本地 token 和远端套餐额度不是同一个概念，额度估算只是用百分比变化做反推。
- Fast / 普通模式可能有不同额度倍率，历史区间混合模式时只能作为近似值。
- 小米 MiMo Credits 默认按 token plan 展示，也可切换到 API 价格档。

## 开发说明

当前职责边界、查询与并发契约及后续重构顺序见 [架构基线与迭代路线](docs/ARCHITECTURE-REVIEW.md)。

项目分层：

```text
src/CodexTokenMonitor.Core/CodexTokenMonitor.Core.csproj   日志、缓存、统计与额度计算（net8.0）
src/CodexTokenMonitor.Wpf/CodexTokenMonitor.Wpf.csproj    原生 WPF 界面与设置窗口（net8.0-windows）
tests/CodexTokenMonitor.Core.Tests/                        核心回归测试
```

运行测试：

```powershell
dotnet test .\tests\CodexTokenMonitor.Core.Tests\CodexTokenMonitor.Core.Tests.csproj -c Release
```

实时日志采用“首次完整读取、后续按文件尾部增量读取”的方式（`LiveFileTailReader` 按文件维护读取游标）。活动 JSONL 被截断、替换、清理缓存或查询需要更早覆盖范围时，读取游标会自动失效并安全回退；历史完整日仍以 SQLite 数据为准。子代理（subagent）会话文件的“父任务回放”段会被过滤，只统计子任务边界之后的 token_count。后台缓存、主窗口刷新、周期分析和导入导出共用 `MonitorRuntime` 的 I/O 闸门；费用曲线只使用缓存并在内存投影缺失额度点，保持独立于预热的响应。

主窗口遇到用量或额度缓存故障时会保留上次成功结果并提示重试；没有历史结果则显示统计暂不可用。关闭时停止接收新后台操作，并在一个 3 秒总预算内等待已登记任务及最后一次显示保存。

关键文件：

- `src/CodexTokenMonitor.Wpf/MainWindow.xaml(.cs)`：WPF 主界面（来源 Tab、额度面板、范围选择、指标卡、时间轴、明细表、数据管理）。
- `src/CodexTokenMonitor.Wpf/MainWindow.DataTransfer.cs`：主窗口的数据导入导出、CSV 和拖放处理（partial 分文件整理）；`MainWindow.DataSharing.cs`：共享服务生命周期与数据交换入口。
- `src/CodexTokenMonitor.Wpf/Themes/MonitorTheme.xaml`：共享颜色与控件样式；`CostCardControl.xaml(.cs)`：费用卡片展示组件。
- `src/CodexTokenMonitor.Wpf/QuotaCycleAnalysisWindow.xaml(.cs)`：当前或所选 7d 周期的模型构成、分段代价、消耗时间线和预测窗口。
- `src/CodexTokenMonitor.Wpf/WpfTokenTimelineControl.cs`：ScottPlot token 时间轴控件。
- `src/CodexTokenMonitor.Wpf/BackgroundCacheWarmer.cs`：后台历史缓存预热；`CacheDetailsWindow.xaml(.cs)`：缓存详情窗口。
- `src/CodexTokenMonitor.Wpf/LastDisplayStore.cs`：恢复上次显示状态。
- `src/CodexTokenMonitor.Wpf/WeekWindowPicker.cs`：7 天窗口选择对话框。
- `src/CodexTokenMonitor.Core/CodexUsageReader.cs`：Codex 日志、token、quota 读取与缓存（含 `SubagentReplayFilter` 子代理过滤）。
- `src/CodexTokenMonitor.Core/CodexAppServerQuotaReader.cs`：通过本地 app-server 协议实时读取 5h/7d 额度。
- `src/CodexTokenMonitor.Core/CodexCliLocator.cs`：自动发现可运行的 Codex CLI。
- `src/CodexTokenMonitor.Core/CodexQuotaCycle.cs`：7d 额度周期的识别与异常快照剔除。
- `src/CodexTokenMonitor.Core/QuotaCycleAnalysisCalculator.cs` / `QuotaPace.cs` / `QuotaSnapshotLookup.cs`：周期分段分析、重置评估、快照查询。
- `src/CodexTokenMonitor.Core/ClaudeUsageReader.cs` / `ZCodeUsageReader.cs` / `WorkBuddyUsageReader.cs` / `DshUsageReader.cs`：其他来源日志读取（DSH 读取 zstd 压缩的会话日志，按 frame 解压并容忍不完整尾帧）。
- `src/CodexTokenMonitor.Core/UsageSourceReader.cs` / `UsageSourceRegistry.cs` / `UsageQueryModels.cs`：来源能力与元数据、查询范围和结果模型。
- `src/CodexTokenMonitor.Wpf/UsageSourceModule.cs` / `UsageDisplayViewModel.cs`：每窗口的来源选择和显示缓存，以及主统计快照、空/失败/恢复状态。
- `src/CodexTokenMonitor.Core/UsageQueryService.cs`：与 WPF 无关的用量查询服务，组合来源读取、缓存修复、分桶和额度锚点；`ExecuteCached` 只接收缓存查询能力，不扫描原始来源日志。
- `src/CodexTokenMonitor.Core/LatestRequestRunner.cs`：串行刷新并合并等待请求，提供版本校验和取消/异常后的恢复。
- `src/CodexTokenMonitor.Core/MonitorRuntime.cs`：共用 I/O 锁、后台任务登记、停止接收、取消与限时等待；任务结束后释放资源。
- `src/CodexTokenMonitor.Core/AnalysisQuerySession.cs` / `QuotaCycleAnalysisQueryService.cs`：分析窗口任务与独立取消、缓存诊断、原周期刷新及校准保护。
- `src/CodexTokenMonitor.Core/CacheOperationDiagnostics.cs`：按操作收集缓存故障，支持同实例后续重试；失败结果不覆盖成功显示。
- `src/CodexTokenMonitor.Core/CodexDataTransferService.cs`：跨电脑数据包导出/导入（幂等合并）。
- `src/CodexTokenMonitor.Core/PriceSettings.cs`：价格档案、分组默认值与价格库。
- `src/CodexTokenMonitor.Core/SubscriptionPlan*.cs`：套餐/实际花费设置和导入。
- `src/CodexTokenMonitor.Core/ResetOpportunities.cs`：rate limit reset bank 数据、汇总与接口同步。
- `src/CodexTokenMonitor.Wpf/ResetOpportunityWindow.xaml(.cs)` / `SubscriptionPlanWindow.xaml(.cs)`：原生 WPF 重置机会与套餐设置，支持表格编辑、验证、导入或同步。

## 隐私

这个工具主要在本地读取和统计日志；跨电脑共享传输统计事件与额度快照，不传输原始会话日志正文。公开仓库不包含个人日志、缓存数据库或发布产物。

程序在首轮用量刷新后及手动同步重置卡时，使用本机 `~/.codex/auth.json` 中的 access_token 向 OpenAI `chatgpt.com` 后端接口查询 rate-limit reset credits，并把结果保存在本机。额度读取通过本机 `codex app-server --stdio` 进程完成；该进程负责访问账户服务。

局域网共享按保存的自动启动选项监听端口，默认开启。其他设备必须提供共享密钥才能读取或合并统计数据；本机主动同步需要在共享窗口发起。

## 项目演进记录

从最初的 token 统计脚本到当前桌面监控器的需求讨论和迭代记录，见：

- [`docs/history/2026-06-22-codex-token-monitor-thread.md`](docs/history/2026-06-22-codex-token-monitor-thread.md)

该档案只包含用户与 Codex 的文字消息，不包含截图二进制、工具原始输出、系统指令或访问凭证。Codex 原始任务仍由桌面应用保存在用户目录中。

更多文档：

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)：架构与数据流说明。
- [`docs/USER_GUIDE.md`](docs/USER_GUIDE.md)：用户使用指南。
- [`docs/CHANGELOG.md`](docs/CHANGELOG.md)：版本变更记录。
