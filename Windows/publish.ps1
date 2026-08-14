[CmdletBinding()]
param(
    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

$project = Join-Path $PSScriptRoot 'src\MirrorPowerAI.Windows\MirrorPowerAI.Windows.csproj'
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'win-x64'))
if (-not $publishDirectory.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'La carpeta de publicación resuelta está fuera de Windows\artifacts.'
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

if (-not $NoRestore) {
    Invoke-MirrorPowerAIDotNet -Arguments @('restore', $project, '--runtime', 'win-x64', '--locked-mode')
}

Invoke-MirrorPowerAIDotNet -Arguments @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'false',
    '--no-restore',
    '-o', $publishDirectory
)

Write-Information "Aplicación publicada en $publishDirectory" -InformationAction Continue
