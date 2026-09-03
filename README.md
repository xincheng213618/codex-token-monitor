# Codex Token Monitor

一个 Windows 桌面额度监控器，用来从本地日志统计 Codex、Claude Code、ZCode、WorkBuddy、DSH（DeepSeek Harness）的 token 用量、缓存命中、估算 API 等价费用，以及 Codex 5h / 7d 额度百分比变化。

> 数据只读取本机日志和本机缓存，不会上传到远端。价格、套餐和额度估算都只是本地辅助分析，最终以官方账单和产品页面为准。

## 功能

- 统计 Codex / Claude Code / ZCode / WorkBuddy / DSH 五种来源的 token 使用量。
  - DSH（DeepSeek Harness）直接读取 `~/.dsh/sessions` 下 zstd 压缩的会话日志，统计每次模型调用的 input / 缓存读取 / output / reasoning。
- 支持按天、近 7 天窗口、按月、按 Codex 额度周期（7d 周期）查看。
- 支持“从当前算”，方便比较同一任务在不同 AI 工具里的消耗。
- 展示 input、cached input、uncached input、output、reasoning output、缓存命中率、事件数和 Coding Time（10 分钟空闲判定的活跃时长）。
- 以 ScottPlot 时间轴图表查看当天/周/月 token 峰值：总 Token 与缓存输入柱状图 + 累计总 Token 折线，可调节高度、带图例和轴标签。
- 用 SQLite 缓存历史统计，历史日期切换更快；启动后后台自动预热历史日缓存。
- 支持导出/导入 Codex 统计数据包，把多台电脑的 token 事件和额度快照合并到一台主统计电脑；重复导入会自动去重。
- 支持恢复上次关闭时的显示状态（来源、时间范围、查询结果）。
- Codex 额度：
  - 优先通过 Codex 本地 app-server（`codex app-server --stdio`）实时读取 5h / 7d 剩余额度百分比，自动发现可运行的 Codex CLI（Desktop 插件副本、npm 安装、PATH 上的 `codex.exe/.cmd/.bat`），不可用时回退到本机会话日志捕获的额度记录。
  - 根据百分比变化和本地 token 消耗估算 100% 额度价值，支持历史 7d 周期表、手动百分比区间估算。
  - 主界面额度面板显示 5h 额度、7d 额度、当前套餐、重置过期、重置评估，并提供一键“估算”窗口。
  - “额度曲线”窗口按额度周期绘制 已用额度% vs 累计估算费用 曲线，按套餐筛选，多周期叠加对比。
- 价格设置：
  - 默认展示 GPT-5.6 Sol、DeepSeek V4 Pro、小米 MiMo V2.5 Pro 三档（Codex / ZCode / Claude Code / WorkBuddy 各自成组）。DeepSeek V4 Flash / Pro 会按北京时间自动合并峰谷计价：`09:00–12:00`、`14:00–18:00` 使用高峰价，其余使用空闲价。
  - 内置可编辑价格库，包含 OpenAI、DeepSeek、小米、Kimi、智谱/Z.AI、豆包、MiniMax、千问、混元、Claude、Grok 等参考档；支持新增/编辑价格预设（`$`、`¥`、Credits 三种单位）。
- 套餐设置：
  - 可记录实际购买套餐和金额。
  - 当前默认示例：`2026-05-01 - 2026-06-01 Plus ¥128`、`2026-06-02 - 2026-07-02 Pro 20x ¥1380`。
  - 会尝试从本地 Codex sqlite 数据库自动识别套餐记录；识别不到时保留手动设置。
- 重置机会设置：
  - 可手动记录 Codex rate limit reset bank 的获得时间、过期时间、是否已用。
  - 可一键从 OpenAI 账户接口同步重置卡（使用本机 `~/.codex/auth.json` 的 access_token，仅在你点击同步时发起）。
  - 当前默认示例包含 `2026-06-16`、`2026-06-24`、`2026-06-27` 三次机会，过期默认按获得时间 + 30 天。
- 缓存详情窗口：查看后台缓存预热进度（按来源分类的完成天数、进度条、最近活动日志），可在暂停后手动恢复。

## 运行环境

- Windows 10/11
- .NET SDK 8.0+

项目主界面使用 WPF，目标框架是 `net8.0-windows10.0.19041.0`。少量设置窗口仍复用 WinForms 对话框。

## 构建

```powershell
dotnet build .\CodexTokenMonitor.slnx -c Release
```

CI（GitHub Actions，`.github/workflows/build.yml`）会在 push / PR 时在 `windows-latest` 上执行 restore + Release 构建。

## 发布单文件 exe

默认 Release 为框架依赖单文件，约 50 MB，需要 .NET 8 Desktop Runtime 和 ASP.NET Core 8 Runtime（局域网共享服务使用）。正式输出仍为 `outputs/CodexTokenMonitor`：

