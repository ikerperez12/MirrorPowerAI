[CmdletBinding()]
param(
    [switch] $RequireAudibleSignal,
    [ValidateRange(10, 120)]
    [int] $TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not [Environment]::UserInteractive -or
    -not [string]::IsNullOrWhiteSpace($env:GITHUB_ACTIONS) -or
    -not [string]::IsNullOrWhiteSpace($env:CI)) {
    throw 'La verificación WASAPI requiere una sesión local e interactiva de Windows; no se ejecuta en CI.'
}

$executable = Join-Path $PSScriptRoot 'artifacts\win-x64\MirrorPowerAI.Windows.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'No existe la aplicación publicada. Ejecuta .\Windows\build.ps1 antes de verificar WASAPI.'
}

$arguments = @('--verify-wasapi')
if ($RequireAudibleSignal) {
    $arguments += '--require-audible-signal'
}

$process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    try {
        $process.Kill()
        $null = $process.WaitForExit(5000)
    }
    catch {
        # The original timeout failure remains the useful diagnostic; only the exact child process is targeted.
    }

    throw "La verificación WASAPI superó el límite de $TimeoutSeconds segundos y se detuvo."
}

switch ($process.ExitCode) {
    0 { return }
    2 { throw 'WASAPI capturó muestras normalizadas, pero no detectó señal audible.' }
    default { throw "Windows no confirmó la captura loopback WASAPI (código de salida $($process.ExitCode))." }
}
