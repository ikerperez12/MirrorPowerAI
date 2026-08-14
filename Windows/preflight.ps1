[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'La aplicación Windows sólo se puede compilar y ejecutar en Windows.'
}

if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'MirrorPowerAI requiere Windows x64.'
}

$osVersion = [Environment]::OSVersion.Version
if ($osVersion.Build -lt 26100) {
    throw "Se requiere Windows 11 24H2 o posterior (build 26100+). Build detectada: $($osVersion.Build)."
}

$dotnetExecutable = Get-MirrorPowerAIDotNet
$dotnetVersion = (& $dotnetExecutable --version).Trim()
$gitVersion = (& git --version)
if ($LASTEXITCODE -ne 0) {
    throw 'Git no está disponible en PATH.'
}

$runtimeRegistryPaths = @(
    'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
)
$runtime = $runtimeRegistryPaths |
    Where-Object { Test-Path -LiteralPath $_ } |
    ForEach-Object { Get-ItemProperty -LiteralPath $_ } |
    Where-Object { $_.Installed -eq 1 } |
    Select-Object -First 1

if ($null -eq $runtime) {
    throw 'Falta Microsoft Visual C++ Redistributable 2015-2022 x64, necesario para Whisper.'
}

[pscustomobject]@{
    WindowsBuild = $osVersion.Build
    Architecture = 'x64'
    DotNetSdk = $dotnetVersion
    DotNetPath = $dotnetExecutable
    VisualCppRuntime = $runtime.Version
    Git = $gitVersion
}
