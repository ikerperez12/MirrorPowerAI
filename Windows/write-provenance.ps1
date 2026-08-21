[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublishDirectory,
    [switch] $RequireCleanWorktree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')
. (Join-Path $PSScriptRoot 'scripts\provenance-common.ps1')

$repositoryRoot = Resolve-MirrorPowerAIPath -Path (Join-Path $PSScriptRoot '..')
$artifactsRoot = Resolve-MirrorPowerAIPath -Path (Join-Path $PSScriptRoot 'artifacts')
$publishPath = Assert-MirrorPowerAIArtifactPublishPath `
    -ArtifactsRoot $artifactsRoot `
    -PublishDirectory $PublishDirectory `
    -RequirePublishDirectory
$resolvedPublishDirectory = $publishPath.PublishDirectory
$executableRelativePath = 'MirrorPowerAI.Windows.exe'
$executable = Join-Path $resolvedPublishDirectory $executableRelativePath
Assert-MirrorPowerAINotReparsePoint -Path $executable -RequireFile | Out-Null

function Invoke-RepositoryGit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    $result = @(& git -C $repositoryRoot @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw 'No fue posible obtener la procedencia Git del candidato publicado.'
    }

    return $result
}

function Write-MirrorPowerAIAtomicProvenanceFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [string] $Content
    )

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    Assert-MirrorPowerAINotReparsePoint -Path $directory -RequireDirectory | Out-Null

    $existingAttributes = Get-MirrorPowerAIPathAttributes -Path $Path
    if ($null -ne $existingAttributes) {
        Assert-MirrorPowerAINotReparsePoint -Path $Path -RequireFile | Out-Null
    }

    $temporaryPath = Join-Path $directory ('.build-provenance.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $backupPath = $null
    try {
        [System.IO.File]::WriteAllText(
            $temporaryPath,
            $Content,
            [System.Text.UTF8Encoding]::new($false))
        Assert-MirrorPowerAINotReparsePoint -Path $temporaryPath -RequireFile | Out-Null

        Assert-MirrorPowerAINotReparsePoint -Path $directory -RequireDirectory | Out-Null
        $existingAttributes = Get-MirrorPowerAIPathAttributes -Path $Path
        if ($null -ne $existingAttributes) {
            Assert-MirrorPowerAINotReparsePoint -Path $Path -RequireFile | Out-Null
            $backupPath = Join-Path $directory ('.build-provenance.' + [Guid]::NewGuid().ToString('N') + '.backup')
            [System.IO.File]::Replace($temporaryPath, $Path, $backupPath)
            Assert-MirrorPowerAINotReparsePoint -Path $backupPath -RequireFile | Out-Null
            [System.IO.File]::Delete($backupPath)
            $backupPath = $null
        }
        else {
            [System.IO.File]::Move($temporaryPath, $Path)
        }

        $temporaryPath = $null
    }
    catch {
        throw 'No fue posible escribir de forma atómica el manifiesto de procedencia.'
    }
    finally {
        if ($null -ne $temporaryPath) {
            $temporaryAttributes = Get-MirrorPowerAIPathAttributes -Path $temporaryPath
            if ($null -ne $temporaryAttributes -and -not (Test-MirrorPowerAIReparsePoint -Attributes $temporaryAttributes)) {
                [System.IO.File]::Delete($temporaryPath)
            }
        }

        if ($null -ne $backupPath) {
            $backupAttributes = Get-MirrorPowerAIPathAttributes -Path $backupPath
            if ($null -ne $backupAttributes -and -not (Test-MirrorPowerAIReparsePoint -Attributes $backupAttributes)) {
                [System.IO.File]::Delete($backupPath)
            }
        }
    }
}

$commit = (Invoke-RepositoryGit -Arguments @('rev-parse', 'HEAD') | Select-Object -First 1).Trim()
$branch = (Invoke-RepositoryGit -Arguments @('rev-parse', '--abbrev-ref', 'HEAD') | Select-Object -First 1).Trim()
$worktreeStatus = @(Invoke-RepositoryGit -Arguments @('status', '--porcelain=v1', '--untracked-files=all'))
$isClean = $worktreeStatus.Count -eq 0
if ($RequireCleanWorktree -and -not $isClean) {
    throw 'La compuerta de release requiere un árbol Git limpio; no se generó procedencia del candidato.'
}

$dotnetExecutable = Get-MirrorPowerAIDotNet
$dotnetVersion = (& $dotnetExecutable --version).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'No fue posible determinar el SDK .NET de la publicación.'
}

$fileRecords = @(Get-MirrorPowerAIPortableFileRecords -PublishDirectory $resolvedPublishDirectory)
$executableRecord = $null
foreach ($fileRecord in $fileRecords) {
    if ([string]::Equals($fileRecord.path, $executableRelativePath, [System.StringComparison]::Ordinal)) {
        $executableRecord = $fileRecord
        break
    }
}

if ($null -eq $executableRecord) {
    throw 'El inventario del portable no contiene el ejecutable esperado.'
}

$fileInventoryHash = Get-MirrorPowerAIFileInventoryHash -Files $fileRecords
$manifestFileRecords = @(
    foreach ($fileRecord in $fileRecords) {
        [ordered]@{
            path = [string] $fileRecord.path
            sizeBytes = [Int64] $fileRecord.sizeBytes
            sha256 = [string] $fileRecord.sha256
        }
    }
)
$provenance = [ordered]@{
    schemaVersion = 2
    generatedAtUtc = [DateTime]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    source = [ordered]@{
        commit = $commit
        branch = $branch
        isClean = $isClean
    }
    build = [ordered]@{
        dotnetSdk = $dotnetVersion
        runtimeIdentifier = 'win-x64'
        targetFramework = 'net10.0-windows'
    }
    artifact = [ordered]@{
        relativePath = $executableRelativePath
        sizeBytes = $executableRecord.sizeBytes
        sha256 = $executableRecord.sha256
    }
    fileInventory = [ordered]@{
        algorithm = 'SHA-256'
        aggregateFormat = 'UTF-8 NUL-delimited path, sizeBytes, sha256 records sorted by ordinal path'
        fileCount = $fileRecords.Count
        aggregateSha256 = $fileInventoryHash
        files = $manifestFileRecords
    }
}

$jsonOptions = [System.Text.Json.JsonSerializerOptions]::new()
$jsonOptions.WriteIndented = $true
$json = [System.Text.Json.JsonSerializer]::Serialize($provenance, $jsonOptions)
$manifestPath = Join-Path $resolvedPublishDirectory 'build-provenance.json'
Write-MirrorPowerAIAtomicProvenanceFile -Path $manifestPath -Content ($json + [Environment]::NewLine)

$writtenFileRecords = @(Get-MirrorPowerAIPortableFileRecords -PublishDirectory $resolvedPublishDirectory)
$writtenFileInventoryHash = Get-MirrorPowerAIFileInventoryHash -Files $writtenFileRecords
if ($writtenFileRecords.Count -ne $fileRecords.Count -or
    -not [string]::Equals($writtenFileInventoryHash, $fileInventoryHash, [System.StringComparison]::Ordinal)) {
    throw 'El contenido del portable cambió durante la generación del manifiesto de procedencia.'
}

Write-Information 'Procedencia escrita para el portable win-x64.' -InformationAction Continue
