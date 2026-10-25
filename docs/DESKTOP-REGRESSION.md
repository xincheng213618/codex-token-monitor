# 桌面回归入口

在 Windows 的 PowerShell 7 中，从仓库根目录运行：

```powershell
.\Scripts\Test-Desktop.ps1
```

脚本先构建整个 solution，然后依次启动主窗口、设置窗口、分析窗口和速度窗口四组测试。每组使用单独的 STA 进程、缓存与来源日志目录；不加载真实账户，不启动共享服务。主窗口由测试直接调用刷新入口，始终不触发 Loaded；设置、分析和速度窗口使用真实 Loaded/Closed 生命周期。速度组覆盖独立采样、完成后校正、七来源/全部来源切换、总吞吐量、峰值、历史持久化/导出、损坏备份恢复及关闭后的结果抑制，并渲染正常与最小尺寸。

solution 使用 `.slnx`，应用、Core 与测试均以 .NET 10 为目标；`global.json` 选择稳定版 10.0 SDK，本机使用 10.0.401，CI 安装 10.0.x。轻量程序需要 .NET 10 Desktop Runtime 和 ASP.NET Core 10 Runtime，自包含发布携带运行时。

## 常用命令

```powershell
# 已构建且仅重跑主窗口；会校验探针中的 Core/WPF DLL 与生产构建一致。
.\Scripts\Test-Desktop.ps1 -NoBuild -Suite main

# 调试配置；仍从独立目录读取和写入测试数据。
.\Scripts\Test-Desktop.ps1 -Configuration Debug -Suite analysis

# 较慢机器可以调整每组超时；默认每组 120 秒、构建 300 秒。
.\Scripts\Test-Desktop.ps1 -TimeoutSeconds 180 -BuildTimeoutSeconds 600

# 检查运行器的失败识别、子进程终止、参数/日志和真实 CLI 参数拒绝。
.\Scripts\Test-DesktopRunner.ps1 -IncludeProbeCli

# 无需桌面环境的 Core 与纯页面状态测试另行运行。
dotnet test .\tests\CodexTokenMonitor.Core.Tests\CodexTokenMonitor.Core.Tests.csproj -c Release
```

`-Suite` 接受 main/settings/analysis/speed，PowerShell 大小写均可；多个值使用数组，例如 `-Suite main,analysis`。`-OutputRoot` 可指定报告根目录，相对路径按仓库根目录解析。每次运行都会创建新的时间戳与 GUID 子目录，不覆盖、清理旧证据。`-NoBuild` 验证的是已有构建产物，不代表源码变更已经编译；修改代码后运行默认命令。

## 输出与判定

默认输出在忽略目录 `artifacts/desktop-probes/<时间戳-GUID>/`：

- `desktop-regression.json`：整次运行结果、配置、开始/结束时间、进程退出码、超时、加载产物哈希及各组报告位置。
- `build.stdout.log` / `build.stderr.log`：构建日志。
- `<suite>.stdout.log` / `<suite>.stderr.log`：每组 UTF-8 日志。
- `<suite>/ui-probe-results.json`：该进程的 schema、suite、PID、实际加载 Core/WPF 哈希、行为断言及渲染记录。
- `<suite>/*.png`：实际 WPF 窗口渲染；`isolated-*` 子目录保存该组生成的缓存、日志和损坏/恢复夹具。

整体成功必须同时满足：构建成功、进程未超时且退出码为 0、报告明确通过且无错误、PID 和两份程序集哈希匹配、关键隔离/取消/恢复行为记录存在、PNG 位于当前组目录并能实际解码且尺寸一致。不会用固定检查总数代替这些条件。失败组保留日志与夹具，其余组继续独立运行，最终脚本返回 1。

探针 CLI 返回码：0 为通过，1 为运行或断言失败，2 为参数或输出目录无效。直接运行 CLI 必须显式指定 `--suite` 与 `--output`；非空目录会被拒绝，防止误读或覆盖旧报告。运行器超时只终止自己创建的子进程树。

## 当前覆盖

| 组 | 主要边界 |
| --- | --- |
| main | 七来源范围读取/往返切换、周期 gate/LRU、最新请求生效、绑定、共享导入、设置/缓存故障与恢复、无品牌图标的单行工具栏、底部持续测速与按需实时视图、同 Rect 明细/实时页签、分析窗口入口、关停排空 |
| settings | 设置窗口加载/保存、失败禁用与编辑保留、修复后保存、真实窗口关闭与运行时排空 |
| analysis | 缓存分析绕 gate、只读曲线、损坏保留/恢复、周期分析、单窗口取消与父运行时关停 |
| speed | 七来源、独立测速、估算校正、吞吐/峰值、窗口内部历史/导出与返回、损坏恢复及采样/索引关闭排空 |

检查和图像数量以当前运行生成的报告为准。测试创建的主窗口和独立窗口均追踪真实 Closed。隔离作用域覆盖启动、Dispatcher 工作和失败清理；设置失败测试通过禁用控件与本地写入边界验证保护，不调用账户同步或导入处理程序。

主窗口组额外生成一份隔离的原生 Codex 日志，验证底部速度在持有主统计 gate 时仍可采样、自动读取追加记录、遵守自动刷新开关与来源变化，且不受下方页签选择影响；结束时逐文件核对来源夹具哈希，禁止意外来源写入。

`RealtimeDataView` 与左下角共享主采样快照，不额外读盘：仅选中实时数据页时更新指标、曲线和会话，离页冻结这些视图但底部继续采样，返回时应用最新快照。布局验证覆盖无品牌图标的单行顶部入口、底部缓存状态、独立上方趋势/分隔条不动，以及下方同一白色 `DetailsCard` 的标题行页签。该区域保留 12,10 内边距，两个页面和 Tab 内容零额外边距、无独立框；只切换原列表区域，复用同一 Rect，模块位置不动。正常和最小窗口尺寸下均检查实时页五指标条始终可见、无重复来源标题/刷新行，以及左侧主要曲线与最右约 240 px 会话栏的左右布局。

完整测速及历史验证通过独立速度分析窗口进行，历史在该窗口内切换并可返回实时，其生命周期与主视图选择独立。主窗口本身仍不进入 Loaded，不发起账户或共享服务请求。

`Test-DesktopRunner.ps1` 使用合成报告与短生命周期子进程验证运行器自身，不作为产品窗口行为证明。带 `-IncludeProbeCli` 时还运行缺参数、重复参数、未知组和非空目录拒绝检查，需已有对应配置的探针构建。

## 维护与 CI

正式源码在 `tests/CodexTokenMonitor.Wpf.Probes/`，独立程序集通过明确友元访问应用内部状态；不再借用 Core.Tests 的程序集名称。新增场景优先沿用独立夹具和真实窗口事件，禁止通过取消隔离或启用账户网络补足测试。修改必需行为或图像名称时，同时更新 `DesktopProbeRunner.psm1` 的验收集合。

GitHub Actions 的 `desktop regression` 工作流支持相关路径 PR、每周计划及手动选择分组，运行同一入口并上传报告和截图。本次本地验收不代表远端 hosted runner 已执行通过。`build` 工作流继续进行 Core 测试和自包含发布冒烟检查。

当前测试不覆盖真实账户联网、设备间共享 HTTP 传输、安装包升级或长时间性能测量。这些场景按相关功能迭代单独验收，不能由离屏渲染和合成数据推断通过。
