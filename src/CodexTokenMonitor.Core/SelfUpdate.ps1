param([Parameter(Mandatory = $true)][string]$PlanPath)
$ErrorActionPreference = 'Stop'
$workDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$parentExited = $false
$changed = [Collections.Generic.List[object]]::new()
$pendingFile = $null

function Assert-OrdinaryPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "更新路径不能包含链接：$cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Assert-Hash([string]$Path, [string]$Expected) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
    if ($actual -ne $Expected) {
        throw "文件校验失败：$Path"
    }
}

try {
    $plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $target = [IO.Path]::GetFullPath($plan.TargetPath)
    $targetDirectory = [IO.Path]::GetDirectoryName($target)
    $payload = Join-Path $workDirectory 'payload'
    Assert-OrdinaryPath $workDirectory
    Assert-OrdinaryPath $target
    if ([IO.Path]::GetFileName($target) -ine 'CodexTokenMonitor.exe') { throw '目标程序路径无效。' }
    $original = Get-Process -Id $plan.ProcessId -ErrorAction Stop
    if ($original.Path -ine $target -or $original.StartTime.ToUniversalTime().Ticks.ToString() -ne $plan.ProcessStartTicks) {
        throw '运行中的程序与更新目标不匹配。'
    }
    Assert-Hash $target $plan.OriginalSha256
    $files = @($plan.Files)
    if (@($files | Where-Object { $_.RelativePath -ceq 'CodexTokenMonitor.exe' }).Count -ne 1) { throw '更新包缺少程序。' }
    foreach ($file in $files) {
        $relative = $file.RelativePath
        $destination = [IO.Path]::GetFullPath((Join-Path $targetDirectory $relative))
        $source = [IO.Path]::GetFullPath((Join-Path $payload $relative))
        if (($relative -cne 'CodexTokenMonitor.exe' -and $relative -cnotlike 'Notices\*') -or
            -not $destination.StartsWith($targetDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not $source.StartsWith($payload + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '更新文件路径无效。' }
        Assert-OrdinaryPath $destination
        Assert-OrdinaryPath $source
        Assert-Hash $source $file.Sha256
    }
    # Fail before asking the application to exit if the installation directory is read-only.
    $probe = Join-Path $targetDirectory ('.update-' + [guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($probe, '')
    Remove-Item -LiteralPath $probe -Force
    [IO.File]::WriteAllText((Join-Path $workDirectory 'ready'), '')
    if (-not $original.WaitForExit(120000)) { throw '等待程序退出超时，原程序未替换。' }
    $parentExited = $true
    Assert-Hash $target $plan.OriginalSha256
    foreach ($file in $files) {
        $destination = Join-Path $targetDirectory $file.RelativePath
        $source = Join-Path $payload $file.RelativePath
        $backup = Join-Path (Join-Path $workDirectory 'backup') $file.RelativePath
        $existed = Test-Path -LiteralPath $destination -PathType Leaf
        if ($existed) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
            Copy-Item -LiteralPath $destination -Destination $backup
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        $pendingFile = $destination + '.update-' + [guid]::NewGuid().ToString('N')
        Copy-Item -LiteralPath $source -Destination $pendingFile
        Assert-Hash $pendingFile $file.Sha256
        # The temporary file is on the same volume, so replacing a locked EXE fails without truncating it.
        if ($existed) { [IO.File]::Replace($pendingFile, $destination, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($pendingFile, $destination) }
        $pendingFile = $null
        $changed.Add([pscustomobject]@{ Destination = $destination; Backup = $backup; Existed = $existed })
        Assert-Hash $destination $file.Sha256
    }
    Start-Process -FilePath $target -WorkingDirectory $targetDirectory -WindowStyle Normal -ErrorAction Stop | Out-Null
}
catch {
    $failure = $_.Exception.Message
    for ($index = $changed.Count - 1; $index -ge 0; $index--) {
        $file = $changed[$index]
        try {
            if ($file.Existed) { Copy-Item -LiteralPath $file.Backup -Destination $file.Destination -Force }
            else { Remove-Item -LiteralPath $file.Destination -Force }
        }
        catch { $failure += "`r`n恢复原文件失败：$($_.Exception.Message)" }
    }
    if ($pendingFile -and (Test-Path -LiteralPath $pendingFile)) {
        Remove-Item -LiteralPath $pendingFile -Force -ErrorAction SilentlyContinue
    }
    [IO.File]::WriteAllText((Join-Path $workDirectory 'error.txt'), $failure, [Text.Encoding]::UTF8)
    if ($parentExited) {
        try { Start-Process -FilePath $target -WorkingDirectory $targetDirectory -WindowStyle Normal -ErrorAction Stop | Out-Null } catch { }
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show("更新失败，已尝试恢复原程序。`r`n$failure`r`n`r`n日志：$workDirectory", 'Codex Token 监控器更新') | Out-Null
    }
    exit 1
}

# This directory was created exclusively for this update. Verify its exact location before recursive cleanup.
$resolvedWork = (Resolve-Path -LiteralPath $workDirectory).ProviderPath
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
if ([IO.Path]::GetDirectoryName($resolvedWork) -ieq $temporaryRoot -and
    [IO.Path]::GetFileName($resolvedWork) -match '^CodexTokenMonitor-update-[a-f0-9]{32}$') {
    Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
}
