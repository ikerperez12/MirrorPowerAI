Set-StrictMode -Version Latest

function Get-MirrorPowerAIPathAttributes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    try {
        return [System.IO.File]::GetAttributes($Path)
    }
    catch [System.IO.FileNotFoundException] {
        return $null
    }
    catch [System.IO.DirectoryNotFoundException] {
        return $null
    }
    catch {
        throw 'No fue posible inspeccionar de forma segura una ruta del portable.'
    }
}

function Test-MirrorPowerAIReparsePoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.IO.FileAttributes] $Attributes
    )

    return (($Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Test-MirrorPowerAIDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.IO.FileAttributes] $Attributes
    )

    return (($Attributes -band [System.IO.FileAttributes]::Directory) -ne 0)
}

function Assert-MirrorPowerAINotReparsePoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [switch] $RequireDirectory,
        [switch] $RequireFile
    )

    $attributes = Get-MirrorPowerAIPathAttributes -Path $Path
    if ($null -eq $attributes) {
        throw 'No existe una ruta requerida del portable.'
    }

    if (Test-MirrorPowerAIReparsePoint -Attributes $attributes) {
        throw 'Se rechazó un reparse point, junction o enlace simbólico en la ruta del portable.'
    }

    $isDirectory = Test-MirrorPowerAIDirectory -Attributes $attributes
    if ($RequireDirectory -and -not $isDirectory) {
        throw 'Se esperaba una carpeta normal dentro de la ruta del portable.'
    }

    if ($RequireFile -and $isDirectory) {
        throw 'Se esperaba un fichero normal dentro de la ruta del portable.'
    }

    return $attributes
}

function Resolve-MirrorPowerAIPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'La ruta del portable no puede estar vacía.'
    }

    try {
        return [System.IO.Path]::GetFullPath($Path)
    }
    catch {
        throw 'No fue posible normalizar una ruta del portable.'
    }
}

function Assert-MirrorPowerAIArtifactPublishPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ArtifactsRoot,
        [Parameter(Mandatory)]
        [string] $PublishDirectory,
        [switch] $CreateArtifactsRoot,
        [switch] $RequirePublishDirectory
    )

    $resolvedArtifactsRoot = Resolve-MirrorPowerAIPath -Path $ArtifactsRoot
    $resolvedPublishDirectory = Resolve-MirrorPowerAIPath -Path $PublishDirectory
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $artifactsPrefix = $resolvedArtifactsRoot + $separator

    if ([string]::Equals(
            $resolvedPublishDirectory,
            $resolvedArtifactsRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedPublishDirectory.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'La carpeta publicada resuelta está fuera de Windows\artifacts.'
    }

    $rootAttributes = Get-MirrorPowerAIPathAttributes -Path $resolvedArtifactsRoot
    if ($null -eq $rootAttributes) {
        if (-not $CreateArtifactsRoot) {
            throw 'No existe la carpeta Windows\artifacts requerida para el portable.'
        }

        try {
            [System.IO.Directory]::CreateDirectory($resolvedArtifactsRoot) | Out-Null
        }
        catch {
            throw 'No fue posible crear la carpeta Windows\artifacts para el portable.'
        }

        $rootAttributes = Get-MirrorPowerAIPathAttributes -Path $resolvedArtifactsRoot
        if ($null -eq $rootAttributes) {
            throw 'No fue posible validar la carpeta Windows\artifacts creada.'
        }
    }

    if (Test-MirrorPowerAIReparsePoint -Attributes $rootAttributes) {
        throw 'Se rechazó un reparse point, junction o enlace simbólico en Windows\artifacts.'
    }

    if (-not (Test-MirrorPowerAIDirectory -Attributes $rootAttributes)) {
        throw 'Windows\artifacts debe ser una carpeta normal.'
    }

    $relativePath = $resolvedPublishDirectory.Substring($artifactsPrefix.Length)
    $relativeSegments = @($relativePath -split '[\\/]')
    if ($relativeSegments.Count -eq 0) {
        throw 'La carpeta publicada no es un descendiente válido de Windows\artifacts.'
    }

    $currentPath = $resolvedArtifactsRoot
    $publishAttributes = $null
    foreach ($segment in $relativeSegments) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..') {
            throw 'La carpeta publicada no contiene segmentos válidos.'
        }

        $currentPath = Join-Path $currentPath $segment
        $currentAttributes = Get-MirrorPowerAIPathAttributes -Path $currentPath
        if ($null -eq $currentAttributes) {
            if ($RequirePublishDirectory) {
                throw 'No existe la carpeta publicada requerida para validar procedencia.'
            }

            break
        }

        if (Test-MirrorPowerAIReparsePoint -Attributes $currentAttributes) {
            throw 'Se rechazó un reparse point, junction o enlace simbólico en la ruta publicada.'
        }

        if (-not (Test-MirrorPowerAIDirectory -Attributes $currentAttributes)) {
            throw 'La ruta publicada debe estar formada sólo por carpetas normales.'
        }

        $publishAttributes = $currentAttributes
    }

    if ($RequirePublishDirectory -and $null -eq $publishAttributes) {
        throw 'No existe la carpeta publicada requerida para validar procedencia.'
    }

    return [pscustomobject]@{
        ArtifactsRoot = $resolvedArtifactsRoot
        PublishDirectory = $resolvedPublishDirectory
    }
}

