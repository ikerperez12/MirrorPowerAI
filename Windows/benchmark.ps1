[CmdletBinding()]
param(
    [switch] $NoBuild,
    [switch] $NoRestore,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $BenchmarkArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

$project = Join-Path $PSScriptRoot 'tools\MirrorPowerAI.Benchmark\MirrorPowerAI.Benchmark.csproj'

if (-not $NoRestore) {
    Invoke-MirrorPowerAIDotNet -Arguments @('restore', $project, '--locked-mode')
}

if (-not $NoBuild) {
    Invoke-MirrorPowerAIDotNet -Arguments @('build', $project, '-c', 'Release', '--no-restore')
}

$dotNetArguments = @(
    'run',
    '--project', $project,
    '-c', 'Release',
    '--no-build',
    '--no-restore',
    '--'
) + $BenchmarkArguments

Invoke-MirrorPowerAIDotNet -Arguments $dotNetArguments
