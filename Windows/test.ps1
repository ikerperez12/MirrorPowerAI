[CmdletBinding()]
param(
    [switch] $NoBuild,
    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

$solution = Join-Path $PSScriptRoot 'MirrorPowerAI.slnx'
$resultsDirectory = Join-Path $PSScriptRoot 'artifacts\test-results'
New-Item -ItemType Directory -Force -Path $resultsDirectory | Out-Null

if (-not $NoRestore) {
    Invoke-MirrorPowerAIDotNet -Arguments @('restore', $solution, '--locked-mode')
}

if (-not $NoBuild) {
    Invoke-MirrorPowerAIDotNet -Arguments @('build', $solution, '-c', 'Release', '--no-restore')
}

$coreTestProject = Join-Path $PSScriptRoot 'tests\MirrorPowerAI.Core.Tests\MirrorPowerAI.Core.Tests.csproj'
$coverageOutput = Join-Path $resultsDirectory 'core-coverage.xml'
Invoke-MirrorPowerAIDotNet -Arguments @(
    'test', $coreTestProject,
    '-c', 'Release',
    '--no-build',
    '--no-restore',
    '--logger', 'trx;LogFileName=core-tests.trx',
    '--results-directory', $resultsDirectory,
    '/p:CollectCoverage=true',
    '/p:CoverletOutputFormat=cobertura',
    "/p:CoverletOutput=$coverageOutput",
    '/p:Threshold=80',
    '/p:ThresholdType=line',
    '/p:ThresholdStat=total'
)

$windowsTestProject = Join-Path $PSScriptRoot 'tests\MirrorPowerAI.Windows.Tests\MirrorPowerAI.Windows.Tests.csproj'
Invoke-MirrorPowerAIDotNet -Arguments @(
    'test', $windowsTestProject,
    '-c', 'Release',
    '--no-build',
    '--no-restore',
    '--logger', 'trx;LogFileName=windows-tests.trx',
    '--results-directory', $resultsDirectory
)

$benchmarkTestProject = Join-Path $PSScriptRoot 'tests\MirrorPowerAI.Benchmark.Tests\MirrorPowerAI.Benchmark.Tests.csproj'
Invoke-MirrorPowerAIDotNet -Arguments @(
    'test', $benchmarkTestProject,
    '-c', 'Release',
    '--no-build',
    '--no-restore',
    '--logger', 'trx;LogFileName=benchmark-tests.trx',
    '--results-directory', $resultsDirectory
)
