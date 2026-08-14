Set-StrictMode -Version Latest

$script:RequiredSdkVersion = '10.0.400'

function Get-MirrorPowerAIDotNet {
    [CmdletBinding()]
    param()

    $candidatePaths = [System.Collections.Generic.List[string]]::new()
    $localDotNet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotNet) {
        $candidatePaths.Add($localDotNet)
    }

    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $dotnetCommand -and -not $candidatePaths.Contains($dotnetCommand.Source)) {
        $candidatePaths.Add($dotnetCommand.Source)
    }

    foreach ($candidatePath in $candidatePaths) {
        $detectedVersion = (& $candidatePath --version 2>$null)
        if ($LASTEXITCODE -eq 0 -and $detectedVersion.Trim() -eq $script:RequiredSdkVersion) {
            return $candidatePath
        }
    }

    throw "No se encontró el SDK .NET $($script:RequiredSdkVersion). Instálalo con Windows\install-dotnet.ps1."
}

function Invoke-MirrorPowerAIDotNet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    $dotnetExecutable = Get-MirrorPowerAIDotNet
    & $dotnetExecutable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet finalizó con código ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}
