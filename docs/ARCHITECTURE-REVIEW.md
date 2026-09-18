# 架构基线与迭代路线

检查日期：2026-09-12，2026-09-18 增补第八、九轮。基于当前代码及隔离测试，记录本轮已落地的边界和后续工作。

## 判断

继续使用现有 WPF + Core 结构。`src` 中已没有 WinForms / WindowsFormsHost 依赖，计算器、来源读取接口、主题和图表组件也已有独立边界。当前维护成本主要来自窗口承担查询调度、Core 同时包含存储和业务规则、以及静态依赖与隐式并发约束。再次更换 UI 框架不能直接解决这些问题。

暂时保留两个生产项目，不引入通用 DI 容器或一次性改写全部 MVVM。先把有行为契约、可以独立测试的职责移出窗口；纯 UI 事件、图表、对话框和控件布局继续由 WPF 管理。

## 分阶段调整后形成的边界

| 组件 | 负责 | 不负责 |
| --- | --- | --- |
| `MainWindow` | 采集界面选择、应用仍有效的查询结果、展示缓存故障、协调 WPF 关闭事件 | 日/周/月/周期读取算法、请求队列状态机、后台任务台账 |
| `MonitorRuntime` | 共用 I/O 锁、生命周期令牌、任务登记、停止接收与限时等待、延迟释放资源 | WPF 定时器、控件状态、主动启动服务 |
| `AnalysisQuerySession` | 分析窗口自己的取消边界、父运行时任务登记、后台执行、可选共用锁、返回缓存诊断 | WPF 控件、各个结果区域的显示状态 |
| `QuotaCycleAnalysisQueryService` | 原周期刷新范围、分析及模型校准的组合、失败时阻止校准 | 查询调度、图表渲染 |
| `LatestRequestRunner<TRequest>` | 串行执行、合并等待请求、版本有效性、传播错误与取消 | 读日志、操作缓存、决定如何显示错误 |
| `UsageQueryService` | 根据请求组合来源读取、历史缓存修复、明细分桶、Coding Time、额度锚点与结果 | WPF 控件、可变来源页面状态、调度线程或自行获取 I/O 锁 |
| `CacheOperationDiagnostics` / `CacheDatabaseState` | 收集一次操作的缓存故障、控制初始化重试、区分不可用与成功空结果 | 删除或重建数据库、显示对话框、决定业务回退 |
| `MonitorSettingsTable<T>` / `PriceSettingsStore` | 按路径保存最近成功设置、提供操作内固定快照、写前校验与失败恢复 | 将不可读配置当作用户清空、在窗口构造时保存回退配置 |
| `UsageSourceRegistry` | 来源顺序、标题、价格组与别名、Reader 单例及窄能力视图 | 读取设置或日志、页面工厂和状态、将额度能力等同于 Codex 周期 |
| `IUsageCacheQuery` / `IUsageQuery` / `IUsageCacheMaintenance` | 分别提供应用缓存查询、可扫描查询、清理/回填；原 `IUsageSourceReader` 保留组合入口 | 依接口名称自动决定锁、主窗口状态和界面呈现 |
| `UsageDisplayViewModel` | 不可变统计文本快照、成功/空/失败保留状态、状态提示与复制可用性、停止后冻结 | 数据查询、来源选择/LRU、图表绘制、价格和额度计算、显示持久化 |
| WPF `UsageSourceModule` / `UsageSourceModules` | 每窗口独立的来源选择、日期/周期状态、最近成功显示、容量 8 的 LRU、桌面页面工厂 | 持有 Reader、扫描或维护缓存、全局注册表、查询算法 |
| `QuotaModelCapacityCalibrationStore` | 按路径完整加锁、初始化、校准事务读写及故障报告 | 默认值回退、校准公式、重建丢失的已知配置 |
| 现有计算器 | 统计、计费、额度周期、消耗时间线和预测规则 | 窗口生命周期 |

领域与查询边界仍放在 Core 程序集中。显示 ViewModel 与 `UsageSourceModule` 的选择/LRU 位于 WPF 项目，均不依赖 WPF 控件类型；查询范围和结果模型独立留在 Core，显示持久化格式保持。

第一轮将主窗口主文件从 2,511 行降至 2,187 行，第二轮接入故障状态与生命周期后为 2,215 行。有意义的变化是查询、调度与关停已有独立测试入口，行数仅用来说明移出的范围。

### 用量查询

窗口在 UI 线程生成 `UsageQueryRequest` 或 `UsageCacheQueryRequest`，捕获对应查询能力、范围及已有额度；取得 `usageQueryGate` 后，通过 `Task.Run` 调用服务，返回 UI 线程再更新窗口。服务只读取请求，不再捕获 `MainWindow`、当前控件或窗口的取消源。

