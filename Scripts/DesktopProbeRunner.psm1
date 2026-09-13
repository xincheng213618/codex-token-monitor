#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Assert-ProbeCondition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-DesktopChildProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$LogPrefix,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $started = $false
    try {
        if (-not $process.Start()) { throw "Could not start $FilePath" }
        $started = $true
        $childId = $process.Id
        # Drain both pipes concurrently; a verbose build must not block on stderr.
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) {
            # This handle belongs to the child we just created, never a user app.
            if (-not $process.HasExited) {
                try { $process.Kill($true) }
                catch [InvalidOperationException] { if (-not $process.HasExited) { throw } }
            }
            if (-not $process.WaitForExit(10000)) { throw "Child $childId did not exit after timeout termination." }
        }
        if (-not $stdout.Wait(5000) -or -not $stderr.Wait(5000)) { throw "Child $childId did not close its output pipes." }
        [IO.File]::WriteAllText("$LogPrefix.stdout.log", $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText("$LogPrefix.stderr.log", $stderr.GetAwaiter().GetResult())
        return [pscustomobject]@{
            ProcessId = $childId
            ExitCode = $process.ExitCode
            TimedOut = $timedOut
            ElapsedMilliseconds = $watch.ElapsedMilliseconds
            Stdout = "$LogPrefix.stdout.log"
            Stderr = "$LogPrefix.stderr.log"
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            try { $process.Kill($true); [void]$process.WaitForExit(10000) }
            catch [InvalidOperationException] { if (-not $process.HasExited) { throw } }
        }
        $process.Dispose()
    }
}

function Get-ProbeCheck($Report, [string]$Name) {
    $matches = @($Report.checks | Where-Object check -EQ $Name)
    Assert-ProbeCondition ($matches.Count -eq 1) "Expected exactly one '$Name' check."
    return $matches[0]
}

function Assert-ProbeFlags($Check, [hashtable]$Flags) {
    foreach ($name in $Flags.Keys) {
        $value = $Check.$name
        Assert-ProbeCondition ($value -is [bool] -and $value -eq $Flags[$name]) "Check '$($Check.check)' has invalid '$name'."
    }
}

