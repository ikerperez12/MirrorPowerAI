[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Manifest,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputJson,
    [Parameter(Mandatory)]
    [ValidateSet('base', 'small')]
    [string] $Model,
    [Parameter(Mandatory)]
    [ValidateSet('es')]
    [string] $Language,
    [Parameter(Mandatory)]
    [ValidateRange(1, 32)]
    [int] $Threads,
    [string] $ModelDirectory,
    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# This wrapper is the offline stability gate. Disable the SDK's background
# workload-advertising check in addition to using --no-restore everywhere.
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

function Invoke-MirrorPowerAICorpusDotNet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $DotNetExecutable,
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    # Native stderr is an ErrorRecord in Windows PowerShell when
    # ErrorActionPreference is Stop. Capture it under Continue so a failed
    # dotnet invocation cannot abort this wrapper and render its command/path.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $DotNetExecutable @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        # Never emit a native command line or captured output, which could
        # otherwise contain operator paths.
        return [pscustomobject]@{
            Succeeded = $false
        }
    }

    return [pscustomobject]@{
        Succeeded = $true
    }
}

function Stop-MirrorPowerAICorpusBenchmark {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Message
    )

    # Keep an automation-visible non-zero contract without throwing an error
    # record that would render the script invocation or operator paths.
    [Console]::Error.WriteLine($Message)
    [Environment]::ExitCode = 1
    $global:LASTEXITCODE = 1
    $Host.SetShouldExit(1)
}

$project = Join-Path $PSScriptRoot 'tools\MirrorPowerAI.Benchmark\MirrorPowerAI.Benchmark.csproj'
try {
    $dotNetExecutable = Get-MirrorPowerAIDotNet
}
catch {
    Stop-MirrorPowerAICorpusBenchmark -Message 'No se pudo preparar el benchmark de corpus.'
    return
}

if (-not $NoBuild) {
    # Dependencies must have been restored separately before entering the
    # offline gate. This build fails closed when assets are absent.
    $build = Invoke-MirrorPowerAICorpusDotNet `
        -DotNetExecutable $dotNetExecutable `
        -Arguments @('build', $project, '-c', 'Release', '--no-restore')
    if (-not $build.Succeeded) {
        Stop-MirrorPowerAICorpusBenchmark -Message 'No se pudo compilar el benchmark de corpus.'
        return
    }
}

$benchmarkArguments = @(
    'run',
    '--project', $project,
    '-c', 'Release',
    '--no-build',
    '--no-restore',
    '--',
    '--corpus-manifest', $Manifest,
    '--output-json', $OutputJson,
    '--model', $Model,
    '--language', $Language,
    '--threads', $Threads.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    '--stable'
)

if (-not [string]::IsNullOrWhiteSpace($ModelDirectory)) {
    $benchmarkArguments += @('--model-dir', $ModelDirectory)
}

$benchmark = Invoke-MirrorPowerAICorpusDotNet `
    -DotNetExecutable $dotNetExecutable `
    -Arguments $benchmarkArguments
if (-not $benchmark.Succeeded) {
    Stop-MirrorPowerAICorpusBenchmark -Message 'El benchmark de corpus no pudo completarse de forma segura.'
    return
}

# The tool reaches this point only after writing its aggregate JSON atomically.
# Keep the wrapper's console channel fixed rather than forwarding arbitrary
# native output; the JSON is the durable aggregate evidence.
[Console]::Out.WriteLine('Benchmark de corpus completado de forma segura.')