- **CacheOnly**：只读本应用持久缓存，不扫描来源的原始会话日志，也不修复用量缓存。日视图缺明细时先显示已有汇总，“从当前算”先读缓存明细；后续正常刷新补齐。修正了旧日视图事件数不一致、旧 custom-start 分支仍扫描原始日志的问题。
- **正常刷新**：保留当天增量、历史缺明细修复、周/周期 10 分钟分桶、月度明细时间线及 Coding Time 口径。完整性检查截止北京时间今天开始，避免将今天尚未结束的缓存当成历史缺口。
- **额度缓存**：`quota-history-v2.jsonl` 是本应用维护的额度缓存，继续参与首屏额度回退和时间线物化。CacheOnly 不等于只允许 SQLite；不应因文件扩展名丢弃已有数据。
- **结果保留**：查询返回完整 `DetailRows`；现有 `UsageSourceModule` 仍在保存显示/LRU 时剥离它，避免每个历史页面持有大量事件。表格、CSV 与额度查询共用事件桶判定。

### 调度与一致性

刷新协调器同时最多执行一个请求、保留一个等待请求。快速切换日期或来源时，等待项被最新项替换；正在执行的旧项可结束，但 `IsCurrent(version)` 阻止其覆盖新选择。等待 I/O 锁后再次检查版本，过时请求不再启动扫描。

一次请求突发中的调用方等待同一个 Completion。成功排空、异常或取消后都恢复空闲；异常和取消继续传播给调用方。普通异常/取消丢弃等待项，生命周期未取消时可以重新请求。回调在发起请求的 `SynchronizationContext` 开始；回调自身必须保留 await 上下文才能继续操作 UI。

主窗口、缓存预热、数据导入导出和共享存储使用 `MonitorRuntime.SharedIoGate`。**主窗口刷新中，仅已完成的内存显示缓存命中可以绕过锁**；服务查询及周期完整性判定必须在同一个临界区内。旧代码按“历史周期且非实时”绕过锁，但内部可能补扫历史日志或写入额度锚点，第一轮已封闭该入口。手动请求取消当前预热后获取锁，自动请求在锁忙时跳过，待下一次定时刷新重试。第三轮将分析窗口按实际读写行为接入同一运行时，具体边界见下文。

### 缓存故障与恢复

`UsageCacheStore` 和 `QuotaSnapshotCacheStore` 保留原查询返回契约，同时向当前操作的 `CacheOperationDiagnostics` 报告锁定、损坏、无访问权限等故障。服务将诊断快照放入 `UsageQueryResult.CacheWarnings`；周期完整性判定在自己的诊断作用域内执行，并与查询警告合并。作用域通过 `AsyncLocal` 传递，默认相互隔离；第三轮增加显式 `propagateToParent: true`，在结束作用域（含异常展开）时将警告转交父操作。查询服务、周期列表与校准采用该模式，既能在内部阻止失败结果持久化，也不会丢失上层提示。

主窗口在保存或应用结果之前检查警告。失败时保留上次成功显示；没有成功记录则明确显示“统计暂不可用”，避免将读取失败展示为零用量。状态提示包含重试说明，悬浮可查看缓存路径与具体操作。失败结果不进入模块显示缓存或 LRU；额度摘要、周期列表和后台预热也分别检查自己的缓存诊断。

初始化或访问失败后，store 不再永久停用：同一诊断操作内限制重复初始化，下一次操作可使用同一个实例重新尝试。严格写入和导入导出在不可用时抛出异常，不再把空导出或未完成导入当成成功。故障恢复不删除、重建或迁移数据库，也未改变原始日志过滤及计费规则。第四轮将价格、套餐与重置卡设置接入相同的诊断边界。

### 生命周期与最后一次保存

`MonitorRuntime.Run` 在调用业务函数前登记任务；函数仍在调用方线程与同步上下文开始执行。主窗口刷新、额度与周期刷新、重置同步、缓存预热、手动导入导出、共享窗口操作、最近数据及历史数据 HTTP 存储操作均登记到运行时。历史共享将请求取消令牌与运行时令牌链接，包含等待共用锁的阶段。

第一次 `Closing` 暂缓关闭，停止定时器、禁用界面并关闭所属窗口；最后一次显示保存及共享服务停止也加入任务台账，然后停止接收新任务、发出取消，并使用一个 **3 秒总等待预算**。等待期间保留 Dispatcher 消息泵，排空或超时后才真正关闭。超时不强行释放仍由已登记任务使用的锁；这些任务及取消回调结束后才释放资源。运行时观察后台异常，并仅保留最近 128 条失败。

`LastDisplayStore.FlushAsync` 与防抖写入的存储 await 均不捕获 UI 上下文，避免同步兼容入口等待写锁时与 UI 续体相互阻塞。Flush 取得写锁后重新读取最新待保存状态，保存期间新产生的显示不会被旧版本覆盖；现有 v5 内容格式与文件路径保持不变。分析子窗口通过 `AnalysisQuerySession` 同时登记窗口自身与父运行时；关闭单个窗口只取消自己的查询，应用关停仍等待尚未退出的分析任务。

第五轮回归发现并修复了一处取消竞态：`CancelAsync` 已将原令牌标记取消，通知 linked token 的回调却可能尚未执行。查询会话在工作执行前、成功后及最终返回前同时检查窗口、父运行时和请求原令牌，拒绝这段时间内的迟到成功。测试通过阻塞取消回调确定性复现；通用运行时允许已登记排空任务完成的原契约保持。

