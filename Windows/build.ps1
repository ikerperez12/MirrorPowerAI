[CmdletBinding()]
param(
    [switch] $SkipPublish,
    [switch] $ReleaseGate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

if ($ReleaseGate -and $SkipPublish) {
    throw 'ReleaseGate requiere publicar primero; no se puede combinar con -SkipPublish.'
}

if ($ReleaseGate -and (
        -not [Environment]::UserInteractive -or
        -not [string]::IsNullOrWhiteSpace($env:GITHUB_ACTIONS) -or
        -not [string]::IsNullOrWhiteSpace($env:CI))) {
    throw 'ReleaseGate requiere una sesión local e interactiva de Windows; no se ejecuta en CI.'
}

& (Join-Path $PSScriptRoot 'preflight.ps1') | Format-List

$solution = Join-Path $PSScriptRoot 'MirrorPowerAI.slnx'
Invoke-MirrorPowerAIDotNet -Arguments @('restore', $solution, '--locked-mode')
Invoke-MirrorPowerAIDotNet -Arguments @('build', $solution, '-c', 'Release', '--no-restore')
& (Join-Path $PSScriptRoot 'test.ps1') -NoBuild -NoRestore

if (-not $SkipPublish) {
    $windowsProject = Join-Path $PSScriptRoot 'src\MirrorPowerAI.Windows\MirrorPowerAI.Windows.csproj'
    Invoke-MirrorPowerAIDotNet -Arguments @('restore', $windowsProject, '--runtime', 'win-x64', '--locked-mode')
    & (Join-Path $PSScriptRoot 'publish.ps1') -NoRestore

    if ($ReleaseGate) {
        & (Join-Path $PSScriptRoot 'verify-overlay.ps1')
    }
}
