param([Parameter(Mandatory = $true)][string]$RepositoryPath)

$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $RepositoryPath).Path
$targetRoot = Split-Path -Parent $PSScriptRoot
$sitePath = Join-Path $sourceRoot 'web/public/data/site.json'
$site = Get-Content -LiteralPath $sitePath -Raw | ConvertFrom-Json
$commit = (git -C $sourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit.' }
$points = @($site.points | Where-Object {
    $_.billing -eq 'subscription' -and $_.channel -in @('OpenAI', 'Zhipu') -and $_.monthly_tokens -gt 0
} | ForEach-Object {
    if ($_.original_price -le 0 -or $_.real_usd_per_mtok -le 0) { throw "Invalid price: $($_.id)" }
    [ordered]@{
        Id = $_.id
        Source = $(if ($_.channel -eq 'OpenAI') { 'Codex' } else { 'ZCode' })
        PlanId = $_.plan_id
        Plan = $_.plan
        Model = $_.model
        Currency = $_.currency
        MonthlyFee = $_.original_price
        MonthlyTokens = $_.monthly_tokens
        UsdPerMillion = $_.real_usd_per_mtok
        Workload = $_.workload
        Confidence = $_.confidence
        Evidence = $_.source
        Note = $_.decision_note
    }
})
if ($points.Count -eq 0) { throw 'No supported reference prices found.' }
$output = [ordered]@{
    SnapshotDate = ([string]$site.generatedAt).Substring(0, 10)
    Commit = $commit
    SourceUrl = 'https://github.com/FeiZhuLulu/real-api-pricing'
    Points = $points
}
$dataDirectory = Join-Path $targetRoot 'src/CodexTokenMonitor.Core/Data'
$noticeDirectory = Join-Path $targetRoot 'docs/third-party/real-api-pricing'
New-Item -ItemType Directory -Path $dataDirectory, $noticeDirectory -Force | Out-Null
$output | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dataDirectory 'real-api-pricing.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Destination (Join-Path $noticeDirectory 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'SOURCES.md') -Destination (Join-Path $noticeDirectory 'SOURCES.md')
Write-Output "Imported $($points.Count) reference prices from $($output.SnapshotDate), commit $commit."
