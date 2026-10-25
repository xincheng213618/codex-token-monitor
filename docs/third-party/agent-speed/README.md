# Agent Speed attribution

Source: https://github.com/zoefix/agent-speed

Reviewed revision: `a94a67fcc78e5d8eea5f82d8d5e737bccf994e2c`.

AgentSpeedEngine and the native source parsers adapt request-state rules,
minimum/maximum measurement intervals, duration-weighted averages, three-second
live windows, interval-overlap distribution, response upserts, ownership deduplication,
recent-response estimation and mode evidence from Agent Speed's `spec/METRICS.md`
and Windows Core implementation, licensed under MIT. The original copyright and
license are in LICENSE.txt and accompany published copies in Notices/agent-speed.
Pinned upstream parser fixtures used in tests retain this attribution.

Our implementation integrates with the existing source registry, incremental file
reader and subagent replay filter. It supports the seven existing sources: Codex,
Claude Code, DSH, WorkBuddy, ZCode, Kimi and Qoder (the latter uses this project's
native request records). It adds output/total-throughput selection, measured peaks,
session trees and aggregate-only local history with CSV export. Dates use UTC+8.
Unknown timing remains unmeasured; estimates require a matching source, model and
mode. A five-minute curve is reconstructed from request intervals, not individual
streamed-token arrival times. This feature sends no network requests and installs
no background service. Prices, quotas and data sharing retain this project's own
implementations. See ../../AGENT-SPEED-ADOPTION.md for scope and validation.
