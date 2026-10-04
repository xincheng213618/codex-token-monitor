[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'

# This script is intentionally restricted to this one artifacts directory.
$expectedRepo = 'C:\Users\17917\Desktop\CodexTokenMonitor'
$expectedTarget = 'C:\Users\17917\Desktop\CodexTokenMonitor\artifacts'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).ProviderPath.TrimEnd('\')
if (-not $repo.Equals($expectedRepo, [StringComparison]::OrdinalIgnoreCase)) {
    throw "This script must remain in $expectedRepo\Scripts."
}
if ((Get-Item -LiteralPath $repo -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The project directory is a link. Cleanup stopped.'
}
if (-not (Test-Path -LiteralPath $expectedTarget)) {
    Write-Host 'The artifacts directory does not exist. Nothing to clean.'
    return
}

$target = (Resolve-Path -LiteralPath $expectedTarget).ProviderPath.TrimEnd('\')
$targetInfo = Get-Item -LiteralPath $target -Force
if (-not $target.Equals($expectedTarget, [StringComparison]::OrdinalIgnoreCase) -or
    -not $targetInfo.PSIsContainer -or
    ($targetInfo.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The target is not the expected ordinary artifacts directory. Cleanup stopped.'
}

# Refuse tracked files and links before deleting anything.
$tracked = @(& git -C $repo ls-files -- artifacts)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not check tracked files. Cleanup stopped.'
}
if ($tracked.Count -gt 0) {
    throw 'The artifacts directory contains tracked files. Cleanup stopped.'
}
$inventory = @(Get-ChildItem -LiteralPath $target -Force -Recurse)
$links = @($inventory | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
if ($links.Count -gt 0) {
    throw "Links are excluded from cleanup: $($links.FullName -join ', ')"
}
$files = @($inventory | Where-Object { -not $_.PSIsContainer })
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
$sizeGiB = [math]::Round($bytes / 1GB, 2)
Write-Host "Target: $target"
Write-Host "Contents: $($files.Count) files, $sizeGiB GiB"
if ($inventory.Count -eq 0) {
    Write-Host 'The artifacts directory is already empty.'
    return
}

# Do not stop processes automatically.
$active = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ProcessId -ne $PID -and (
        ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) -or
        ($_.CommandLine -and $_.CommandLine.IndexOf($target, [StringComparison]::OrdinalIgnoreCase) -ge 0)
    )
})
if ($active.Count -gt 0) {
    $active | Select-Object ProcessId, Name, ExecutablePath | Format-Table -AutoSize | Out-Host
    throw 'A process references artifacts. Close that process or wait for its task to finish, then retry.'
}

$children = @(Get-ChildItem -LiteralPath $target -Force)
if (-not $PSCmdlet.ShouldProcess($target, "Remove all contents ($($files.Count) files, $sizeGiB GiB), keeping the directory")) {
    return
}
foreach ($child in $children) {
    $childPath = (Resolve-Path -LiteralPath $child.FullName).ProviderPath
    if (-not $childPath.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the authorized target: $childPath"
    }
    $current = Get-Item -LiteralPath $childPath -Force
    if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "A link appeared during cleanup: $childPath"
    }
    Remove-Item -LiteralPath $childPath -Recurse -Force -Confirm:$false
}

$remaining = @(Get-ChildItem -LiteralPath $target -Force)
if ($remaining.Count -gt 0) {
    throw "Cleanup is incomplete. $($remaining.Count) entries remain in $target."
}
Write-Host "Cleanup complete: removed $($files.Count) files ($sizeGiB GiB); the artifacts directory is empty."
