[CmdletBinding()]
param(
    [switch] $NoRestore,
    [switch] $RequireCleanWorktree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')
. (Join-Path $PSScriptRoot 'scripts\provenance-common.ps1')

$project = Join-Path $PSScriptRoot 'src\MirrorPowerAI.Windows\MirrorPowerAI.Windows.csproj'
$artifactsRoot = Resolve-MirrorPowerAIPath -Path (Join-Path $PSScriptRoot 'artifacts')
$publishDirectory = Resolve-MirrorPowerAIPath -Path (Join-Path $artifactsRoot 'win-x64')
$publishPath = Assert-MirrorPowerAIArtifactPublishPath `
    -ArtifactsRoot $artifactsRoot `
    -PublishDirectory $publishDirectory `
    -CreateArtifactsRoot
$publishDirectory = $publishPath.PublishDirectory

if ($null -ne (Get-MirrorPowerAIPathAttributes -Path $publishDirectory)) {
    Assert-MirrorPowerAIPlainDirectoryTree -DirectoryPath $publishDirectory
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

if (-not $NoRestore) {
    Invoke-MirrorPowerAIDotNet -Arguments @('restore', $project, '--runtime', 'win-x64', '--locked-mode')
}

Invoke-MirrorPowerAIDotNet -Arguments @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '-o', $publishDirectory
)

& (Join-Path $PSScriptRoot 'write-provenance.ps1') `
    -PublishDirectory $publishDirectory `
    -RequireCleanWorktree:$RequireCleanWorktree

Write-Information 'Aplicación publicada como portable win-x64.' -InformationAction Continue
