# 本地构建与重启约定

- 使用 PowerShell 原生语法。递归删除或移动前，解析并核对绝对路径，使用带 `-LiteralPath` 的 PowerShell cmdlet。
- `outputs` 只保留一个正式程序目录 `outputs\CodexTokenMonitor`，以及已纳入版本控制的 `outputs\一键生成CodexTokenMonitor.cmd`。正式目录根部只保留 `CodexTokenMonitor.exe`；随程序发布的 `Notices` 许可目录保留。
- 不按功能名称另建正式发布目录，不在 `outputs` 留下旧版 exe、备份、测试截图或验证目录。临时发布和验证产物放到系统临时目录或项目的 `artifacts`。
- 更新正在运行的程序时，先在临时目录生成并验证最新版，再核对进程的完整 exe 路径；只关闭本次更新或明确清理范围内的实例。替换后核对文件哈希。
- 用户要求重启时，从 `outputs\CodexTokenMonitor\CodexTokenMonitor.exe` 启动一份最新版，确认进程响应和窗口标题；完成清理后核对 `outputs` 仅有上述正式目录与生成脚本。
- 保留与当前任务无关的未提交修改。
