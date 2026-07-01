# Codex Token Monitor

一个 Windows 桌面额度监控器，用来从本地日志统计 Codex、Claude Code、ZCode 的 token 用量、缓存命中、估算 API 等价费用，以及 Codex 5h / 7d 额度百分比变化。

> 数据只读取本机日志和本机缓存，不会上传到远端。价格、套餐和额度估算都只是本地辅助分析，最终以官方账单和产品页面为准。

## 功能

- 统计 Codex / Claude Code / ZCode token 使用量。
- 支持按天、近 7 天窗口、按月查看。
- 支持“从当前算”，方便比较同一任务在不同 AI 工具里的消耗。
- 展示 input、cached input、uncached input、output、reasoning output、缓存命中率、事件数和 Coding Time。
- 以时间轴图表查看当天/周/月 token 峰值。
- 用 SQLite 缓存历史统计，历史日期切换更快。
- Codex 额度估算：
  - 读取本地捕获到的 5h / 7d 剩余额度百分比。
  - 根据百分比变化和本地 token 消耗估算 100% 额度价值。
  - 支持历史 7d 周期表、手动百分比区间估算。
- 价格设置：
  - 默认展示 GPT-5.5、DeepSeek V4 Pro、小米 MiMo Credits 三档。
  - 内置可编辑价格库，包含 OpenAI、DeepSeek、小米、Kimi、智谱/Z.AI、豆包、MiniMax、千问、混元、Claude、Grok 等参考档。
- 套餐设置：
  - 可记录实际购买套餐和金额。
  - 当前默认示例：`2026-05-01 - 2026-06-01 Plus ¥128`、`2026-06-02 - 2026-07-02 Pro 20x ¥1380`。
  - 会尝试从本地 Codex sqlite 数据库自动识别套餐记录；识别不到时保留手动设置。
- 重置机会设置：
  - 可手动记录 Codex rate limit reset bank 的获得时间、过期时间、是否已用。
  - 当前默认示例包含 `2026-06-16`、`2026-06-24`、`2026-06-27` 三次机会，过期默认按获得时间 + 30 天。

## 运行环境

- Windows 10/11
- .NET SDK 8.0+

项目使用 WinForms 和 Windows App SDK 无关，目标框架是 `net8.0-windows`。

## 构建

```powershell
dotnet build .\CodexTokenMonitor.slnx -c Release
```

## 发布单文件 exe

便携版会把 .NET runtime 一起打进 exe，体积较大，但复制到没装 .NET 的 Windows 机器也能直接运行：

```powershell
dotnet publish .\src\CodexTokenMonitor\CodexTokenMonitor.csproj -c Release -o .\outputs\CodexTokenMonitor
```

生成文件：

```text
outputs/CodexTokenMonitor/CodexTokenMonitor.exe
```

轻量版只打包应用和依赖，要求本机已安装 .NET 8 Desktop Runtime，exe 体积会小很多：

```powershell
dotnet publish .\src\CodexTokenMonitor\CodexTokenMonitor.csproj -c Lite -o .\outputs\CodexTokenMonitor-lite
```

生成文件：

```text
outputs/CodexTokenMonitor-lite/CodexTokenMonitor.exe
```

## 本地数据位置

监控器自己的缓存与设置默认放在：

```text
%LOCALAPPDATA%\CodexTokenMonitor\
```

主要包括：

- token 统计缓存
- quota 快照缓存
- 价格设置
- 套餐设置
- 重置机会设置

读取的数据来源通常包括：

- `%USERPROFILE%\.codex\sessions`
- `%USERPROFILE%\.codex\archived_sessions`
- `%USERPROFILE%\.codex` 下与 quota / state 相关的本地 sqlite/json 日志
- Claude Code / ZCode 的本地日志目录

不同版本客户端日志结构可能变化，所以统计器会尽量容错。

## 费用口径

费用使用本地 token 事件估算：

```text
cost = uncached_input_millions * input_price
     + cached_input_millions * cached_input_price
     + output_millions * output_price
```

注意：

- Reasoning output 已包含在 output 中，不重复计费。
- Codex 本地 token 和远端套餐额度不是同一个概念，额度估算只是用百分比变化做反推。
- Fast / 普通模式可能有不同额度倍率，历史区间混合模式时只能作为近似值。
- 小米 MiMo Credits 默认按 token plan 展示，也可切换到 API 价格档。

## 开发说明

主项目：

```text
src/CodexTokenMonitor/CodexTokenMonitor.csproj
```

关键文件：

- `CodexUsageReader.cs`：Codex 日志、token、quota 读取与缓存。
- `ClaudeUsageReader.cs`：Claude Code 日志读取。
- `ZCodeUsageReader.cs`：ZCode 日志读取。
- `Form1.cs`：主界面。
- `QuotaEstimateForm.cs`：额度估算窗口。
- `PriceSettings*.cs`：价格档案设置。
- `SubscriptionPlan*.cs`：套餐/实际花费设置和导入。
- `ResetOpportunity*.cs`：rate limit reset bank 手动记录和展示。

## 隐私

这个工具的目标是本地观测，不会主动联网上传日志。公开仓库不包含个人日志、缓存数据库或发布产物。
