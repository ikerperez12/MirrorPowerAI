[CmdletBinding()]
param(
    [ValidateRange(1, 120)]
    [int] $TimeoutSeconds = 30,
    [string] $PublishDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'La verificación del runtime Whisper requiere Windows x64.'
}

if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $PSScriptRoot 'artifacts\win-x64'
}

$resolvedPublishDirectory = [System.IO.Path]::GetFullPath($PublishDirectory)
$executable = Join-Path $resolvedPublishDirectory 'MirrorPowerAI.Windows.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'No existe la aplicación publicada. Ejecuta .\Windows\build.ps1 antes de verificar Whisper.'
}

$process = Start-Process `
    -FilePath $executable `
    -ArgumentList '--verify-whisper-runtime' `
    -WorkingDirectory $resolvedPublishDirectory `
    -WindowStyle Hidden `
    -PassThru
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    try {
        $process.Kill()
        $null = $process.WaitForExit(5000)
    }
    catch {
        # The timeout remains the useful failure; cleanup is best effort for this exact child process.
    }

    throw "La carga del runtime Whisper superó el límite de $TimeoutSeconds segundos y se detuvo."
}

if ($process.ExitCode -ne 0) {
    throw "El portable no pudo cargar el runtime Whisper CPU x64 fijado (código de salida $($process.ExitCode))."
}

Write-Information (
    'El portable cargó el runtime Whisper CPU x64 y ejecutó una llamada nativa sin modelo, audio, configuración, secretos ni red.'
) -InformationAction Continue
