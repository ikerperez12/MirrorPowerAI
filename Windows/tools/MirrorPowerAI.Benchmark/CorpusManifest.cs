using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MirrorPowerAI.Benchmark;

/// <summary>
/// Represents a validated local corpus manifest. Asset paths are retained only
/// in memory and are never included in a benchmark result or diagnostic.
/// </summary>
internal sealed record CorpusManifest(
    string Id,
    string Revision,
    string License,
    string Source,
    string ManifestSha256,
    string DirectoryPath,
    IReadOnlyList<CorpusManifestItem> Items);

/// <summary>
/// Represents one syntactically valid corpus entry. Content integrity is
/// checked again immediately before each inference operation.
/// </summary>
internal sealed record CorpusManifestItem(
    string Id,
    string AudioPath,
    string AudioSha256,
    string ReferencePath,
    string ReferenceSha256);

/// <summary>
/// Provides the minimum filesystem surface needed to inspect an untrusted
/// local manifest. The seam keeps reparse-point behaviour testable without
/// requiring a developer-mode symlink in the test environment.
/// </summary>
internal interface ICorpusFileSystem
{
    bool FileExists(string path);

    long GetFileLength(string path);

    FileAttributes GetAttributes(string path);

    Task<byte[]> ReadAllBytesAsync(
        string path,
        string owningDirectory,
        int maximumBytes,
        CancellationToken cancellationToken);
}

internal sealed class PhysicalCorpusFileSystem : ICorpusFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public async Task<byte[]> ReadAllBytesAsync(
        string path,
        string owningDirectory,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        WindowsPathSafety.EnsureOpenFileIsUnderDirectory(stream, owningDirectory);
        if (stream.Length > maximumBytes)
        {
            throw new IOException("El manifiesto de corpus supera el límite permitido.");
        }

        var content = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < content.Length)
        {
            var read = await stream
                .ReadAsync(content.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("No se pudo leer el manifiesto de corpus completo.");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new IOException("El manifiesto de corpus supera el límite permitido.");
        }

        return content;
    }
}

internal interface ICorpusManifestLoader
{
    Task<CorpusManifest> LoadAsync(string manifestPath, CancellationToken cancellationToken);
}

/// <summary>
/// Loads only the closed v1 corpus manifest schema. It rejects traversal,
/// links and junctions so a manifest cannot make the benchmark inspect files
/// outside its own directory tree.
/// </summary>
internal sealed class CorpusManifestLoader : ICorpusManifestLoader
{
    internal const int ManifestVersion = 1;
    internal const int MaximumManifestBytes = 1024 * 1024;
    internal const int MaximumItemCount = 10_000;
    internal const int MaximumReferenceBytes = 1024 * 1024;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly HashSet<string> RootProperties =
        new(StringComparer.Ordinal)
        {
            "version",
            "id",
            "revision",
            "license",
            "source",
            "items",
        };
    private static readonly HashSet<string> ItemProperties =
        new(StringComparer.Ordinal)
        {
            "id",
            "audio",
            "audioSha256",
            "reference",
            "referenceSha256",
        };

    private readonly ICorpusFileSystem _fileSystem;

    public CorpusManifestLoader(ICorpusFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new PhysicalCorpusFileSystem();
    }

    /// <inheritdoc />
    public async Task<CorpusManifest> LoadAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullManifestPath = ResolveManifestPath(manifestPath);
        var manifestDirectory = Path.GetDirectoryName(fullManifestPath);
        if (string.IsNullOrWhiteSpace(manifestDirectory))
        {
            throw Invalid("El manifiesto de corpus no tiene un directorio válido.");
        }

        try
        {
            EnsureNotReparsePath(manifestDirectory, fullManifestPath);
            if (!_fileSystem.FileExists(fullManifestPath))
            {
                throw Invalid("No se encuentra el manifiesto local de corpus.");
            }

            if (_fileSystem.GetFileLength(fullManifestPath) > MaximumManifestBytes)
            {
                throw Invalid("El manifiesto de corpus supera el límite permitido.");
            }

            // Read, hash, and decode one bounded byte representation. The digest
            // emitted in the result therefore identifies exactly the metadata
            // parsed below instead of a second, potentially changed read.
            var manifestBytes = await _fileSystem
                .ReadAllBytesAsync(
                    fullManifestPath,
                    manifestDirectory,
                    MaximumManifestBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
            var json = StrictUtf8.GetString(manifestBytes);
            return ParseManifest(json, manifestDirectory, manifestSha256);
        }
        catch (CorpusManifestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or DecoderFallbackException
            or JsonException)
        {
            throw Invalid("No se pudo leer un manifiesto de corpus válido.", exception);
        }
    }

