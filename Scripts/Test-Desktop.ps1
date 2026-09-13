#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidateSet('main', 'settings', 'analysis')][string[]]$Suite = @('main', 'settings', 'analysis'),
    [string]$OutputRoot = 'artifacts\desktop-probes',
    [switch]$NoBuild,
    [ValidateRange(1, 1800)][int]$TimeoutSeconds = 120,
    [ValidateRange(1, 1800)][int]$BuildTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DesktopProbeRunner.psm1') -Force
try {
    $result = Invoke-DesktopRegression @PSBoundParameters
    Write-Host "Desktop report: $($result.ReportPath)"
    if (-not $result.Passed) { exit 1 }
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
