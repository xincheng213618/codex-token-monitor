#Requires -Version 7.0
# Tests the runner itself with synthetic reports and short-lived child processes.
# It never constructs application windows or reads account/configuration files.
[CmdletBinding()]
param(
    [string]$OutputRoot = 'artifacts\desktop-probes',
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$IncludeProbeCli
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DesktopProbeRunner.psm1') -Force
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = Join-Path ([IO.Path]::GetFullPath($OutputRoot, $repo)) ('runner-self-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$checks = [Collections.Generic.List[object]]::new()

function Check([bool]$Value, [string]$Name) {
    if (-not $Value) { throw "Runner self-check failed: $Name" }
    $checks.Add([pscustomobject]@{ check = $Name; passed = $true })
}
function Rejects([scriptblock]$Action, [string]$Expected, [string]$Name) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Check ($null -ne $failure -and $failure -like "*$Expected*") $Name
}

try {
    $pwsh = (Get-Process -Id $PID).Path
    $success = Invoke-DesktopChildProcess -FilePath $pwsh -Arguments @('-NoProfile', '-NonInteractive', '-Command', '[Console]::Out.Write("ok"); [Console]::Error.Write("note"); exit 0') -WorkingDirectory $repo -LogPrefix (Join-Path $root 'success') -TimeoutSeconds 15
    Check ($success.ExitCode -eq 0 -and -not $success.TimedOut -and (Get-Content -LiteralPath $success.Stdout -Raw) -ceq 'ok' -and (Get-Content -LiteralPath $success.Stderr -Raw) -ceq 'note') 'success-and-both-output-pipes'
    $nonzero = Invoke-DesktopChildProcess -FilePath $pwsh -Arguments @('-NoProfile', '-NonInteractive', '-Command', 'exit 7') -WorkingDirectory $repo -LogPrefix (Join-Path $root 'nonzero') -TimeoutSeconds 15
    Check ($nonzero.ExitCode -eq 7 -and -not $nonzero.TimedOut) 'nonzero-exit-preserved'
    $argumentScript = Join-Path $root 'arguments with spaces.ps1'
    [IO.File]::WriteAllText($argumentScript, 'param([string]$Value) [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); [Console]::Out.Write($Value); [Console]::Error.Write("中文诊断")')
    $argument = '空格 space "quotes" & $variable `literal`; tail\'
    $argumentsResult = Invoke-DesktopChildProcess -FilePath $pwsh -Arguments @('-NoProfile', '-NonInteractive', '-File', $argumentScript, $argument) -WorkingDirectory $repo -LogPrefix (Join-Path $root 'arguments') -TimeoutSeconds 15
    Check ($argumentsResult.ExitCode -eq 0 -and (Get-Content -LiteralPath $argumentsResult.Stdout -Raw) -ceq $argument -and (Get-Content -LiteralPath $argumentsResult.Stderr -Raw) -ceq '中文诊断') 'literal-arguments-and-utf8-diagnostics'
    $timeout = Invoke-DesktopChildProcess -FilePath $pwsh -Arguments @('-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 30') -WorkingDirectory $repo -LogPrefix (Join-Path $root 'timeout') -TimeoutSeconds 1
    Check ($timeout.TimedOut -and -not (Get-Process -Id $timeout.ProcessId -ErrorAction SilentlyContinue)) 'timeout-terminates-owned-child'
    Rejects { Invoke-DesktopChildProcess -FilePath (Join-Path $root 'does-not-exist.exe') -Arguments @('arg') -WorkingDirectory $repo -LogPrefix (Join-Path $root 'missing') -TimeoutSeconds 1 } 'does-not-exist.exe' 'start-error-retains-original-cause'

    $suiteRoot = Join-Path $root 'synthetic analysis with spaces'
    $fixtureRoot = Join-Path $suiteRoot 'isolated-fixture'
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    Add-Type -AssemblyName PresentationCore
    $bitmap = [Windows.Media.Imaging.BitmapSource]::Create(2, 2, 96, 96, [Windows.Media.PixelFormats]::Bgra32, $null, [byte[]]::new(16), 8)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $imageStream = [IO.MemoryStream]::new()
    try { $encoder.Save($imageStream); $png = $imageStream.ToArray() }
    finally { $imageStream.Dispose() }
    $imageNames = @('quota-cost-curve.png', 'quota-estimate.png', 'cycle-cache-failure-preserved.png', 'cycle-analysis-recovered.png')
    $fixtureChecks = @(
        @{ check = 'cached-windows-bypass-gate'; timelineBefore = 0; timelineAfter = 0 },
        @{ check = 'empty-known-period-fallback' },
        @{ check = 'cached-windows-corruption-recovery' },
        @{ check = 'manual-estimate-corruption-recovery' },
        @{ check = 'cycle-corruption-recovery' },
        @{ check = 'single-window-close'; dispatcherResponsive = $true; queryDrained = $true; parentStillUsable = $true },
        @{ check = 'parent-shutdown'; childDrained = $true; latePublication = $false; newQueryRejected = $true },
        @{ check = 'isolation-and-exit'; isolatedRoot = $fixtureRoot; mainWindowConstructed = $false; sharingServerStarted = $false; allClosed = $true; appServerReads = 0 }
    )
    foreach ($name in $imageNames) {
        $path = Join-Path $suiteRoot $name
        [IO.File]::WriteAllBytes($path, $png)
        $fixtureChecks += @{ check = 'render'; path = $path; width = 2; height = 2 }
    }
    $template = @{
        schemaVersion = 1; suite = 'analysis'; processId = 12345; coreSha256 = ('A' * 64); wpfSha256 = ('B' * 64)
        passed = $true; error = $null; checks = $fixtureChecks; syntheticRunnerFixture = $true
    } | ConvertTo-Json -Depth 8
    $reportPath = Join-Path $suiteRoot 'ui-probe-results.json'
    function Verify([scriptblock]$Mutate = {}) {
        $report = $template | ConvertFrom-Json
        & $Mutate $report
        $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
        Test-DesktopProbeReport -OutputDirectory $suiteRoot -Suite analysis -ProcessId 12345 -CoreHash ('A' * 64) -WpfHash ('B' * 64)
    }
    $valid = Verify
    Check ($valid.BehaviorChecks -eq 8 -and $valid.Renders -eq 4) 'valid-synthetic-report'
    Rejects { Verify { param($r) $r.passed = 'true' } } 'did not pass' 'string-passed-is-not-success'
    Rejects { Verify { param($r) $r.error = 'failure' } } 'did not pass' 'error-cannot-accompany-success'
    Rejects { Verify { param($r) $r.processId = 1 } } 'different process' 'stale-report-process-rejected'
    Rejects { Verify { param($r) $r.coreSha256 = 'wrong' } } 'expected production' 'binary-mismatch-rejected'
    Rejects { Verify { param($r) $r.checks = @($r.checks | Where-Object check -NE 'parent-shutdown') } } 'Required behavior' 'missing-lifecycle-check-rejected'
    Rejects { Verify { param($r) ($r.checks | Where-Object check -EQ 'parent-shutdown').latePublication = $true } } 'latePublication' 'late-result-cannot-pass'
    Rejects { Verify { param($r) ($r.checks | Where-Object check -EQ 'isolation-and-exit').isolatedRoot = $root } } 'Fixture root escaped' 'external-fixture-root-rejected'
    Rejects { Verify { param($r) @($r.checks | Where-Object check -EQ 'render')[0].path = (Join-Path $root 'outside.png') } } 'Render path escaped' 'external-render-path-rejected'
    Rejects { Verify { param($r) @($r.checks | Where-Object check -EQ 'render')[0].width = 3 } } 'dimensions' 'wrong-render-dimensions-rejected'
    Rejects { Verify { param($r) $r.checks = @($r.checks | Where-Object { $_.check -ne 'render' -or $_.path -notlike '*quota-estimate.png' }) } } 'Required image' 'missing-required-render-rejected'
    [IO.File]::WriteAllBytes((Join-Path $suiteRoot $imageNames[0]), [byte[]](0..40))
    Rejects { Verify } 'not a PNG' 'invalid-image-header-rejected'
    [IO.File]::WriteAllBytes((Join-Path $suiteRoot $imageNames[0]), $png[0..32])
    Rejects { Verify } 'PNG pixel decoding failed' 'truncated-image-rejected'
    [IO.File]::WriteAllBytes((Join-Path $suiteRoot $imageNames[0]), $png)
    Verify | Out-Null
    if ($IncludeProbeCli) {
        $target = "bin\$Configuration\net8.0-windows10.0.19041.0"
        if ($Configuration -ieq 'Release') { $target = Join-Path $target 'win-x64' }
        $probe = Join-Path $repo "tests\CodexTokenMonitor.Wpf.Probes\$target\CodexTokenMonitor.Wpf.Probes.dll"
        Check (Test-Path -LiteralPath $probe -PathType Leaf) 'probe-cli-binary-available'
        $dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
        $missingOutput = Join-Path $root 'invalid cli must not create this'
        $unknown = Invoke-DesktopChildProcess -FilePath $dotnet -Arguments @($probe, '--suite', 'unknown', '--output', $missingOutput) -WorkingDirectory $repo -LogPrefix (Join-Path $root 'cli-unknown') -TimeoutSeconds 15
        Check ($unknown.ExitCode -eq 2 -and -not (Test-Path -LiteralPath $missingOutput)) 'unknown-suite-rejected-before-output-creation'
        $duplicate = Invoke-DesktopChildProcess -FilePath $dotnet -Arguments @($probe, '--suite', 'main', '--suite', 'settings') -WorkingDirectory $repo -LogPrefix (Join-Path $root 'cli-duplicate') -TimeoutSeconds 15
        Check ($duplicate.ExitCode -eq 2) 'duplicate-cli-argument-rejected'
        $missing = Invoke-DesktopChildProcess -FilePath $dotnet -Arguments @($probe) -WorkingDirectory $repo -LogPrefix (Join-Path $root 'cli-missing') -TimeoutSeconds 15
        Check ($missing.ExitCode -eq 2) 'missing-cli-arguments-rejected'
        $existingDirectory = Join-Path $root '已有报告 with spaces'
        [IO.Directory]::CreateDirectory($existingDirectory) | Out-Null
        $sentinel = Join-Path $existingDirectory 'ui-probe-results.json'
        [IO.File]::WriteAllText($sentinel, 'existing evidence must remain unchanged')
        $existing = Invoke-DesktopChildProcess -FilePath $dotnet -Arguments @($probe, '--suite', 'main', '--output', $existingDirectory) -WorkingDirectory $repo -LogPrefix (Join-Path $root 'cli-existing') -TimeoutSeconds 15
        Check ($existing.ExitCode -eq 2 -and [IO.File]::ReadAllText($sentinel) -ceq 'existing evidence must remain unchanged' -and @(Get-ChildItem -LiteralPath $existingDirectory).Count -eq 1) 'existing-evidence-is-not-overwritten'
    }
    @{ passed = $true; checks = $checks.ToArray() } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'runner-self-test.json') -Encoding utf8
    Write-Host "Runner self-checks passed: $($checks.Count). Evidence: $root"
}
catch {
    @{ passed = $false; error = $_.Exception.ToString(); checks = $checks.ToArray() } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'runner-self-test.json') -Encoding utf8
    Write-Error $_ -ErrorAction Continue
    exit 1
}
