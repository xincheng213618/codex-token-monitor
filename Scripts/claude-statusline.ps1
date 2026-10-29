# Claude Code status line for CodexTokenMonitor.
#
# Claude Code pipes session JSON to the status line command. For claude.ai
# Pro/Max sessions it carries the official rate_limits (5h / 7d used
# percentage and resets_at epoch seconds). This script records each change of
# those values to %LOCALAPPDATA%\ClaudeCodeTokenMonitor\rate-limits.jsonl,
# which the monitor reads, and prints a short status line.
#
# Configure in ~/.claude/settings.json:
#   "statusLine": { "type": "command",
#     "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"<path>\\claude-statusline.ps1\"" }

$ErrorActionPreference = 'Stop'
try {
    $raw = [Console]::In.ReadToEnd()
    $data = $raw | ConvertFrom-Json
} catch {
    exit 0
}

$limits = $data.rate_limits
$parts = @()
if ($data.model.display_name) { $parts += $data.model.display_name }

if ($limits -and ($limits.five_hour -or $limits.seven_day)) {
    try {
        $dir = Join-Path $env:LOCALAPPDATA 'ClaudeCodeTokenMonitor'
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        $record = [ordered]@{
            recorded_at = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            session_id  = $data.session_id
            model       = $data.model.id
            five_hour   = $limits.five_hour
            seven_day   = $limits.seven_day
        }
        # Only a change in percentage or reset time is worth a line; the
        # status line refreshes far more often than the limits move.
        $key = [ordered]@{ five_hour = $limits.five_hour; seven_day = $limits.seven_day } | ConvertTo-Json -Compress -Depth 4
        $keyPath = Join-Path $dir 'rate-limits.last'
        $previous = if (Test-Path -LiteralPath $keyPath) { [IO.File]::ReadAllText($keyPath) } else { '' }
        if ($previous -ne $key) {
            $line = ($record | ConvertTo-Json -Compress -Depth 4) + "`n"
            [IO.File]::AppendAllText((Join-Path $dir 'rate-limits.jsonl'), $line, (New-Object Text.UTF8Encoding $false))
            [IO.File]::WriteAllText($keyPath, $key, (New-Object Text.UTF8Encoding $false))
        }
    } catch {
        # Recording is best effort; the status line itself must still print.
    }

    if ($null -ne $limits.five_hour.used_percentage) { $parts += ('5h {0:0}%' -f [double]$limits.five_hour.used_percentage) }
    if ($null -ne $limits.seven_day.used_percentage) { $parts += ('7d {0:0}%' -f [double]$limits.seven_day.used_percentage) }
}

# ASCII separator: Windows PowerShell's console encoding mangles other symbols.
[Console]::Out.Write(($parts -join ' | '))