### 分析窗口的查询边界

额度估算、独立费用曲线、周期分析三个窗口的加载，以及手动估算、右键定位周期，统一经过 `AnalysisQuerySession`。每次查询在后台建立独立诊断作用域并返回值与警告；主加载、手动估算和周期定位分别限制重复执行，互不共享一个请求队列。窗口关闭后，成功、异常和 finally 路径都检查停止状态，避免更新已经关闭的 UI。

| 入口 | 共用 I/O 锁 | 行为 |
| --- | --- | --- |
| 额度摘要/历史列表、手动百分比估算、周期定位 | 不等待 | 读取已保存用量与应用额度历史，不扫描来源日志 |
| 内嵌与独立费用曲线 | 不等待 | 读取已有用量，缺失额度点仅在内存投影，不保存派生锚点 |
| 当前或历史周期分析 | 等待 | 当前周期可能补扫原始日志；两类分析都会物化额度点、读写模型校准 |

`ReadCachedQuotaTimeline` 与原 `ReadMaterializedQuotaTimeline` 共用投影算法；只读入口不保存缺失锚点，原入口继续物化。只读指查询不保存派生数据，store 首次加载仍沿用既有 SQLite 初始化。`QuotaCostCurveCalculator` 和纯范围规则 `QuotaAnalysisRefreshRange` 已移入 Core，移除了曲线中未使用的 gate 参数。

`QuotaCycleAnalysisQueryService` 保持“刷新原来打开的周期，不跟随后继周期”的规则；原始快照读取失败时不再启动分析，分析含警告时不写校准。补算上一周期校准时也检查诊断，避免不完整数据覆盖已保存的校准。`ReadWeeklyCycles` 禁止将带警告的结果放进两分钟内存缓存，并把数据库路径纳入键，避免恢复后仍命中失败产物或跨目录串数据。

分析页失败时保留已有图表、历史表和手动估算文本；首次失败明确提示暂不可用。成功恢复清除故障提示。估算加载结果携带本次完整周期列表，右键分析可正确传入上一周期，即使打开窗口时没有预传周期。第四轮补齐了套餐读取诊断，校准服务在读取失败后停止写入，避免回退套餐污染已保存的模型校准。

### 设置存储与编辑

价格 JSON、套餐与重置卡 SQLite 的读取失败均报告诊断。套餐和重置卡返回同路径的最近成功记录；没有成功记录时返回带失败诊断的空集合，调用方不能把它当作有效空配置。价格保留稳定的最近成功对象，首次失败的默认值仅供旧接口兼容，不能通过编辑窗口或自动补充模型保存。写入先确认当前文件或全部原表记录可读取，再进行原子替换或事务写入；失败保留文件与当前编辑。

设置快照按实际诊断作用域保存在弱引用表中。一次操作只读取一次，各并发操作保留各自的快照；新的操作或显式重新读取可以恢复。价格的无作用域 `Current` 消费已加载快照，主窗口显示、缩放和明细计算不反复读设置文件。价格原有版本归一化规则保持不变；已有旧格式设置仍会按原规则归一化保存。显式修改成功后更新当前操作与全局成功快照。

套餐和重置卡只在首次创建表时事务写入示例。已有空表代表用户明确清空，不再自动填回。已观察到的配置文件或表后来消失时，连续重试仍报告不可用，不创建替代数据；恢复同路径原数据后可继续读取。价格也保留首次坏文件的已知状态，文件消失不能转为成功默认值。重置同步必须收到有效 `credits` 数组和完整合法记录才可保存；合法空数组仍能清空，缺字段、错误类型、非法时间或取消不能覆盖本地卡片。

三个设置窗口通过 `AnalysisQuerySession` 异步加载和保存。首次失败停用编辑、默认恢复和保存并提供重新读取；保存失败保留编辑与窗口，成功后才返回已保存。打开价格窗口发现的模型先加入编辑副本；主窗口自动补充模型则在已登记的后台查询和共用锁内执行，并检查诊断。

主窗口套餐与重置卡摘要独立后台读取，只发布成功快照。一个设置表损坏不会阻止另一个摘要或 Token 查询；旧摘要标记“沿用上次设置”，首次失败显示暂不可用。普通刷新合并正在执行的读取，设置保存成功最多追加一次读取。这里的 `MainWindow.Settings.cs` 是窗口呈现代码的归类，尚未视为 ViewModel 解耦。

### 来源读取文件拆分

第四轮将两个 SQLite store 和相关 DTO 分别移到 `UsageCacheStore.cs`、`QuotaSnapshotCacheStore.cs`，将 `ScanRange` 与回放过滤器移到 `UsageLogScanning.cs`。拆分时七个类型正文逐一校验一致，按原顺序重组与备份文本完全相同；SQL、缓存版本和回放算法保持。证据在 `work/architecture-round4/split-verification.json`。第五轮另行移除 reader 中的全局测试路径覆盖，与纯文件移动分开验证。

### 来源注册与测试路径

