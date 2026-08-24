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
    throw 'La verificación UI requiere una sesión local e interactiva de Windows; no se ejecuta en CI.'
}

$executable = Join-Path $PSScriptRoot 'artifacts\win-x64\MirrorPowerAI.Windows.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'No existe la aplicación publicada. Ejecuta .\Windows\build.ps1 antes de verificar la interfaz.'
}

$process = Start-Process -FilePath $executable -ArgumentList '--verify-ui' -PassThru
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    try {
        # Only the exact diagnostic child is terminated. It has no helper processes and no normal app
        # resources, so this cannot target an existing user session.
        $process.Kill()
        $null = $process.WaitForExit(5000)
    }
    catch {
        # The original timeout failure remains the useful diagnostic.
    }

    throw "La verificación UI superó el límite de $TimeoutSeconds segundos y se detuvo."
}

if ($process.ExitCode -ne 0) {
    throw "Windows no confirmó el ciclo WPF de configuración y overlay (código de salida $($process.ExitCode))."
}

Write-Information (
    'La interfaz WPF real cargó valores predeterminados en una ruta temporal aislada, mostró ambos proveedores, expuso controles UIA y cerró configuración y overlay sin usar configuración del usuario, DPAPI, audio real, modelos ni red.'
) -InformationAction Continue