function Test-DesktopProbeReport {
    param(
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$Suite,
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$CoreHash,
        [Parameter(Mandatory)][string]$WpfHash
    )
    $reportPath = Join-Path $OutputDirectory 'ui-probe-results.json'
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Assert-ProbeCondition ($report.schemaVersion -eq 1 -and $report.suite -ceq $Suite) 'Wrong report schema or suite.'
    Assert-ProbeCondition ($report.processId -eq $ProcessId) 'Report came from a different process.'
    Assert-ProbeCondition ($report.passed -is [bool] -and $report.passed -and [string]::IsNullOrEmpty($report.error)) 'Probe report did not pass.'
    Assert-ProbeCondition ($report.coreSha256 -ceq $CoreHash -and $report.wpfSha256 -ceq $WpfHash) 'Probe did not load the expected production binaries.'
    Assert-ProbeCondition ($report.checks -is [array] -and $report.checks.Count -gt 0) 'Probe report contains no checks.'

    # These are required behaviors, not an expected total. Adding new checks is
    # fine; silently dropping cancellation/isolation/recovery coverage is not.
    $required = switch ($Suite) {
        'main' { @('display-bindings-initialized', 'registered-tabs', 'historical-cycle-gate',
            'materialized-cycle-display', 'source-selection-round-trip', 'latest-wins-source-and-range',
            'independent-window-recovery', 'shared-import-copy-binding', 'empty-to-ready-binding',
            'main-price-failure', 'main-price-recovery', 'real-closing-drain', 'isolation') }
        'settings' { @('normal-load', 'failed-load-and-retry', 'failed-save-and-retry', 'isolation-and-shutdown') }
        'analysis' { @('cached-windows-bypass-gate', 'empty-known-period-fallback', 'cached-windows-corruption-recovery',
            'manual-estimate-corruption-recovery', 'single-window-close', 'cycle-corruption-recovery', 'parent-shutdown', 'isolation-and-exit') }
        default { throw "Unknown suite $Suite" }
    }
    foreach ($name in $required) {
        Assert-ProbeCondition ($name -cin @($report.checks.check)) "Required behavior '$name' is missing."
    }
    switch ($Suite) {
        'main' {
            $isolation = Get-ProbeCheck $report 'isolation'
            Assert-ProbeFlags $isolation @{ mainLoaded = $false; sharingServerStarted = $false; allClosed = $true }
            Assert-ProbeCondition ($isolation.appServerReads -eq 0 -and $isolation.windowsCreated -eq $isolation.closedWindows) 'Main-window isolation or shutdown was incomplete.'
            Assert-ProbeFlags (Get-ProbeCheck $report 'real-closing-drain') @{
                firstCloseDeferred = $true; dispatcherResponsive = $true; resultPublished = $false
                gateReleasedBeforeDisposal = $true; finalClosed = $true
            }
            Assert-ProbeFlags (Get-ProbeCheck $report 'materialized-cycle-display') @{ bypassesGate = $true }
            Assert-ProbeFlags (Get-ProbeCheck $report 'latest-wins-source-and-range') @{ supersededPublished = $false }
            $queries = @($report.checks | Where-Object check -EQ 'cache-only-refresh')
            foreach ($source in @('Codex', 'ClaudeCode', 'ZCode', 'WorkBuddy', 'Dsh')) {
                foreach ($mode in @('Day', 'Week', 'Month')) {
                    Assert-ProbeCondition (@($queries | Where-Object { $_.source -ceq $source -and $_.mode -ceq $mode -and $_.custom -eq $false }).Count -eq 1) "Missing $source/$mode query."
                }
                Assert-ProbeCondition (@($queries | Where-Object { $_.source -ceq $source -and $_.custom -eq $true }).Count -eq 1) "Missing $source/custom query."
            }
            Assert-ProbeCondition (@($queries | Where-Object { $_.source -ceq 'Codex' -and $_.mode -ceq 'Cycle' }).Count -eq 1) 'Missing Codex cycle query.'
        }
        'settings' {
            $isolation = Get-ProbeCheck $report 'isolation-and-shutdown'
            Assert-ProbeFlags $isolation @{ allClosed = $true; runtimeDrained = $true; mainWindowCreated = $false; accountRequestsSkipped = $true; sharedNetworkStarted = $false }
            foreach ($window in @('PriceSettingsWindow', 'SubscriptionPlanWindow', 'ResetOpportunityWindow')) {
                foreach ($name in @('normal-load', 'failed-load-and-retry', 'failed-save-and-retry')) {
                    $check = @($report.checks | Where-Object { $_.check -ceq $name -and $_.window -ceq $window })
                    Assert-ProbeCondition ($check.Count -eq 1) "Missing $window/$name check."
                    if ($name -eq 'failed-load-and-retry') {
                        Assert-ProbeFlags $check[0] @{ saveAndOverwriteDisabled = $true; corruptBytesPreserved = $true; originalRowsRecovered = $true }
                    }
                    if ($name -eq 'failed-save-and-retry') {
                        Assert-ProbeFlags $check[0] @{ editPreserved = $true; dialogRemainedOpen = $true; corruptBytesPreserved = $true; retryDialogResult = $true; persisted = $true }
                    }
                }
            }
        }
        'analysis' {
            $isolation = Get-ProbeCheck $report 'isolation-and-exit'
            Assert-ProbeFlags $isolation @{ mainWindowConstructed = $false; sharingServerStarted = $false; allClosed = $true }
            Assert-ProbeCondition ($isolation.appServerReads -eq 0) 'Analysis probe accessed an account.'
            Assert-ProbeFlags (Get-ProbeCheck $report 'parent-shutdown') @{ childDrained = $true; latePublication = $false; newQueryRejected = $true }
            Assert-ProbeFlags (Get-ProbeCheck $report 'single-window-close') @{ dispatcherResponsive = $true; queryDrained = $true; parentStillUsable = $true }
            $cached = Get-ProbeCheck $report 'cached-windows-bypass-gate'
            Assert-ProbeCondition ($cached.timelineBefore -eq 0 -and $cached.timelineAfter -eq 0) 'Read-only curves persisted derived anchors.'
        }
    }

    $prefix = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $isolationPath = [IO.Path]::GetFullPath($isolation.isolatedRoot)
    Assert-ProbeCondition ($isolationPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) 'Fixture root escaped the suite directory.'
    $renders = @($report.checks | Where-Object check -EQ 'render')
    $requiredImages = switch ($Suite) {
        'main' { @('wpf-architecture-probe.png', 'main-source-round-trip.png', 'main-independent-window.png', 'main-stale-result.png', 'main-first-failure.png', 'main-successful-empty.png', 'main-empty-recovered.png') }
        'settings' {
            foreach ($window in @('PriceSettingsWindow', 'SubscriptionPlanWindow', 'ResetOpportunityWindow')) {
                foreach ($state in @('healthy', 'load-failure', 'save-failure')) { "$window-$state.png" }
            }
            'SubscriptionPlanWindow-reload-failure.png'; 'ResetOpportunityWindow-reload-failure.png'
        }
        'analysis' { @('quota-cost-curve.png', 'quota-estimate.png', 'cycle-cache-failure-preserved.png', 'cycle-analysis-recovered.png') }
    }
    $imageNames = @()
    Add-Type -AssemblyName PresentationCore
    foreach ($render in $renders) {
        Assert-ProbeCondition ([IO.Path]::IsPathFullyQualified($render.path)) 'Render path must be absolute.'
        $path = [IO.Path]::GetFullPath($render.path)
        Assert-ProbeCondition ($path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) 'Render path escaped the suite directory.'
        $bytes = [IO.File]::ReadAllBytes($path)
        Assert-ProbeCondition ($bytes.Length -ge 33 -and [BitConverter]::ToString($bytes, 0, 8) -ceq '89-50-4E-47-0D-0A-1A-0A' -and [Text.Encoding]::ASCII.GetString($bytes, 12, 4) -ceq 'IHDR') 'Render is not a PNG image.'
        $width = [uint32]$bytes[16] * 16777216 + [uint32]$bytes[17] * 65536 + [uint32]$bytes[18] * 256 + [uint32]$bytes[19]
        $height = [uint32]$bytes[20] * 16777216 + [uint32]$bytes[21] * 65536 + [uint32]$bytes[22] * 256 + [uint32]$bytes[23]
        Assert-ProbeCondition ($width -gt 0 -and $height -gt 0 -and $width -eq $render.width -and $height -eq $render.height) 'Render dimensions do not match the PNG.'
        Assert-ProbeCondition ($width -le 16384 -and $height -le 16384 -and [long]$width * $height -le 40000000) 'Render dimensions exceed the desktop screenshot limit.'
        # A valid header alone can describe a truncated, unusable PNG. Force
        # actual pixel decoding, without creating a WPF Application or window.
        $stream = [IO.MemoryStream]::new($bytes, $false)
        try {
            $decoder = [Windows.Media.Imaging.BitmapDecoder]::Create($stream, [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
            $frame = $decoder.Frames[0]
            Assert-ProbeCondition ($frame.PixelWidth -eq $width -and $frame.PixelHeight -eq $height) 'Decoded render dimensions differ from its report.'
            $stride = [int][Math]::Ceiling($frame.PixelWidth * $frame.Format.BitsPerPixel / 8.0)
            $pixels = [byte[]]::new($stride * $frame.PixelHeight)
            $frame.CopyPixels($pixels, $stride, 0)
        }
        catch { throw "PNG pixel decoding failed for '$path': $($_.Exception.Message)" }
        finally { $stream.Dispose() }
        $imageNames += [IO.Path]::GetFileName($path)
    }
    foreach ($name in $requiredImages) { Assert-ProbeCondition ($name -cin $imageNames) "Required image '$name' is missing." }
    return [pscustomobject]@{ ReportPath = $reportPath; BehaviorChecks = $report.checks.Count - $renders.Count; Renders = $renders.Count }
}

function Invoke-DesktopRegression {
    [CmdletBinding()]
    param(
        [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
        [ValidateSet('main', 'settings', 'analysis')][string[]]$Suite = @('main', 'settings', 'analysis'),
        [string]$OutputRoot = 'artifacts\desktop-probes',
        [switch]$NoBuild,
        [ValidateRange(1, 1800)][int]$TimeoutSeconds = 120,
        [ValidateRange(1, 1800)][int]$BuildTimeoutSeconds = 300
    )
    Assert-ProbeCondition $IsWindows 'Desktop probes require Windows and PowerShell 7.'
    Assert-ProbeCondition ($Suite.Count -gt 0) 'At least one suite is required.'
    # PowerShell's ValidateSet accepts different casing; the probe CLI and
    # report contract deliberately use canonical names.
    $Suite = @($Suite | ForEach-Object { $_.ToLowerInvariant() })
    $Configuration = if ($Configuration -ieq 'Release') { 'Release' } else { 'Debug' }
    $root = [IO.Path]::GetFullPath($OutputRoot, $script:RepositoryRoot)
    $runRoot = Join-Path $root ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($runRoot) | Out-Null
    $summaryPath = Join-Path $runRoot 'desktop-regression.json'
    $summary = [ordered]@{ schemaVersion = 1; passed = $false; configuration = $Configuration; noBuild = [bool]$NoBuild; startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); build = $null; suites = @(); error = $null }
    try {
        $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        if (-not $NoBuild) {
            Write-Host '[build] Building solution and desktop probes...'
            $summary.build = Invoke-DesktopChildProcess -FilePath $dotnet -Arguments @('build', (Join-Path $script:RepositoryRoot 'CodexTokenMonitor.slnx'), '-c', $Configuration) -WorkingDirectory $script:RepositoryRoot -LogPrefix (Join-Path $runRoot 'build') -TimeoutSeconds $BuildTimeoutSeconds
            Assert-ProbeCondition (-not $summary.build.TimedOut -and $summary.build.ExitCode -eq 0) "Build failed; see logs in $runRoot"
        }
        $target = "bin\$Configuration\net8.0-windows10.0.19041.0"
        if ($Configuration -eq 'Release') { $target = Join-Path $target 'win-x64' }
        $probeDirectory = Join-Path $script:RepositoryRoot "tests\CodexTokenMonitor.Wpf.Probes\$target"
        $probeDll = Join-Path $probeDirectory 'CodexTokenMonitor.Wpf.Probes.dll'
        $wpfDirectory = Join-Path $script:RepositoryRoot "src\CodexTokenMonitor.Wpf\$target"
        Assert-ProbeCondition (Test-Path -LiteralPath $probeDll -PathType Leaf) 'Probe binary is missing. Run again without -NoBuild.'
        $coreHash = (Get-FileHash -LiteralPath (Join-Path $script:RepositoryRoot "src\CodexTokenMonitor.Core\bin\$Configuration\net8.0\CodexTokenMonitor.Core.dll") -Algorithm SHA256).Hash
        $wpfHash = (Get-FileHash -LiteralPath (Join-Path $wpfDirectory 'CodexTokenMonitor.dll') -Algorithm SHA256).Hash
        Assert-ProbeCondition ((Get-FileHash -LiteralPath (Join-Path $probeDirectory 'CodexTokenMonitor.Core.dll')).Hash -ceq $coreHash -and (Get-FileHash -LiteralPath (Join-Path $probeDirectory 'CodexTokenMonitor.dll')).Hash -ceq $wpfHash) 'Probe copies differ from production binaries. Run again without -NoBuild.'
        $summary['coreSha256'] = $coreHash
        $summary['wpfSha256'] = $wpfHash
        foreach ($name in @($Suite | Select-Object -Unique)) {
            $suiteRoot = Join-Path $runRoot $name
            $item = [ordered]@{ suite = $name; passed = $false; process = $null; validation = $null; error = $null }
            Write-Host "[$name] Running isolated WPF regression..."
            try {
                $item.process = Invoke-DesktopChildProcess -FilePath $dotnet -Arguments @($probeDll, '--suite', $name, '--output', $suiteRoot) -WorkingDirectory $script:RepositoryRoot -LogPrefix (Join-Path $runRoot $name) -TimeoutSeconds $TimeoutSeconds
                Assert-ProbeCondition (-not $item.process.TimedOut) "Suite '$name' timed out after $TimeoutSeconds seconds."
                Assert-ProbeCondition ($item.process.ExitCode -eq 0) "Suite '$name' exited with code $($item.process.ExitCode)."
                $item.validation = Test-DesktopProbeReport -OutputDirectory $suiteRoot -Suite $name -ProcessId $item.process.ProcessId -CoreHash $coreHash -WpfHash $wpfHash
                $item.passed = $true
                Write-Host "[$name] Passed: $($item.validation.BehaviorChecks) behavior checks, $($item.validation.Renders) renders."
            }
            catch { $item.error = $_.Exception.Message; Write-Host "[$name] Failed: $($item.error)" }
            $summary.suites += [pscustomobject]$item
            $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding utf8
        }
        $summary.passed = @($summary.suites | Where-Object { -not $_.passed }).Count -eq 0
    }
    catch { $summary.error = $_.Exception.Message; Write-Host "Desktop regression failed: $($summary.error)" }
    finally {
        $summary['finishedAtUtc'] = [DateTimeOffset]::UtcNow.ToString('O')
        $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    }
    return [pscustomobject]@{ Passed = $summary.passed; ReportPath = $summaryPath }
}

Export-ModuleMember -Function Invoke-DesktopRegression, Invoke-DesktopChildProcess, Test-DesktopProbeReport