第五轮新增 `UsageSourceRegistry` 作为有序来源元数据入口。Reader 注册、窗口 Module 创建、主窗口和价格设置 Tab、价格组别名、后台预热来源列表均从它取得定义。枚举数值固定为原有 0–4，既有标题、顺序、别名、未知值回退 Codex 的规则不变。Tab 通过来源 Tag 识别选择；顺序不再与手写 switch 绑定。价格 JSON 中各来源字段仍保持原格式。

注册阶段只创建元数据及延迟工厂，不读取价格、日志或缓存。Reader 继续使用单例，五类 Module 仍为每个窗口独立创建，Codex 的额度/周期状态和各来源 LRU 不放进全局注册表。额度能力来自定义，周期能力继续由具体 Module 决定。

最后两处 `OverrideCodexHome` 测试已改为缓存与日志双作用域，移除了 reader 的全局测试覆盖属性。两类路径作用域改为上下文中的栈节点，按 LIFO 释放；重复释放旧作用域不会覆盖后来进入的新路径，父/子异步上下文各自恢复原路径。真实 Reader 的并发测试使用相同 turn id、不同目录及不同 Token 数据，覆盖扫描与缓存写入隔离。

### 第六轮：来源能力、显示状态与校准存储

`IUsageSourceReader` 现在组合三个能力接口。查询请求和 Coding Time 回源辅助只接收 `IUsageQuery`，完整性判断只接收 `IUsageCacheQuery`；后台预热将计划读取与维护执行分开，指定日重建明确取得 `IUsageCacheMaintenance`。Registry 的窄接口视图仍指向原适配器，五个 Reader 的实现正文保持一致。

`CacheQuery` 表示读取应用缓存，不承诺 SQLite 连接没有初始化或迁移写入。`includeLiveToday:false` 的一般查询仍可能补读历史；主查询额度时间线仍有物化行为。`CacheOnly` 分支继续由服务及回归测试约束，本轮未据新接口名称取消任何共用锁。

`UsageDisplayViewModel` 接收调度器已确认有效的结果，一次发布来源、范围与统计文本快照。窗口标题、统计文本、内容/空面板、状态与详情、复制按钮通过绑定显示；加载、成功有数据、成功空结果、首次失败和保留成功结果后的失败有独立状态。成功空结果也属于最近成功结果，后续失败不会伪装成首次无数据。切换到没有成功显示的来源会清掉旧数字和警告；忙碌仅控制复制可用性，关闭后状态冻结。复制还检查模块中的摘要数据是否可用：共享导入清空模块缓存但暂不刷新时保留原显示、停用复制，下一次成功刷新后恢复。

窗口继续负责图表、明细表适配、动态费用卡片、额度与保存；`ApplySummary` 将统计文本交给 ViewModel，`ApplyEmptyModuleState` 清理对应视图对象。已移除对绑定属性的局部赋值，避免一次刷新破坏后续绑定。一般查询异常也进入失败显示，首次异常不会无限停留在“正在读取”。文本格式函数与原有复制/表格共用，计费和复制内容格式保持。

校准存储移到独立文件，操作只捕获一次数据库路径，初始化和读写在同一条按路径的锁内完成。初始化成功才设置状态；失败记录诊断并抛出原异常，后续调用可恢复。已知数据库或表消失不创建替代数据，恢复原路径后重试；批次写入继续事务提交，任意一条失败均回滚前面的更新。SQL、数值格式、模型估算公式和套餐失败守卫保持。

### 第七轮：缓存专用入口与页面状态归属

`UsageCacheQueryRequest` 只含 `IUsageCacheQuery`、范围和已有额度，`ExecuteCached` 无法调用来源扫描或维护方法。旧 `Execute` 的 CacheOnly 请求委托它，仍忽略同时传入的实时标志。主窗口按所需能力捕获请求，并在原临界区内执行查询、价格补充与周期完整性判定。

缓存入口复用现有分桶、Coding Time 和额度组装：普通范围保留缓存汇总，自定义起点从裁剪明细生成汇总；缺明细时不补扫，也不推造活跃时长。额度回退与时间线物化保留原契约，纯缓存查询能力不表示可绕过共用锁。复核补齐额度时间线返回后的取消检查：即使最后一个缓存读取正常返回，若令牌已经取消，普通和缓存查询都不能发布迟到成功。

`RangeMode`、`SelectedRange`、`UsageQueryResult` 原样移到 Core 的 `UsageQueryModels.cs`；`UsageSourceModule`、五类具体页面和工厂移到 WPF。页面只从 Registry 复制 Source/Title/SupportsQuota，不再持有 Reader。Core Registry 移除了桌面工厂依赖，WPF 工厂继续按 Registry 顺序为每个窗口创建独立页面，Codex 周期支持由具体页面声明。

选择 setter、LRU 与结果保存正文逐段对照一致，证据在 `work/architecture-round7/page-state-move-verification.json`。既有容量 8、命中提升顺序、失败保护、清空不重置选择、剥离大明细和 v5 显示恢复格式保持；搬迁后的纯页面代码继续作为链接源纳入无需 WPF 的行为测试。

### 第八轮：程序化控件更新的统一抑制边界

