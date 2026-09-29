# CodexTokenMonitor 项目备忘

## 架构关键点
- 六个数据源（Codex/Claude Code/ZCode/WorkBuddy/DSH/Kimi）各自有 UsageReader + 独立 SQLite 缓存
  （`%LOCALAPPDATA%\<Source>TokenMonitor\`，版本：Codex=v4、ZCode=v7、其余=v3）。
- 共享组件：`UsageCacheStore`（usage_days + usage_events 两级）、`HistoricalBatchWarmer`、
  `UsageEventMerger`。Codex 的 warmer 是独立实现（事件级聚合），其余五源共用 `HistoricalUsageBatchWarmer`。
- 模型归因链路：reader 解析 ModelId → TokenUsageEvent → bucket.Add(event) 聚合进
  ModelUsage → 持久化为 usage_days.model_usage_json → UI"实际模型"列 / "实际模型"费用卡
  （`summary.ModelUsage.Count > 0` 才显示）。
- 缓存 schema 变更/解析变更用 `cache_maintenance` 表一次性迁移旗标（如 workbuddy-model-context-v1、
  warmer-model-context-v1），重置 is_complete 触发重扫；绝不动 usage_events 明细。

## 排障套路
- "实际模型=未识别"类问题：先对比 `usage_days.model_usage_json` 与 `usage_events.model_id`，
  再看 `cache_maintenance` 旗标与对应 reader 的解析字段（WorkBuddy=providerData.model，
  Claude=message.model，ZCode=model.modelId，DSH=request/context 行，Kimi=root.model）。
- 标量 `bucket.Add(timestamp,...)` 重载不写 ModelUsage——从事件构建要持久化的桶时必须用事件重载。

## 协作约定
- **多 AI 会话并行开发本仓库**（每模块一个会话）。开工先 `git status`，只改自己范围文件；
  遇 EBUSY 是并发会话/IDE 占用，重试即可。测试失败先判断是否他人未提交工作所致。
- 用户偏好：直接执行不提问、定期编译、Git 回滚兜底、数据驱动验证（定量+表格）。

## 已修复
- 2026-09-29：共享 warmer 日桶丢 ModelUsage（WorkBuddy 全部历史日"未识别"）→
  事件重载修复 + warmer-model-context-v1 迁移 + WorkBuddy reasoning 解析（详见 2026-09-29.md）。
