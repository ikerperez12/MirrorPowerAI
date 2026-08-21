using System.Text;

namespace MirrorPowerAI.Benchmark;

/// <summary>
/// Contains only aggregate values and the approved corpus metadata. It never
/// retains item identifiers, local paths, references or transcripts.
/// </summary>
internal sealed record CorpusBenchmarkResult(
    string CorpusId,
    string CorpusRevision,
    string CorpusLicense,
    string CorpusSource,
    string ManifestSha256,
    BenchmarkModel Model,
    string Language,
    int ThreadCount,
    bool Stable,
    int ItemCount,
    TimeSpan AudioDuration,
    TimeSpan ModelVerificationElapsed,
    TimeSpan WhisperElapsed,
    int EditCount,
    int ReferenceWordCount)
{
    public double WordErrorRate => EditCount / (double)ReferenceWordCount;

    public double RealTimeFactor => WhisperElapsed.TotalSeconds / AudioDuration.TotalSeconds;
}

internal interface ICorpusResultWriter
{
    Task WriteAsync(
        string outputJsonPath,
        CorpusBenchmarkResult result,
        CancellationToken cancellationToken);
}

/// <summary>
/// Coordinates preflight, verified model resolution and item execution. The
/// result writer is invoked only after every item succeeds, so a failed corpus
/// run cannot leave a partial success report behind.
/// </summary>
internal sealed class CorpusBenchmarkCommand
{
    private readonly ICorpusManifestLoader _manifestLoader;
    private readonly ICorpusAssetPreflight _assetPreflight;
    private readonly IBenchmarkModelResolver _modelResolver;
    private readonly ICorpusItemExecutor _itemExecutor;
    private readonly ICorpusResultWriter _resultWriter;

    public CorpusBenchmarkCommand(
        ICorpusManifestLoader? manifestLoader = null,
        ICorpusAssetPreflight? assetPreflight = null,
        IBenchmarkModelResolver? modelResolver = null,
        ICorpusItemExecutor? itemExecutor = null,
        ICorpusResultWriter? resultWriter = null)
    {
        _manifestLoader = manifestLoader ?? new CorpusManifestLoader();
        _assetPreflight = assetPreflight ?? new CorpusAssetValidator();
        _modelResolver = modelResolver ?? new PinnedBenchmarkModelResolver();
        _itemExecutor = itemExecutor ?? new DefaultCorpusItemExecutor();
        _resultWriter = resultWriter ?? new AtomicCorpusResultWriter();
    }

