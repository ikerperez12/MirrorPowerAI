[CmdletBinding()]
param(
    [switch] $NoBuild,
    [switch] $NoRestore,
    [switch] $ShowTranscript,
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

$transcriptArgument = if ($ShowTranscript) {
    @('--show-transcript')
} else {
    @()
}

$dotNetArguments = @(
    'run',
    '--project', $project,
    '-c', 'Release',
    '--no-build',
    '--no-restore',
    '--'
) + $transcriptArgument + $BenchmarkArguments

# Invoke the benchmark directly so a non-zero exit cannot be rethrown by the
# common helper with the complete command line (which may contain local paths).
$dotNetExecutable = Get-MirrorPowerAIDotNet
& $dotNetExecutable @dotNetArguments
$benchmarkExitCode = $LASTEXITCODE
if ($benchmarkExitCode -ne 0) {
    throw "El benchmark finalizó con código interno $benchmarkExitCode."
}