主窗口原来用 5 个 `suppress*` 布尔加 `initializing` 标志阻止控件镜像赋值触发各自的刷新处理器。布尔赋值没有 try/finally 保护：一旦赋值路径抛出，标志会永久停留，对应刷新入口就此失效；`TryRestoreLastDisplay` 捕获/恢复 `initializing` 的写法同样会在异常时把窗口留在抑制状态。

第八轮引入 `UiEventSuppressor`（WPF 项目，链接进 Core.Tests）：计数式抑制，`Begin()` 返回可释放作用域，嵌套安全、重复释放安全、深度不下穿。主窗口的 6 个标志全部移除：

- 构造函数持有整个建立过程的作用域，替代 `initializing = true/false`。
- `UpdateRangeControls` 的三个选择器镜像、`ApplyCycleOptions`、`UpdateStartNowButtonState`、`SyncRangeModeItems`、`ShiftPeriodAsync` 与 `RangeModeChangedAsync` 的单点赋值、`TryRestoreLastDisplay` 的恢复段，全部改为 `using` 作用域；镜像赋值异常时作用域随栈展开释放，不再需要手写 finally。
- 事件处理器入口统一检查 `IsSuppressing`。原五个标志的抑制范围（各自只挡自己的控件事件）收敛为一个作用域后语义不变：这些赋值块内只会触发本就被抑制的控件事件，实际事件流保持原有契约。
- `TryRestoreLastDisplay` 的 `ApplySummary` 与状态提示仍运行在作用域之外，恢复语义逐行对应；区别仅在异常路径现在会正确释放。

桌面探针原来用反射 `Set(window, "initializing", …)` 控制事件流，本轮改为按窗口持有 `UiEventSuppressor.Scope`（重复抑制幂等、释放对应一次持有），保持原布尔语义；探针因此不再依赖字段名，改依赖 `suppressUiEvents` 类型化成员。

测试：`UiEventSuppressorTests` 覆盖基本抑制、嵌套释放顺序、重复释放、异常路径释放、孤儿作用域不能解除新作用域、并行作用域计数线程安全共 6 项。

### 第九轮：Codex 来源读取去静态化

`CodexUsageReader` 原是静态类，进程内共享尾读器游标、子代理过滤器字典和额度历史缓存，测试隔离只能依赖 `AsyncLocal` 路径作用域，且字典按文件路径无限累积。第九轮把它改为实例类：

- 55 个有状态成员（`UsageTailReader`/`QuotaTailReader`、两组 `SubagentReplayFilter` 字典、额度历史缓存及其同步锁、全部读取/预热/补扫入口）改为实例成员；纯 JSON 解析与归一化辅助（`NormalizeQuotaSnapshotWindows`、`TryRead*`、`ClassifyRateLimitWindows` 等）保持静态。
- `UsageSourceReaders` 持有进程级共享实例并暴露 `UsageSourceReaders.Codex`；`CodexUsageSourceReader` 适配器改为构造注入该实例。原直接静态调用的生产代码（`QuotaEstimateCalculator`、`UsageQueryService`、`QuotaSnapshotCacheStore`、`BackgroundCacheWarmer`、`MainWindow.DataSharing` 等 17 处）改走 `UsageSourceReaders.Codex`，与适配器指向同一实例，行为不变。
- 测试改走 `UsageSourceReaders.Codex`（保持共享语义）；需要隔离的测试可以 `new CodexUsageReader()` 获得独立游标与过滤器，不再依赖全局状态。
- `DshUsageReader.OverrideSessionsRoot`（最后一个 reader 级全局测试覆盖属性）删除，DSH 测试改用 `UsageLogPaths.PushRoot`，与第五轮其他 reader 的处理一致。
- `CodexQuotaCycleReader` 仍有单条目 2 分钟周期缓存（有界备忘缓存），留待后续轮次；`MonitorCachePaths`/`UsageLogPaths` 的 `AsyncLocal` 作用域继续承担日志与缓存路径隔离，不是可移除项。

测试期观察到一次偶发 `MonitorSettingsStoreRecoveryTests` 失败（`ObjectDisposedException`，Microsoft.Data.Sqlite 连接池在并行测试下的句柄竞态），单类重跑与全量重跑均通过；疑似与 monitor-settings 低频路径的 `Pooling=true` 有关，列入后续优先级。

测试：Core 回归 553/553（本轮不改测试语义，替换调用目标）。

第十二轮（2026-09-19）把 `CodexAppServerQuotaReader` 也改为实例类：`SyncRoot` 锁、20s/10s 成功/失败缓存与已选 CLI 命令随实例持有，`ReadCurrent`/`ReadPlanTypeAsync` 为实例方法，纯协议解析（`ParseRateLimitsResponse` 等）保持静态。实例由 `CodexUsageReader` 持有并经 `UsageSourceReaders.Codex.AppServerQuota` 暴露；订阅套餐窗口与主窗口回退路径改走共享实例。两个桌面探针原来反射静态字段 `lastAttemptUtc` 断言"探针期间零 app-server 调用"，改为从共享实例读取同一实例字段，断言语义不变。至此读取与额度链路没有进程级静态可变状态。验证：Core 回归 553/553，桌面回归三组通过（报告 `artifacts/desktop-probes/20260919-011607-*/`）。

