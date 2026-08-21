using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MirrorPowerAI.Core.Models;

namespace MirrorPowerAI.Benchmark;

internal sealed class ResolvedBenchmarkModel : IDisposable
{
    private FileStream? _cachedModelLock;

    public ResolvedBenchmarkModel(
        string modelPath,
        WhisperModelDescriptor descriptor,
        TimeSpan verificationElapsed,
        FileStream? cachedModelLock = null)
    {
        ModelPath = modelPath;
        Descriptor = descriptor;
        VerificationElapsed = verificationElapsed;
        _cachedModelLock = cachedModelLock;
    }

    public string ModelPath { get; }

    public WhisperModelDescriptor Descriptor { get; }

    public TimeSpan VerificationElapsed { get; }

    /// <summary>
    /// Retains a read-only share lock for a verified cached model until every
    /// corpus item finishes. Whisper opens a second read handle, while Windows
    /// denies write/delete replacement between hash verification and inference.
    /// </summary>
    public void Dispose()
    {
        _cachedModelLock?.Dispose();
        _cachedModelLock = null;
        GC.SuppressFinalize(this);
    }
}

internal interface IBenchmarkModelResolver
{
    Task<ResolvedBenchmarkModel> ResolveAsync(
        CorpusBenchmarkOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves only the two descriptors pinned in source. Cached mode deliberately
/// avoids constructing or using an HTTP client, making the stable corpus gate
/// fail closed when a verified local model is unavailable.
/// </summary>
internal sealed class PinnedBenchmarkModelResolver : IBenchmarkModelResolver
{
    /// <inheritdoc />
    public async Task<ResolvedBenchmarkModel> ResolveAsync(
        CorpusBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var descriptor = BenchmarkCommand.SelectDescriptor(options.Model);
        descriptor.EnsureValid();
        var stopwatch = Stopwatch.StartNew();
        string modelPath;
        FileStream? cachedModelLock = null;
        if (options.RequireCachedModel)
        {
            var cachedModel = await ResolveCachedAsync(
                    descriptor,
                    options.ModelDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            modelPath = cachedModel.Path;
            cachedModelLock = cachedModel.LockStream;
        }
        else
        {
            using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var modelManager = new WhisperModelManager(httpClient, descriptor);
            modelPath = await modelManager
                .EnsureAvailableAsync(options.ModelDirectory, cancellationToken)
                .ConfigureAwait(false);
        }

        stopwatch.Stop();
        return new ResolvedBenchmarkModel(
            modelPath,
            descriptor,
            stopwatch.Elapsed,
            cachedModelLock);
    }

    private static async Task<VerifiedCachedModel> ResolveCachedAsync(
        WhisperModelDescriptor descriptor,
        string modelDirectory,
        CancellationToken cancellationToken)
    {
        FileStream? stream = null;
        try
        {
            var directory = Path.GetFullPath(modelDirectory);
            var modelPath = Path.Combine(directory, descriptor.FileName);
            if (!Directory.Exists(directory)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                || !File.Exists(modelPath)
                || (File.GetAttributes(modelPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new CachedWhisperModelException(
                    "No hay un modelo Whisper local con el tamaño fijado.");
            }

            stream = new FileStream(
                modelPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            WindowsPathSafety.EnsureOpenFileIsUnderDirectory(stream, directory);
            if (stream.Length != descriptor.ExpectedSize)
            {
                throw new CachedWhisperModelException(
                    "No hay un modelo Whisper local con el tamaño fijado.");
            }

            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    Convert.ToHexStringLower(hash),
                    descriptor.Sha256,
                    StringComparison.Ordinal))
            {
                throw new CachedWhisperModelException(
                    "El modelo Whisper local no supera la verificación SHA-256 fijada.");
            }

            stream.Position = 0;
            var verifiedModel = new VerifiedCachedModel(modelPath, stream);
            stream = null;
            return verifiedModel;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or ArgumentException
            or NotSupportedException)
        {
            throw new CachedWhisperModelException(
                "No se pudo comprobar el modelo Whisper local requerido.",
                exception);
        }
        finally
        {
            // Cancellation and every failed validation path must release the
            // read/delete lock. Ownership transfers only to VerifiedCachedModel.
            stream?.Dispose();
        }
    }

    private sealed record VerifiedCachedModel(string Path, FileStream LockStream);
}

internal sealed class CachedWhisperModelException : Exception
{
    public CachedWhisperModelException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal interface ICorpusAssetPreflight
{
    Task ValidateAsync(CorpusManifest manifest, CancellationToken cancellationToken);
}

internal interface ICorpusItemExecutor
{
    Task<CorpusItemMetrics> ExecuteAsync(
        CorpusManifest manifest,
        CorpusManifestItem item,
        ResolvedBenchmarkModel model,
        CorpusBenchmarkOptions options,
        CancellationToken cancellationToken);
}

internal interface ICorpusWhisperRunner
{
    Task<WhisperRunResult> RunAsync(
        string modelPath,
        Stream normalizedWave,
        string language,
        int threadCount,
        CancellationToken cancellationToken);
}

internal sealed class DefaultCorpusWhisperRunner : ICorpusWhisperRunner
{
    /// <inheritdoc />
    public Task<WhisperRunResult> RunAsync(
        string modelPath,
        Stream normalizedWave,
        string language,
        int threadCount,
        CancellationToken cancellationToken) =>
        WhisperBenchmarkRunner.RunAsync(
            modelPath,
            normalizedWave,
            language,
            threadCount,
            cancellationToken);
}

internal readonly record struct CorpusItemMetrics(
    TimeSpan AudioDuration,
    TimeSpan WhisperElapsed,
    int EditCount,
    int ReferenceWordCount);

internal sealed class CorpusAssetValidator : ICorpusAssetPreflight
{
    private const long MaximumReferenceBytes = CorpusManifestLoader.MaximumReferenceBytes;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly ICorpusFileSystem _fileSystem;

    public CorpusAssetValidator(ICorpusFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new PhysicalCorpusFileSystem();
    }

    /// <inheritdoc />
    public async Task ValidateAsync(CorpusManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        foreach (var item in manifest.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = await OpenValidatedInputAsync(manifest, item, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal async Task<ValidatedCorpusInput> OpenValidatedInputAsync(
        CorpusManifest manifest,
        CorpusManifestItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureNoReparsePoints(manifest.DirectoryPath, item.AudioPath);
        EnsureNoReparsePoints(manifest.DirectoryPath, item.ReferencePath);

        NormalizedWaveFile? wave = null;
        try
        {
            wave = NormalizedWaveFile.Open(
                item.AudioPath,
                stream => WindowsPathSafety.EnsureOpenFileIsUnderDirectory(stream, manifest.DirectoryPath));
            await VerifyOpenStreamHashAsync(wave.Stream, item.AudioSha256, cancellationToken)
                .ConfigureAwait(false);
            var reference = await ReadValidatedReferenceAsync(
                    manifest.DirectoryPath,
                    item.ReferencePath,
                    item.ReferenceSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ValidatedCorpusInput(wave, reference);
        }
        catch
        {
            wave?.Dispose();
            throw;
        }
    }

    private static async Task VerifyOpenStreamHashAsync(
        FileStream stream,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexStringLower(hash), expectedSha256, StringComparison.Ordinal))
        {
            throw Invalid("Un archivo de corpus no coincide con el SHA-256 declarado.");
        }

        stream.Position = 0;
    }

    private static async Task<string> ReadValidatedReferenceAsync(
        string manifestDirectory,
        string referencePath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                referencePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            WindowsPathSafety.EnsureOpenFileIsUnderDirectory(
                stream,
                manifestDirectory);
            if (stream.Length > MaximumReferenceBytes)
            {
                throw Invalid("Un archivo de referencia del corpus supera el límite permitido.");
            }

            await VerifyOpenStreamHashAsync(stream, expectedSha256, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(
                stream,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 64 * 1024,
                leaveOpen: true);
            var reference = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (reference.Any(static character => char.IsControl(character)
                    && character is not '\r' and not '\n' and not '\t'))
            {
                throw Invalid("La referencia del corpus no está codificada como texto UTF-8 válido.");
            }

            _ = WordErrorRate.Calculate(reference, string.Empty);
            return reference;
        }
        catch (CorpusManifestLoader.CorpusManifestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or DecoderFallbackException)
        {
            throw Invalid("No se pudo validar una referencia del corpus.", exception);
        }
    }

    private void EnsureNoReparsePoints(string manifestDirectory, string targetPath)
    {
        var current = manifestDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(current, targetPath);
        if (Path.IsPathRooted(relative) || relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\'],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment is "." or ".."))
        {
            throw Invalid("El manifiesto de corpus intenta salir de su directorio.");
        }

        EnsureNotReparsePoint(current);
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNotReparsePoint(current);
        }
    }

    private void EnsureNotReparsePoint(string path)
    {
        try
        {
            if ((_fileSystem.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw Invalid("El manifiesto de corpus no permite enlaces o puntos de reparación.");
            }
        }
        catch (CorpusManifestLoader.CorpusManifestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw Invalid("No se pudo comprobar un archivo del corpus.", exception);
        }
    }

    private static CorpusManifestLoader.CorpusManifestException Invalid(
        string message,
        Exception? innerException = null) =>
        new(message, innerException);
}

internal sealed class ValidatedCorpusInput : IDisposable
{
    public ValidatedCorpusInput(NormalizedWaveFile wave, string reference)
    {
        Wave = wave ?? throw new ArgumentNullException(nameof(wave));
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
    }

    public NormalizedWaveFile Wave { get; }

    public string Reference { get; }

    public void Dispose()
    {
        Wave.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed class DefaultCorpusItemExecutor : ICorpusItemExecutor
{
    private readonly CorpusAssetValidator _assets;
    private readonly ICorpusWhisperRunner _runner;

    public DefaultCorpusItemExecutor(
        CorpusAssetValidator? assets = null,
        ICorpusWhisperRunner? runner = null)
    {
        _assets = assets ?? new CorpusAssetValidator();
        _runner = runner ?? new DefaultCorpusWhisperRunner();
    }

    /// <inheritdoc />
    public async Task<CorpusItemMetrics> ExecuteAsync(
        CorpusManifest manifest,
        CorpusManifestItem item,
        ResolvedBenchmarkModel model,
        CorpusBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        using var input = await _assets.OpenValidatedInputAsync(manifest, item, cancellationToken)
            .ConfigureAwait(false);
        var whisper = await _runner.RunAsync(
                model.ModelPath,
                input.Wave.Stream,
                options.Language,
                options.ThreadCount,
                cancellationToken)
            .ConfigureAwait(false);
        var wer = WordErrorRate.Calculate(input.Reference, whisper.Transcript);
        return new CorpusItemMetrics(
            input.Wave.Duration,
            whisper.Elapsed,
            wer.EditCount,
            wer.ReferenceWordCount);
    }
}
