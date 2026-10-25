# Agent Speed golden samples

Copied from zoefix/agent-speed revision a94a67fcc78e5d8eea5f82d8d5e737bccf994e2c,
`spec/fixtures`. MIT license and copyright: docs/third-party/agent-speed/LICENSE.txt.
`golden.json` contains the corresponding response expectations from the upstream
independent reference implementation. The suite checks input/output totals, request
start/end, finalization, model and response ownership in startup order.

Claude Desktop samples share our existing Claude Code source. WorkBuddy's two
editions share our existing WorkBuddy source. The two additional upstream timestamp
stress files using year 0000 or dates beyond .NET DateTimeOffset's range are excluded;
this suite is not a claim of full upstream protocol or numeric-edge compatibility.
Our own tests additionally cover the native ZCode ledger, Qoder request segments,
Kimi protocol 1.4 and native message mirrors, persistence, modes and lifecycle.