```powershell
dotnet publish .\src\CodexTokenMonitor.Wpf\CodexTokenMonitor.Wpf.csproj -c Release -o .\outputs\CodexTokenMonitor
```

生成文件：

```text
outputs/CodexTokenMonitor/CodexTokenMonitor.exe
```

轻量版（`Lite` 配置，框架依赖）只打包应用和依赖，要求本机已安装 .NET 8 Desktop Runtime 和 ASP.NET Core 8 Runtime，exe 体积会小很多：

```powershell
dotnet publish .\src\CodexTokenMonitor.Wpf\CodexTokenMonitor.Wpf.csproj -c Lite -o .\outputs\CodexTokenMonitor-lite
```

生成文件：

```text
outputs/CodexTokenMonitor-lite/CodexTokenMonitor.exe
```

一键发布脚本（双击运行，自动定位项目并发布到 `outputs\CodexTokenMonitor`，可用 `--no-pause` / `--no-open` 控制）：

```text
outputs/一键生成CodexTokenMonitor.cmd
```

局域网共享入口为 **数据管理 → 局域网共享**：默认端口 **36666**，手动开启本机服务；其他电脑填写地址和访问密钥，手动上传本周或下载并合并本周。使用说明见 [用户指南](docs/USER_GUIDE.md#局域网共享手动上传--下载本周)。

## 本地数据位置

监控器自己的缓存与设置默认放在：

```text
%LOCALAPPDATA%\CodexTokenMonitor\
```

主要包括：

- token 统计缓存（SQLite，含每日汇总、明细事件）
- quota 快照缓存（SQLite + `quota-history-v2.jsonl` 历史额度时间线）
- 价格设置、套餐设置、重置机会设置（SQLite）
- `wpf-last-display-v2.json`：上次关闭时的显示状态

读取的数据来源通常包括：

- `%USERPROFILE%\.codex\sessions` / `archived_sessions`：Codex 会话 JSONL（token_count 事件）
- `%USERPROFILE%\.codex` 下与 quota / state 相关的本地 sqlite/json 日志
- `%USERPROFILE%\.codex\auth.json`：仅“同步重置卡”时读取 access_token
- `%USERPROFILE%\.claude`（Claude Code 日志）、ZCode / WorkBuddy 的本地日志目录
- `%USERPROFILE%\.dsh\sessions`：DeepSeek Harness 会话日志（`session.jsonl.zstd`，zstd 多帧拼接的 JSONL）

不同版本客户端日志结构可能变化，所以统计器会尽量容错。

## 跨电脑合并 Codex 数据

1. 在每台电脑点击“数据管理”，按需要选择“导入其他电脑数据…”“仅导出今天数据…”“仅导出本周数据…”或“导出全部数据…”，生成 `*.codex.json` 数据包（旧版 `*.codex-data.json` 仍可导入）。今天和本周都按北京时间计算，本周从周一 00:00 开始。
2. 将这些文件复制到作为主统计端的电脑。
3. 将一个或多个数据包直接拖到程序窗口，确认后即可合并；也可以在“数据管理”中点击“导入其他电脑数据…”手动选择。

数据包包含已缓存的 Codex token 事件和 5h / 7d 额度快照，不包含价格、套餐和重置券设置。导入按稳定事件 ID（事件时间 + token 数组合键，或事件自带 key）和额度快照键合并，同一数据包可重复导入而不会重复计数；“刷新日”只重建本机日志缓存，已导入的跨电脑数据会保留。

> 只应合并同一个 Codex 账户的数据；不同账户的额度百分比没有可比性。

## 费用口径

费用使用本地 token 事件估算：

```text
cost = uncached_input_millions * input_price
     + cached_input_millions * cached_input_price
     + output_millions * output_price
```

注意：

- Reasoning output 已包含在 output 中，不重复计费。
- 单事件 input 超过 27.2 万 token 时记为“长上下文事件”，在明细行单独统计。
- Codex 本地 token 和远端套餐额度不是同一个概念，额度估算只是用百分比变化做反推。
- Fast / 普通模式可能有不同额度倍率，历史区间混合模式时只能作为近似值。
- 小米 MiMo Credits 默认按 token plan 展示，也可切换到 API 价格档。

## 开发说明

项目分层：

```text
src/CodexTokenMonitor.Core/CodexTokenMonitor.Core.csproj   日志、缓存、统计与额度计算（net8.0）
src/CodexTokenMonitor.Wpf/CodexTokenMonitor.Wpf.csproj    WPF 界面与本地对话框（net8.0-windows）
src/CodexTokenMonitor.LegacyDialogs/*.cs                   WPF 暂时复用的 WinForms 设置对话框
tests/CodexTokenMonitor.Core.Tests/                        核心回归测试
```

运行测试：

```powershell
dotnet test .\tests\CodexTokenMonitor.Core.Tests\CodexTokenMonitor.Core.Tests.csproj -c Release
```

实时日志采用“首次完整读取、后续按文件尾部增量读取”的方式（`LiveFileTailReader` 按文件维护读取游标）。活动 JSONL 被截断、替换、清理缓存或查询需要更早覆盖范围时，读取游标会自动失效并安全回退；历史完整日仍以 SQLite 数据为准。子代理（subagent）会话文件的“父任务回放”段会被过滤，只统计子任务边界之后的 token_count。后台缓存与前台刷新共用 I/O 闸门，避免同时扫描。

关键文件：

- `src/CodexTokenMonitor.Wpf/MainWindow.xaml(.cs)`：WPF 主界面（来源 Tab、额度面板、范围选择、指标卡、时间轴、明细表、数据管理）。
- `src/CodexTokenMonitor.Wpf/QuotaEstimateWindow.xaml(.cs)`：额度估算窗口（当前 5h/7d、历史周期表、手动估算、内嵌额度曲线）。
- `src/CodexTokenMonitor.Wpf/QuotaCostCurveWindow.xaml(.cs)` + `QuotaCostCurveCalculator.cs` + `QuotaCostCurveControl.cs`：额度费用曲线窗口。
- `src/CodexTokenMonitor.Wpf/WpfTokenTimelineControl.cs`：ScottPlot token 时间轴控件。
- `src/CodexTokenMonitor.Wpf/BackgroundCacheWarmer.cs`：后台历史缓存预热；`CacheDetailsWindow.xaml(.cs)`：缓存详情窗口。
- `src/CodexTokenMonitor.Wpf/LastDisplayStore.cs`：恢复上次显示状态。
- `src/CodexTokenMonitor.Wpf/WeekWindowPicker.cs`：7 天窗口选择对话框。
- `src/CodexTokenMonitor.Core/CodexUsageReader.cs`：Codex 日志、token、quota 读取与缓存（含 `SubagentReplayFilter` 子代理过滤）。
- `src/CodexTokenMonitor.Core/CodexAppServerQuotaReader.cs`：通过本地 app-server 协议实时读取 5h/7d 额度。
- `src/CodexTokenMonitor.Core/CodexCliLocator.cs`：自动发现可运行的 Codex CLI。
- `src/CodexTokenMonitor.Core/CodexQuotaCycle.cs`：7d 额度周期的识别与异常快照剔除。
- `src/CodexTokenMonitor.Core/QuotaEstimateCalculator.cs` / `QuotaPace.cs` / `QuotaSnapshotLookup.cs`：额度估算、重置评估、快照查询。
- `src/CodexTokenMonitor.Core/ClaudeUsageReader.cs` / `ZCodeUsageReader.cs` / `WorkBuddyUsageReader.cs` / `DshUsageReader.cs`：其他来源日志读取（DSH 读取 zstd 压缩的会话日志，按 frame 解压并容忍不完整尾帧）。
- `src/CodexTokenMonitor.Core/UsageSourceReader.cs` / `UsageModules.cs`：来源抽象与查询模块（含显示缓存）。
- `src/CodexTokenMonitor.Core/CodexDataTransferService.cs`：跨电脑数据包导出/导入（幂等合并）。
- `src/CodexTokenMonitor.Core/PriceSettings.cs`：价格档案、分组默认值与价格库。
- `src/CodexTokenMonitor.Core/SubscriptionPlan*.cs`：套餐/实际花费设置和导入。
- `src/CodexTokenMonitor.Core/ResetOpportunities.cs`：rate limit reset bank 数据、汇总与接口同步。
- `src/CodexTokenMonitor.LegacyDialogs/*.cs`：WPF 暂时复用的设置对话框。

## 隐私

这个工具的目标是本地观测，不会主动联网上传日志。公开仓库不包含个人日志、缓存数据库或发布产物。

唯一的联网行为是“重置设置 → 同步重置卡”时，使用本机 `~/.codex/auth.json` 中的 access_token 向 OpenAI `chatgpt.com` 后端接口查询 rate-limit reset credits，并仅把结果写入本地缓存；不点击同步就不会发起任何网络请求。额度读取通过本机 `codex app-server` 进程完成，属于本地回环通信。

## 项目演进记录

从最初的 token 统计脚本到当前桌面监控器的需求讨论和迭代记录，见：

- [`docs/history/2026-06-22-codex-token-monitor-thread.md`](docs/history/2026-06-22-codex-token-monitor-thread.md)

该档案只包含用户与 Codex 的文字消息，不包含截图二进制、工具原始输出、系统指令或访问凭证。Codex 原始任务仍由桌面应用保存在用户目录中。

更多文档：

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)：架构与数据流说明。
- [`docs/USER_GUIDE.md`](docs/USER_GUIDE.md)：用户使用指南。
- [`docs/CHANGELOG.md`](docs/CHANGELOG.md)：版本变更记录。