    /// <summary>
    /// Runs a corpus only after every asset passes its hash/format/reference
    /// preflight. No output is produced from this method.
    /// </summary>
    public async Task<CorpusBenchmarkResult> RunAsync(
        CorpusBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        var manifest = await _manifestLoader
            .LoadAsync(options.ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        EnsureOutputDoesNotReplaceManifest(options.OutputJsonPath, options.ManifestPath);
        await _assetPreflight.ValidateAsync(manifest, cancellationToken).ConfigureAwait(false);

        using var model = await _modelResolver.ResolveAsync(options, cancellationToken).ConfigureAwait(false);
        long audioTicks = 0;
        long whisperTicks = 0;
        var editCount = 0;
        var referenceWordCount = 0;

        foreach (var item in manifest.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metric = await _itemExecutor
                .ExecuteAsync(manifest, item, model, options, cancellationToken)
                .ConfigureAwait(false);
            if (metric.AudioDuration <= TimeSpan.Zero
                || metric.WhisperElapsed < TimeSpan.Zero
                || metric.EditCount < 0
                || metric.ReferenceWordCount <= 0)
            {
                throw new InvalidDataException("El benchmark de corpus recibió una métrica no válida.");
            }

            try
            {
                audioTicks = checked(audioTicks + metric.AudioDuration.Ticks);
                whisperTicks = checked(whisperTicks + metric.WhisperElapsed.Ticks);
                editCount = checked(editCount + metric.EditCount);
                referenceWordCount = checked(referenceWordCount + metric.ReferenceWordCount);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("Las métricas agregadas del corpus no son válidas.", exception);
            }
        }

        if (audioTicks <= 0 || referenceWordCount <= 0)
        {
            throw new InvalidDataException("El benchmark de corpus no produjo métricas agregadas válidas.");
        }

        return new CorpusBenchmarkResult(
            manifest.Id,
            manifest.Revision,
            manifest.License,
            manifest.Source,
            manifest.ManifestSha256,
            options.Model,
            options.Language,
            options.ThreadCount,
            options.Stable,
            manifest.Items.Count,
            TimeSpan.FromTicks(audioTicks),
            model.VerificationElapsed,
            TimeSpan.FromTicks(whisperTicks),
            editCount,
            referenceWordCount);
    }

    /// <summary>
    /// Writes a result atomically only after <see cref="RunAsync"/> has
    /// completed. The caller may then render the safe console summary.
    /// </summary>
    public async Task<CorpusBenchmarkResult> RunAndWriteAsync(
        CorpusBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(options, cancellationToken).ConfigureAwait(false);
        await _resultWriter
            .WriteAsync(options.OutputJsonPath, result, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private static void ValidateOptions(CorpusBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Language);
        if (!Enum.IsDefined(options.Model))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "El modelo de corpus no está permitido.");
        }

        if (options.ThreadCount is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "El número de hilos de corpus no es válido.");
        }

        if (options.Stable
            && (!options.RequireCachedModel
                || !string.Equals(options.Language, "es", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "El modo estable requiere español y un modelo local verificado.",
                nameof(options));
        }
    }

    private static void EnsureOutputDoesNotReplaceManifest(string outputJsonPath, string manifestPath)
    {
        try
        {
            if (string.Equals(
                    Path.GetFullPath(outputJsonPath),
                    Path.GetFullPath(manifestPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "La salida JSON del benchmark debe ser distinta del manifiesto.",
                    nameof(outputJsonPath));
            }
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException)
        {
            throw new ArgumentException(
                "La salida JSON del benchmark no es válida.",
                nameof(outputJsonPath),
                exception);
        }
    }
}

/// <summary>
/// Writes the deterministic JSON through a same-directory temporary file. A
/// failure before the replacement leaves an existing report untouched.
/// </summary>
internal sealed class AtomicCorpusResultWriter : ICorpusResultWriter
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc />
    public async Task WriteAsync(
        string outputJsonPath,
        CorpusBenchmarkResult result,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputJsonPath);
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        var targetPath = ResolveTargetPath(outputJsonPath);
        var directory = Path.GetDirectoryName(targetPath)!;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        var payload = Utf8WithoutBom.GetBytes(CorpusBenchmarkOutput.Serialize(result));

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new IOException("No se pudo escribir el resultado seguro del benchmark de corpus.", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string ResolveTargetPath(string outputJsonPath)
    {
        try
        {
            if (Path.GetInvalidPathChars().Any(character => outputJsonPath.Contains(character)))
            {
                throw new ArgumentException("La salida JSON del benchmark no es válida.", nameof(outputJsonPath));
            }

            var targetPath = Path.GetFullPath(outputJsonPath);
            var directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory)
                || !Directory.Exists(directory)
                || !string.Equals(Path.GetExtension(targetPath), ".json", StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                || (File.Exists(targetPath)
                    && (File.GetAttributes(targetPath) & FileAttributes.ReparsePoint) != 0))
            {
                throw new ArgumentException("La salida JSON del benchmark no es válida.", nameof(outputJsonPath));
            }

            return targetPath;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or NotSupportedException)
        {
            throw new ArgumentException(
                "La salida JSON del benchmark no es válida.",
                nameof(outputJsonPath),
                exception);
        }
    }
}