    internal CorpusManifest ParseManifest(
        string json,
        string manifestDirectory,
        string? manifestSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDirectory);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
        }
        catch (JsonException exception)
        {
            throw Invalid("El manifiesto de corpus no es JSON v1 válido.", exception);
        }

        using (document)
        {
            var root = GetClosedObject(document.RootElement, RootProperties);
            var version = RequireInt32(root, "version");
            if (version != ManifestVersion)
            {
                throw Invalid("La versión del manifiesto de corpus no es compatible.");
            }

            var corpusId = RequireString(root, "id");
            var revision = RequireString(root, "revision");
            var license = RequireString(root, "license");
            var source = RequireSource(root);
            if (!IsSafeId(corpusId) || !IsSafeMetadata(revision) || !IsSafeMetadata(license))
            {
                throw Invalid("El manifiesto de corpus contiene metadatos no válidos.");
            }

            manifestSha256 ??= Convert.ToHexStringLower(
                SHA256.HashData(StrictUtf8.GetBytes(json)));
            if (!IsSha256(manifestSha256))
            {
                throw Invalid("El manifiesto de corpus contiene un SHA-256 no válido.");
            }

            var itemsElement = RequireProperty(root, "items");
            if (itemsElement.ValueKind != JsonValueKind.Array || itemsElement.GetArrayLength() is 0 or > MaximumItemCount)
            {
                throw Invalid("El manifiesto de corpus debe contener una cantidad válida de elementos.");
            }

            var normalizedDirectory = NormalizeDirectory(manifestDirectory);
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<CorpusManifestItem>(itemsElement.GetArrayLength());
            foreach (var itemElement in itemsElement.EnumerateArray())
            {
                var item = ParseItem(itemElement, normalizedDirectory);
                if (!identifiers.Add(item.Id))
                {
                    throw Invalid("El manifiesto de corpus contiene identificadores duplicados.");
                }

                if (!files.Add(item.AudioPath) || !files.Add(item.ReferencePath))
                {
                    throw Invalid("El manifiesto de corpus reutiliza un archivo de entrada.");
                }

                EnsureOwnedInputFile(normalizedDirectory, item.AudioPath, maximumFileBytes: null);
                EnsureOwnedInputFile(normalizedDirectory, item.ReferencePath, MaximumReferenceBytes);
                items.Add(item);
            }

            return new CorpusManifest(
                corpusId,
                revision,
                license,
                source,
                manifestSha256,
                normalizedDirectory,
                items);
        }
    }

    private static CorpusManifestItem ParseItem(JsonElement itemElement, string manifestDirectory)
    {
        var item = GetClosedObject(itemElement, ItemProperties);
        var id = RequireString(item, "id");
        if (!IsSafeId(id))
        {
            throw Invalid("El manifiesto de corpus contiene un identificador no válido.");
        }

        var audioPath = ResolveOwnedRelativePath(manifestDirectory, RequireString(item, "audio"));
        var referencePath = ResolveOwnedRelativePath(manifestDirectory, RequireString(item, "reference"));
        if (!string.Equals(Path.GetExtension(audioPath), ".wav", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(referencePath), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("El manifiesto de corpus requiere archivos WAV y TXT.");
        }

        var audioSha256 = RequireSha256(item, "audioSha256");
        var referenceSha256 = RequireSha256(item, "referenceSha256");
        return new CorpusManifestItem(id, audioPath, audioSha256, referencePath, referenceSha256);
    }

    private void EnsureOwnedInputFile(string manifestDirectory, string path, int? maximumFileBytes)
    {
        try
        {
            EnsureNotReparsePath(manifestDirectory, path);
            if (!_fileSystem.FileExists(path))
            {
                throw Invalid("Falta un archivo declarado por el manifiesto de corpus.");
            }

            if (maximumFileBytes is int maximumBytes && _fileSystem.GetFileLength(path) > maximumBytes)
            {
                throw Invalid("Un archivo de referencia del corpus supera el límite permitido.");
            }
        }
        catch (CorpusManifestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw Invalid("No se pudo comprobar un archivo del manifiesto de corpus.", exception);
        }
    }

    private void EnsureNotReparsePath(string manifestDirectory, string targetPath)
    {
        var current = NormalizeDirectory(manifestDirectory);
        EnsureNotReparsePoint(current);
        var relative = Path.GetRelativePath(current, targetPath);
        if (Path.IsPathRooted(relative) || ContainsTraversalSegment(relative))
        {
            throw Invalid("El manifiesto de corpus intenta salir de su directorio.");
        }

        foreach (var segment in SplitPath(relative))
        {
            current = Path.Combine(current, segment);
            EnsureNotReparsePoint(current);
        }
    }

    private void EnsureNotReparsePoint(string path)
    {
        if ((_fileSystem.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Invalid("El manifiesto de corpus no permite enlaces o puntos de reparación.");
        }
    }

    private static Dictionary<string, JsonElement> GetClosedObject(
        JsonElement element,
        HashSet<string> allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("El manifiesto de corpus contiene metadatos mal formados.");
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name) || !properties.TryAdd(property.Name, property.Value))
            {
                throw Invalid("El manifiesto de corpus contiene metadatos desconocidos o duplicados.");
            }
        }

        if (properties.Count != allowedProperties.Count)
        {
            throw Invalid("El manifiesto de corpus omite metadatos obligatorios.");
        }

        return properties;
    }

    private static JsonElement RequireProperty(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name) =>
        properties.TryGetValue(name, out var value)
            ? value
            : throw Invalid("El manifiesto de corpus omite metadatos obligatorios.");

    private static int RequireInt32(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        var value = RequireProperty(properties, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw Invalid("El manifiesto de corpus contiene metadatos mal formados.");
        }

        return result;
    }

    private static string RequireString(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        var value = RequireProperty(properties, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid("El manifiesto de corpus contiene metadatos mal formados.");
        }

        return value.GetString()!;
    }

    private static string RequireSha256(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        var hash = RequireString(properties, name);
        if (!IsSha256(hash))
        {
            throw Invalid("El manifiesto de corpus contiene un SHA-256 no válido.");
        }

        return hash;
    }

    private static string RequireSource(IReadOnlyDictionary<string, JsonElement> properties)
    {
        var source = RequireString(properties, "source");
        if (source.Length > 2048
            || !Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw Invalid("El manifiesto de corpus contiene un origen no válido.");
        }

        return uri.AbsoluteUri;
    }

    private static string ResolveManifestPath(string manifestPath)
    {
        try
        {
            if (Path.GetInvalidPathChars().Any(character => manifestPath.Contains(character)))
            {
                throw Invalid("La ruta del manifiesto de corpus no es válida.");
            }

            return Path.GetFullPath(manifestPath);
        }
        catch (CorpusManifestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw Invalid("La ruta del manifiesto de corpus no es válida.", exception);
        }
    }

    private static string ResolveOwnedRelativePath(string manifestDirectory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.Contains(':')
            || ContainsTraversalSegment(relativePath)
            || SplitPath(relativePath).Length == 0)
        {
            throw Invalid("El manifiesto de corpus contiene una ruta de archivo no segura.");
        }

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(manifestDirectory, relativePath));
            var rootWithSeparator = EnsureTrailingDirectorySeparator(manifestDirectory);
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("El manifiesto de corpus intenta salir de su directorio.");
            }

            return fullPath;
        }
        catch (CorpusManifestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw Invalid("El manifiesto de corpus contiene una ruta de archivo no válida.", exception);
        }
    }

    private static string NormalizeDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string EnsureTrailingDirectorySeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static bool ContainsTraversalSegment(string path) =>
        SplitPath(path).Any(static segment => segment is "." or "..");

    private static string[] SplitPath(string path) =>
        path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);

    private static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 64
        && IsAsciiLetterOrDigit(id[0])
        && id.All(static character =>
            IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsSafeMetadata(string value) =>
        value.Length is > 0 and <= 128
        && value.All(static character =>
            IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or '+' or ' ');

    private static bool IsSha256(string hash) =>
        hash.Length == 64
        && hash.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9';

    private static CorpusManifestException Invalid(string message, Exception? innerException = null) =>
        new(message, innerException);

    internal sealed class CorpusManifestException : IOException
    {
        public CorpusManifestException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
