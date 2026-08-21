[CmdletBinding()]
param(
    [ValidateRange(1, 120)]
    [int] $TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not [Environment]::UserInteractive -or
    -not [string]::IsNullOrWhiteSpace($env:GITHUB_ACTIONS) -or
    -not [string]::IsNullOrWhiteSpace($env:CI)) {
    throw 'La verificación del shell requiere una sesión local e interactiva de Windows; no se ejecuta en CI.'
}

$executable = Join-Path $PSScriptRoot 'artifacts\win-x64\MirrorPowerAI.Windows.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'No existe la aplicación publicada. Ejecuta .\Windows\build.ps1 antes de verificar el shell.'
}

$process = Start-Process -FilePath $executable -ArgumentList '--verify-shell' -PassThru
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    try {
        $process.Kill()
        $null = $process.WaitForExit(5000)
    }
    catch {
        # The original timeout failure remains the useful diagnostic; only the exact child process is targeted.
    }

    throw "La verificación del shell superó el límite de $TimeoutSeconds segundos y se detuvo."
}

if ($process.ExitCode -ne 0) {
    throw "Windows no confirmó la bandeja, mutex y hotkey global (código de salida $($process.ExitCode))."
}

Write-Information 'La bandeja, el mutex por usuario y Alt+Shift+L se inicializaron y liberaron en recursos Windows reales.' -InformationAction Continue