第十一轮（2026-09-19）关闭 SQLite 连接池竞态：`MonitorSettingsDatabase`、`UsageCacheStore`、`QuotaSnapshotCacheStore` 与共享历史只读连接全部改为 `Pooling=false`。生产 I/O 本就被 `MonitorRuntime.SharedIoGate` 串行化，池化没有可测收益，而并行测试负载下池化句柄已被观察到一次 `ObjectDisposedException`。`SubscriptionPlanImporter` 原本就是非池化。稳定性验证：**连续 20 次全量 Core 回归 553/553 全部通过，0 失败**，另跑三组桌面探针通过（报告 `artifacts/desktop-probes/20260919-010215-*/`）。

第十五轮（2026-09-19）给 `desktop-regression.yml` 增加每周一 02:00 UTC 定时触发与并发组：手动分发仍是主路径，定时兜底捕获跨功能迭代累积的回归，PR 门禁保持 Core 回归不变。

第十四轮（2026-09-19）把主窗口 `GetSelectedRange` 的范围解析逐字移入 Core 的 `UsageRangePolicy.ResolveSelectedRange(mode, pickerValue, customStartLocal, cycle, now)`：窗口只提供模块状态与当前周期选择，纯函数返回 `SelectedRange`。规则（周/月/周期/自定义起算的边界钳制、跟随当前判定、标题文案）保持不变，新增 13 项无 WPF 行为测试覆盖当前/历史/未来钳制与周期反转。主窗口代码相应缩减约 70 行。验证：Core 回归 566/566，桌面回归三组通过（报告 `artifacts/desktop-probes/20260919-012412-*/`）。

第十三轮（2026-09-19）对整个 `src/` 做静态可变状态审计，确认去静态化收官：剩余静态成员均为有意保留——`MonitorCachePaths`/`UsageLogPaths`/`CacheOperationDiagnostics` 的 `AsyncLocal` 作用域（既定测试隔离机制）、`CacheOperationDiagnostics.nextOperationId`（Interlocked 单调计数）、`PriceSettings.States` 弱引用快照表与 `ResetOpportunities`/`SubscriptionPlans` 的按路径设置表（第四/六轮文档化设计，带恢复测试）、`CodexDataTransferService.ImportGate`（进程级导入串行契约）与 `CodexModelCost.Catalogs` 弱表备忘（随 PriceSettings 实例回收）。Claude/ZCode/WorkBuddy 三个读取器本就无状态。

第十轮（2026-09-19）把 `CodexQuotaCycleReader` 的单条目 2 分钟周期缓存改为实例持有：缓存字段与 `ReadWeeklyCycles`/`InvalidateCache` 随实例走，周期识别纯算法保持静态；实例由 `CodexUsageReader` 持有并经 `UsageSourceReaders.Codex.Cycles` 暴露，`ClearCache`/`ClearCachedDay` 失效本实例缓存。Core 回归 553/553，桌面回归三组通过（报告 `artifacts/desktop-probes/20260919-005753-*/`）。

第九轮（2026-09-18/19）验证：Core 回归 **553/553 通过，0 跳过**，Release 构建 **0 警告、0 错误**；桌面回归三组全部通过（主窗口 46 项检查、8 张渲染，设置 10 项、11 张渲染，分析 9 项、4 张渲染），报告在 `artifacts/desktop-probes/20260919-005227-*/desktop-regression.json`。`DshUsageReaderTests` 全量通过，确认路径作用域迁移等价；`ReaderCacheConsistencyTests` 在共享实例语义下继续约束缓存一致性。

## 后续优先级

| 优先级 | 现有证据 | 下一步及验收条件 |
| --- | --- | --- |
| ~~来源读取去静态化~~（已完成） | `CodexUsageReader` 实例化，共享实例经 `UsageSourceReaders.Codex` 暴露；DSH 全局测试覆盖属性已删；`CodexQuotaCycleReader` 周期缓存挂到 Codex reader 实例上 | 无遗留 |
| ~~测试稳定性：SQLite 连接池竞态~~（已完成） | 一次偶发 `ObjectDisposedException`（`MonitorSettingsDatabase.OpenConnection`，池化句柄），重跑即绿 | 生产四个 store 全部 `Pooling=false`（I/O 由共享闸门串行化，池化无收益）；连续 20 次全量回归验证 |
| 按功能迭代：额度和设置显示 | 主统计与每来源页面状态已独立；额度/设置摘要仍是窗口适配代码 | 随相关需求提取有状态契约的部分，继续保留图表和控件适配职责，不以 partial 文件数量或全面 MVVM 作为完成标准 |
| 按功能迭代：来源扩展 | 来源定义和 Tab 已统一，Core 查询与 WPF 页面工厂分离 | 新来源分别注册读取能力和桌面页面，保持价格 JSON 字段与来源枚举的兼容迁移约定 |

不以文件行数减少作为架构完成标准。每一轮应有一个清楚的责任边界、行为测试和运行验证；不要把分成多个 partial 文件当作已经解除耦合。

## 后续实现约定

