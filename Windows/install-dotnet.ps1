[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredVersion = '10.0.400'
$installDirectory = Join-Path $env:USERPROFILE '.dotnet'
$installerPath = Join-Path $env:TEMP "MirrorPowerAI-dotnet-install-$([Guid]::NewGuid().ToString('N')).ps1"

try {
    Write-Information "Descargando el instalador oficial de .NET..." -InformationAction Continue
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath

    $signature = Get-AuthenticodeSignature -LiteralPath $installerPath
    $microsoftSubject = 'O=Microsoft Corporation'
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        -not $signature.SignerCertificate.Subject.Contains($microsoftSubject, [StringComparison]::Ordinal)) {
        throw "El instalador de .NET no tiene una firma Authenticode válida de Microsoft."
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
}
finally {
    if (Test-Path -LiteralPath $installerPath) {
        Remove-Item -LiteralPath $installerPath -Force
    }
}
