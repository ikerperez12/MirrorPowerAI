[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$executable = Join-Path $PSScriptRoot 'artifacts\win-x64\MirrorPowerAI.Windows.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'No existe la aplicación publicada. Ejecuta .\Windows\build.ps1 antes de verificar el overlay.'
}

$process = Start-Process -FilePath $executable -ArgumentList '--verify-overlay' -PassThru -Wait
if ($process.ExitCode -ne 0) {
    throw "Windows no confirmó WDA_EXCLUDEFROMCAPTURE (código de salida $($process.ExitCode))."
}

Write-Information 'WDA_EXCLUDEFROMCAPTURE se aplicó y se leyó correctamente en una ventana WPF real.' -InformationAction Continue