1. 新用量查询优先扩展服务或来源适配器，不把原始日志读取重新写进控件事件。
2. 后台查询捕获输入快照，返回结果再由 UI 应用；不在工作线程读取当前 Tab 或修改 module 状态。
3. 新增可能扫描、回填、导入或物化缓存的入口，明确共用锁及取消源的所有者；主窗口后台工作通过 `MonitorRuntime.Run` 登记，不能在已登记任务内部等待 `StopAsync`。不能仅凭方法包含 Cached 就认定无写入。
4. 保持费用口径、来源标识、SQLite 格式、传输协议和显示恢复格式；需要变更时单独安排兼容迁移。
5. 测试使用 fake reader 或同时隔离 `MonitorCachePaths` 和 `UsageLogPaths`；缓存隔离本身不能阻止扫描真实日志。
6. 组合多个缓存操作时显式检查或合并各自的诊断；有警告的结果不能替换成功显示。底层保留空结果兼容契约不代表调用方可以忽略失败。

## 验证

修改前 Core 基线为 322 项通过。第一轮新增 23 项查询用例、9 项调度用例，**354/354 通过**。查询测试覆盖 CacheOnly 边界、日与历史修复、分桶与 Coding Time、额度回退/锚点、周期完整性及取消；调度测试覆盖请求合并、过时结果、同步完成/重入、异常恢复和生命周期取消。

第二轮增加 9 项缓存故障测试、10 项运行时测试、3 项历史共享生命周期测试、2 项显示保存测试与 1 项失败结果缓存保护测试，最终 **379/379 通过，0 跳过**。覆盖真实 SQLite 锁定/损坏及同实例恢复、作用域隔离、停止接收/总超时/延迟释放、历史 HTTP 等锁取消、失败记录上限、非消息泵 UI 上下文中的同步 Flush，以及等待写锁时出现较新保存的情况。

第三轮增加 9 项分析查询会话测试、7 项周期查询服务测试、7 项缓存投影/周期缓存测试，最终 **402/402 通过，0 跳过**。覆盖窗口与父运行时独立取消/排空、等锁取消、只读查询绕过预热、失败结果不进入校准、恢复后的新诊断、原周期范围保留、缓存投影与原物化结果一致、锚点不落库、周期失败产物不进缓存、数据库路径隔离与显式嵌套诊断传播。

第四、五轮增加 13 项价格存储恢复、34 项套餐/重置存储恢复、19 项来源注册、5 项路径作用域、1 项套餐失败不污染校准和 2 项取消传播测试；另将原窗口取消测试改为确定性竞态复现。最终 **476/476 通过，0 跳过**，Release 构建 **0 警告、0 错误**。测试包含真实文件独占锁、坏 JSON/SQLite、保存后空表、文件或表消失、原路径恢复、并发操作固定快照、非法重置响应、失败种子事务回滚及真实 Reader 并发目录隔离。最终 TRX 在 `work/architecture-round4/test-results/architecture-round5-final.trx`。

第六轮新增 9 项来源能力测试、8 项校准存储恢复测试、11 项纯显示 ViewModel 测试，**504/504 通过，0 跳过**。覆盖五来源不完整缓存旁存在真实不同原始数据时不回填、无维护能力的查询替身、并行校准目录、原异常/诊断/事务回滚、不可变显示快照、空成功与首次失败区别、切换/忙碌/关闭后状态，以及模块缓存失效时复制状态独立于原显示。最终 TRX 在 `work/architecture-round6/test-results/architecture-round6-final.trx`。

第七轮新增 19 项缓存专用查询测试、8 项页面状态组合测试，**531/531 通过，0 跳过**，Release 构建 **0 警告、0 错误**。仅实现 `IUsageCacheQuery` 的替身覆盖四种范围及自定义起点、摘要和明细不一致、无明细的每日分桶、编码时间、读取诊断恢复、空范围及最后一次额度读取取消；页面测试补足 LRU 更新/失败不提升顺序、实时显示不挤掉历史缓存、跨来源清空隔离及手写 v5 文件恢复后刷新。最终 TRX 在 `work/architecture-round7/test-results/architecture-round7-final.trx`。

`dotnet build .\CodexTokenMonitor.slnx -c Release`：**0 警告、0 错误**。最终回归命令：

```powershell
dotnet test .\tests\CodexTokenMonitor.Core.Tests\CodexTokenMonitor.Core.Tests.csproj -c Release --no-build --no-restore
```

第一轮隔离 WPF 探针 27 组检查全部通过；第二轮扩展为 **31 组检查全部通过**。探针使用真实主窗口刷新入口、真实 SQLite 缓存和合成事件，包括五来源的日/周/月/自定义区间与 Codex 周期共 21 组查询，以及历史周期等锁时 Dispatcher 响应、已物化周期缓存直接命中、来源/范围快速切换取最新。第二轮另外验证真实损坏缓存时保留成功显示或提示统计不可用、恢复数据库后同一 store 重试成功，以及真实 `Close()` 暂缓关闭、取消查询并等待受控任务结束后才触发 `Closed`。已查看实际 1380 × 940 离屏渲染，明细表和时间轴正常显示样例数据。

