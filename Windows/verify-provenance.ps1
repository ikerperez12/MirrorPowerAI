[CmdletBinding()]
param(
    [string] $PublishDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\provenance-common.ps1')

function Get-MirrorPowerAIRequiredManifestProperty {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object] $Object,
        [Parameter(Mandatory)]
        [string] $Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw 'El manifiesto de procedencia no contiene los campos requeridos.'
    }

    return $property.Value
}

function Get-MirrorPowerAIManifestInt64 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object] $Value
    )

    if ($Value -isnot [Int64] -and $Value -isnot [Int32]) {
        throw 'El manifiesto de procedencia contiene un tamaño o conteo no válido.'
    }

    $number = [Int64] $Value

    if ($number -lt 0) {
        throw 'El manifiesto de procedencia contiene un tamaño o conteo no válido.'
    }

    return $number
}

function Assert-MirrorPowerAIManifestHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object] $Value
    )

    $hash = [string] $Value
    if ($hash -notmatch '\A[0-9a-f]{64}\z') {
        throw 'El manifiesto de procedencia contiene un hash SHA-256 no válido.'
    }

    return $hash
}

function Get-MirrorPowerAIValidatedManifestFiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object] $Inventory
    )

    $algorithm = [string] (Get-MirrorPowerAIRequiredManifestProperty -Object $Inventory -Name 'algorithm')
    $aggregateFormat = [string] (Get-MirrorPowerAIRequiredManifestProperty -Object $Inventory -Name 'aggregateFormat')
    if (-not [string]::Equals($algorithm, 'SHA-256', [System.StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $aggregateFormat,
            'UTF-8 NUL-delimited path, sizeBytes, sha256 records sorted by ordinal path',
            [System.StringComparison]::Ordinal)) {
        throw 'El manifiesto de procedencia usa un formato de inventario no compatible.'
    }

    $reportedFileCount = Get-MirrorPowerAIManifestInt64 `
        -Value (Get-MirrorPowerAIRequiredManifestProperty -Object $Inventory -Name 'fileCount')
    $reportedAggregate = Assert-MirrorPowerAIManifestHash `
        -Value (Get-MirrorPowerAIRequiredManifestProperty -Object $Inventory -Name 'aggregateSha256')
    $manifestFiles = @(Get-MirrorPowerAIRequiredManifestProperty -Object $Inventory -Name 'files')
    if ($manifestFiles.Count -ne $reportedFileCount) {
        throw 'El conteo del manifiesto de procedencia no coincide con su listado.'
    }

    $validatedFiles = [System.Collections.Generic.List[object]]::new()
    $seenPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $previousPath = $null
    foreach ($manifestFile in $manifestFiles) {
        if ($null -eq $manifestFile) {
            throw 'El manifiesto de procedencia contiene una entrada de fichero no válida.'
        }

        $path = [string] (Get-MirrorPowerAIRequiredManifestProperty -Object $manifestFile -Name 'path')
        if ([string]::IsNullOrWhiteSpace($path) -or
            $path.StartsWith('/', [System.StringComparison]::Ordinal) -or
            $path.IndexOf([System.IO.Path]::DirectorySeparatorChar) -ge 0 -or
            $path -match '(^|/)\.\.?(?:/|$)' -or
            [string]::Equals($path, 'build-provenance.json', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'El manifiesto de procedencia contiene una ruta relativa no válida.'
        }

        if (-not $seenPaths.Add($path)) {
            throw 'El manifiesto de procedencia contiene rutas de fichero ambiguas.'
        }

        if ($null -ne $previousPath -and
            [System.StringComparer]::Ordinal.Compare($previousPath, $path) -ge 0) {
            throw 'El listado de ficheros del manifiesto no está ordenado de forma determinista.'
        }

        $sizeBytes = Get-MirrorPowerAIManifestInt64 `
            -Value (Get-MirrorPowerAIRequiredManifestProperty -Object $manifestFile -Name 'sizeBytes')
        $sha256 = Assert-MirrorPowerAIManifestHash `
            -Value (Get-MirrorPowerAIRequiredManifestProperty -Object $manifestFile -Name 'sha256')
        $validatedFiles.Add([pscustomobject]@{
                path = $path
                sizeBytes = $sizeBytes
                sha256 = $sha256
            })
        $previousPath = $path
    }

    $expectedAggregate = Get-MirrorPowerAIFileInventoryHash -Files @($validatedFiles)
    if (-not [string]::Equals($expectedAggregate, $reportedAggregate, [System.StringComparison]::Ordinal)) {
        throw 'El hash agregado del manifiesto de procedencia no coincide con su listado.'
    }

    return [pscustomobject]@{
        Files = @($validatedFiles | ForEach-Object { $_ })
        AggregateSha256 = $reportedAggregate
    }
}

if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $PSScriptRoot 'artifacts\win-x64'
}