function Assert-MirrorPowerAIPlainDirectoryTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $DirectoryPath
    )

    $resolvedDirectoryPath = Resolve-MirrorPowerAIPath -Path $DirectoryPath
    Assert-MirrorPowerAINotReparsePoint -Path $resolvedDirectoryPath -RequireDirectory | Out-Null

    $pendingDirectories = [System.Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push($resolvedDirectoryPath)
    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Pop()
        Assert-MirrorPowerAINotReparsePoint -Path $currentDirectory -RequireDirectory | Out-Null

        try {
            $entries = [System.IO.Directory]::GetFileSystemEntries($currentDirectory)
        }
        catch {
            throw 'No fue posible enumerar de forma segura el contenido del portable.'
        }

        foreach ($entry in $entries) {
            $attributes = Assert-MirrorPowerAINotReparsePoint -Path $entry
            if (Test-MirrorPowerAIDirectory -Attributes $attributes) {
                $pendingDirectories.Push($entry)
            }
        }
    }
}

function ConvertTo-MirrorPowerAINormalizedRelativePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RootDirectory,
        [Parameter(Mandatory)]
        [string] $Path
    )

    $resolvedRootDirectory = Resolve-MirrorPowerAIPath -Path $RootDirectory
    $resolvedPath = Resolve-MirrorPowerAIPath -Path $Path
    $prefix = $resolvedRootDirectory + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Un fichero del portable quedó fuera de su carpeta publicada.'
    }

    $relativePath = $resolvedPath.Substring($prefix.Length).
        Replace([System.IO.Path]::DirectorySeparatorChar, [char] '/').
        Replace([System.IO.Path]::AltDirectorySeparatorChar, [char] '/')
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        $relativePath.StartsWith('/', [System.StringComparison]::Ordinal) -or
        $relativePath -match '(^|/)\.\.?(?:/|$)') {
        throw 'Un fichero del portable tiene una ruta relativa no válida.'
    }

    return $relativePath
}

function ConvertTo-MirrorPowerAIHex {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [byte[]] $Bytes
    )

    return [System.BitConverter]::ToString($Bytes).Replace('-', '').ToLowerInvariant()
}

function Get-MirrorPowerAIFileHashRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    Assert-MirrorPowerAINotReparsePoint -Path $Path -RequireFile | Out-Null
    try {
        $stream = [System.IO.FileStream]::new(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
    }
    catch {
        throw 'No fue posible abrir de forma segura un fichero del portable para calcular su hash.'
    }

    try {
        $length = $stream.Length
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = ConvertTo-MirrorPowerAIHex -Bytes $sha256.ComputeHash($stream)
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    Assert-MirrorPowerAINotReparsePoint -Path $Path -RequireFile | Out-Null
    return [pscustomobject]@{
        sizeBytes = [Int64] $length
        sha256 = $hash
    }
}

function Get-MirrorPowerAIPortableFileRecords {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $PublishDirectory,
        [string] $ExcludedRelativePath = 'build-provenance.json'
    )

    $resolvedPublishDirectory = Resolve-MirrorPowerAIPath -Path $PublishDirectory
    Assert-MirrorPowerAIPlainDirectoryTree -DirectoryPath $resolvedPublishDirectory

    $recordsByPath = [System.Collections.Generic.SortedDictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)
    $pendingDirectories = [System.Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push($resolvedPublishDirectory)
    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Pop()
        Assert-MirrorPowerAINotReparsePoint -Path $currentDirectory -RequireDirectory | Out-Null

        try {
            $entries = [System.IO.Directory]::GetFileSystemEntries($currentDirectory)
        }
        catch {
            throw 'No fue posible enumerar de forma segura el contenido del portable.'
        }

        foreach ($entry in $entries) {
            $attributes = Assert-MirrorPowerAINotReparsePoint -Path $entry
            if (Test-MirrorPowerAIDirectory -Attributes $attributes) {
                $pendingDirectories.Push($entry)
                continue
            }

            $relativePath = ConvertTo-MirrorPowerAINormalizedRelativePath `
                -RootDirectory $resolvedPublishDirectory `
                -Path $entry
            if ([string]::Equals(
                    $relativePath,
                    $ExcludedRelativePath,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                if (-not [string]::Equals(
                        $relativePath,
                        $ExcludedRelativePath,
                        [System.StringComparison]::Ordinal)) {
                    throw 'Se encontró una variante ambigua del nombre del manifiesto de procedencia.'
                }

                continue
            }

            if ($recordsByPath.ContainsKey($relativePath)) {
                throw 'Se encontraron rutas relativas duplicadas en el portable.'
            }

            $hashRecord = Get-MirrorPowerAIFileHashRecord -Path $entry
            $recordsByPath.Add($relativePath, [pscustomobject]@{
                    path = $relativePath
                    sizeBytes = $hashRecord.sizeBytes
                    sha256 = $hashRecord.sha256
                })
        }
    }

    return @($recordsByPath.Values | ForEach-Object { $_ })
}

function Get-MirrorPowerAIFileInventoryHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]] $Files
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    $canonicalBytes = [System.IO.MemoryStream]::new()
    try {
        foreach ($file in $Files) {
            $fields = @(
                [string] $file.path,
                ([Int64] $file.sizeBytes).ToString([Globalization.CultureInfo]::InvariantCulture),
                [string] $file.sha256)
            foreach ($field in $fields) {
                $bytes = $encoding.GetBytes($field)
                $canonicalBytes.Write($bytes, 0, $bytes.Length)
                $canonicalBytes.WriteByte(0)
            }
        }

        $canonicalBytes.Position = 0
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ConvertTo-MirrorPowerAIHex -Bytes $sha256.ComputeHash($canonicalBytes)
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $canonicalBytes.Dispose()
    }
}