第三轮重新运行主窗口 **31 组回归全部通过**，最新证据在 `work/architecture-round3/main-validation/`。另有 `work/architecture-round3/UiProbe.csproj` 使用真实 WPF Loaded/Closed 生命周期验证分析窗口，**12 组检查全部通过**（包含 4 张实际渲染）：共享锁被占用时加载出 3 个周期、27 个曲线点，派生锚点表行数保持 0；周期分析损坏恢复检查保留同一图表/表格对象，恢复后仍为 8 段、1,350,000 tokens。费用曲线、估算表和手动估算也分别验证了真实 SQLite 损坏时保留成功结果、恢复后重读并清除警告。空预传周期列表时，右键派生窗口正确获得上一周期及同一父运行时。图像与 JSON 位于 `work/architecture-round3/validation/`（忽略目录）；已查看曲线、估算、故障保留和恢复后的四张渲染。

两类探针均保持缓存与来源日志双路径隔离，app-server 调用为 0，共享服务未启动；分析窗口探针不构造主窗口，主窗口探针不触发 Loaded。历史共享生命周期由独立存储测试验证，本轮未验证真实账户联网或外部 HTTP 传输；正在运行的 EXE 未替换。

第四、五轮最终 WPF 回归使用同一份最终 Core 产物：

- 主窗口报告 **38 条检查（含 1 条渲染）**：在原 31 条基础上增加套餐/重置摘要独立故障与恢复、首次失败、价格故障保留与恢复，以及来源标签 Tag 映射。报告和 1380 × 940 渲染在 `work/architecture-round4/main-validation-phase5-final/`。
- 三个设置窗口 **10 项行为/隔离检查、11 张渲染**：真实 Loaded、坏文件禁存和重试、已有编辑再载入失败保留、保存失败留窗、修复后真实 `ShowDialog` 成功及磁盘重读、全部关闭与运行时排空。首次坏库顶部明确显示暂不可用。证据在 `work/architecture-round4/setting-validation/output-phase5-final/`。
- 分析窗口 **8 项行为/隔离检查、4 张渲染** 再次通过，覆盖共用锁、只读曲线、派生锚点不落库、损坏保留与恢复、窗口取消及父运行时排空。证据在 `work/architecture-round4/setting-validation/analysis-output-phase5-final/`。

第六轮最终 WPF 回归再次通过：主窗口 **43 项行为/隔离检查、5 张渲染**，三个设置窗口 **10 项行为/隔离检查、11 张渲染**，分析窗口 **8 项行为/隔离检查、4 张渲染**。主窗口新增检查 22 处绑定始终有效、忙碌时复制状态、成功空结果与读取失败的区别、空结果恢复、真实来源切换，以及共享导入暂不刷新时保留原快照和明细、停用复制并在成功读取后恢复。三组分别位于 `work/architecture-round6/output-final-copy/`、`settings-output/`、`analysis-output/`，每组包含 `ui-probe-results.json`；使用最终构建产物，真实账户与服务调用保持隔离。

第七轮最终 WPF 回归为主窗口 **45 项行为/隔离检查、7 张渲染**，设置窗口 **10 项行为/隔离检查、11 张渲染**，分析窗口 **8 项行为/隔离检查、4 张渲染**，全部通过。新增真实五来源往返的 10 次 Tab 事件，验证日期、周范围、自定义起点和非默认历史周期选择均保留；另构造第二个真实主窗口，验证五组页面、ViewModel、运行时与 gate 独立，第二窗失败/恢复/关闭不改变第一窗的选择与成功显示。原周期 LRU 绕 gate、共享导入与异步关停检查保留。报告和图像分别位于 `work/architecture-round7/main-output-final/`、`settings-output/`、`analysis-output/`。

第八轮（2026-09-18）验证：Core 回归 **553/553 通过，0 跳过**（新增 6 项 `UiEventSuppressorTests`），Release 构建 **0 警告、0 错误**。桌面回归三组全部通过：主窗口 **46 项检查、8 张渲染**（含 2026-09-18 额度估算改动新增项），设置窗口 **10 项、11 张渲染**，分析窗口 **9 项、4 张渲染**；报告在 `artifacts/desktop-probes/20260919-000223-*/desktop-regression.json`。主窗口五来源往返、真实源切换与独立窗口恢复检查均依赖新的抑制域语义，确认程序化赋值不触发刷新、真实用户选择正常排队。

以上检查使用隔离数据且未访问真实账户；已检查实际窗口渲染，取消竞态修复后再次验证首次失败和恢复画面。第七轮已完成缓存专用服务入口与每来源页面状态迁移；隔离桌面探针现已正式化（探针项目 + 串行入口 + CI 工作流），后续迭代重复使用同一验证入口。cached 接口仍可能触发 SQLite 初始化，主查询额度时间线仍有物化，不据接口名称移除现有共用锁。

现有额度回放修复保持，未改版本、发布或替换运行程序。

相关历史：[UI 与迁移结构检查](UI-STRUCTURE-REVIEW.md)、[额度消耗预测](QUOTA-FORECAST.md)。本轮结论优先于旧检查中的待办顺序。