$artifactsRoot = Resolve-MirrorPowerAIPath -Path (Join-Path $PSScriptRoot 'artifacts')
$publishPath = Assert-MirrorPowerAIArtifactPublishPath `
    -ArtifactsRoot $artifactsRoot `
    -PublishDirectory $PublishDirectory `
    -RequirePublishDirectory
$resolvedPublishDirectory = $publishPath.PublishDirectory
Assert-MirrorPowerAIPlainDirectoryTree -DirectoryPath $resolvedPublishDirectory

$manifestPath = Join-Path $resolvedPublishDirectory 'build-provenance.json'
Assert-MirrorPowerAINotReparsePoint -Path $manifestPath -RequireFile | Out-Null
try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw 'No fue posible leer el manifiesto de procedencia como JSON válido.'
}

if ($null -eq $manifest -or (Get-MirrorPowerAIManifestInt64 -Value (Get-MirrorPowerAIRequiredManifestProperty -Object $manifest -Name 'schemaVersion')) -ne 2) {
    throw 'La versión del manifiesto de procedencia no es compatible.'
}

$inventory = Get-MirrorPowerAIRequiredManifestProperty -Object $manifest -Name 'fileInventory'
$expectedInventory = Get-MirrorPowerAIValidatedManifestFiles -Inventory $inventory
$actualFiles = @(Get-MirrorPowerAIPortableFileRecords -PublishDirectory $resolvedPublishDirectory)
if ($actualFiles.Count -ne $expectedInventory.Files.Count) {
    throw 'El portable contiene un número de ficheros distinto al manifiesto de procedencia.'
}

for ($index = 0; $index -lt $actualFiles.Count; $index++) {
    $actualFile = $actualFiles[$index]
    $expectedFile = $expectedInventory.Files[$index]
    if (-not [string]::Equals($actualFile.path, $expectedFile.path, [System.StringComparison]::Ordinal) -or
        $actualFile.sizeBytes -ne $expectedFile.sizeBytes -or
        -not [string]::Equals($actualFile.sha256, $expectedFile.sha256, [System.StringComparison]::Ordinal)) {
        throw 'El listado de ficheros del portable no coincide con el manifiesto de procedencia.'
    }
}

$actualAggregate = Get-MirrorPowerAIFileInventoryHash -Files $actualFiles
if (-not [string]::Equals($actualAggregate, $expectedInventory.AggregateSha256, [System.StringComparison]::Ordinal)) {
    throw 'El hash agregado del portable no coincide con el manifiesto de procedencia.'
}

$artifact = Get-MirrorPowerAIRequiredManifestProperty -Object $manifest -Name 'artifact'
$artifactPath = [string] (Get-MirrorPowerAIRequiredManifestProperty -Object $artifact -Name 'relativePath')
$artifactSize = Get-MirrorPowerAIManifestInt64 `
    -Value (Get-MirrorPowerAIRequiredManifestProperty -Object $artifact -Name 'sizeBytes')
$artifactHash = Assert-MirrorPowerAIManifestHash `
    -Value (Get-MirrorPowerAIRequiredManifestProperty -Object $artifact -Name 'sha256')
if (-not [string]::Equals($artifactPath, 'MirrorPowerAI.Windows.exe', [System.StringComparison]::Ordinal)) {
    throw 'El manifiesto de procedencia no identifica el ejecutable esperado.'
}

$actualExecutable = $actualFiles | Where-Object {
    [string]::Equals($_.path, $artifactPath, [System.StringComparison]::Ordinal)
} | Select-Object -First 1
if ($null -eq $actualExecutable -or
    $actualExecutable.sizeBytes -ne $artifactSize -or
    -not [string]::Equals($actualExecutable.sha256, $artifactHash, [System.StringComparison]::Ordinal)) {
    throw 'El hash adicional del ejecutable no coincide con el portable.'
}

Write-Information (
    'Procedencia verificada: {0} ficheros, hash agregado {1}.' -f
    $actualFiles.Count,
    $actualAggregate) -InformationAction Continue
