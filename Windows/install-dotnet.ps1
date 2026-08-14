[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredVersion = '10.0.400'
$installDirectory = Join-Path $env:USERPROFILE '.dotnet'
$installerPath = Join-Path $env:TEMP 'MirrorPowerAI-dotnet-install.ps1'

Write-Information "Descargando el instalador oficial de .NET..." -InformationAction Continue
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath

$signature = Get-AuthenticodeSignature -LiteralPath $installerPath
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "La firma Authenticode del instalador de .NET no es válida: $($signature.Status)."
}

& $installerPath -Version $requiredVersion -InstallDir $installDirectory -Architecture x64 -NoPath
if ($LASTEXITCODE -ne 0) {
    throw "La instalación de .NET falló con código $LASTEXITCODE."
}

$dotnetExecutable = Join-Path $installDirectory 'dotnet.exe'
$installedVersion = (& $dotnetExecutable --version).Trim()
if ($installedVersion -ne $requiredVersion) {
    throw "Se instaló $installedVersion, pero se esperaba $requiredVersion."
}

Write-Information "SDK .NET $installedVersion instalado en $installDirectory." -InformationAction Continue
