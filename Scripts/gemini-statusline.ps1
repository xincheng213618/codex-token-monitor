# Google Antigravity CLI (agy) status line for CodexTokenMonitor.
#
# Antigravity CLI pipes JSON context (model + quota buckets with remaining_fraction
# and reset_time) to the statusLine command. This script records changes to
# %LOCALAPPDATA%\GeminiTokenMonitor\rate-limits.jsonl and prints a concise status line.
#
# Configure in ~/.gemini/antigravity-cli/settings.json:
#   "statusLine": {
#     "enabled": true,
#     "type": "command",
#     "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"<path>\\Scripts\\gemini-statusline.ps1\""
#   }

$ErrorActionPreference = 'Stop'
try {
    $raw = [Console]::In.ReadToEnd()
    $data = $raw | ConvertFrom-Json
} catch {
    exit 0
}

$parts = @()
if ($data.model.display_name) {
    $parts += $data.model.display_name
} elseif ($data.model.id) {
    $parts += $data.model.id
}

$quota = $data.quota
if ($quota) {
    try {
        $dir = Join-Path $env:LOCALAPPDATA 'GeminiTokenMonitor'
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        $key = $quota | ConvertTo-Json -Compress -Depth 5
        $keyPath = Join-Path $dir 'rate-limits.last'
        $previous = if (Test-Path -LiteralPath $keyPath) { [IO.File]::ReadAllText($keyPath) } else { '' }
        if ($previous -ne $key) {
            $record = [ordered]@{
                recorded_at = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                model       = $data.model.id
                quota       = $quota
            }
            $line = ($record | ConvertTo-Json -Compress -Depth 6) + "`n"
            [IO.File]::AppendAllText((Join-Path $dir 'rate-limits.jsonl'), $line, (New-Object Text.UTF8Encoding $false))
            [IO.File]::WriteAllText($keyPath, $key, (New-Object Text.UTF8Encoding $false))
        }
    } catch {
        # Recording is best effort; the status line itself must still print.
    }

    foreach ($prop in $quota.PSObject.Properties) {
        $name = $prop.Name.ToLowerInvariant()
        if ($name -match 'claude|gpt|openai') { continue }
        $bucket = $prop.Value
        if ($null -ne $bucket.remaining_fraction) {
            $remPct = [math]::Round([double]$bucket.remaining_fraction * 100.0)
            if ($name -match '5h|five') { $parts += ("5h {0:0}%" -f $remPct) }
            elseif ($name -match '7d|week|seven') { $parts += ("7d {0:0}%" -f $remPct) }
        }
    }
}

[Console]::Out.Write(($parts -join ' | '))
