using System.IO;
using System.Security.Cryptography;
using System.Text;
using Whisper.net;

namespace MirrorPowerAI.Windows.Transcription;

/// <summary>
/// Executes CPU-only local inference through Whisper.net 1.9.1.
/// </summary>
public sealed class WhisperNetInferenceEngine : IWhisperInferenceEngine, IWhisperInferencePrewarmer, IDisposable
{
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private WhisperFactory? _factory;
    private ModelStamp? _factoryStamp;
    private int _disposed;

    /// <inheritdoc />
    public async Task PrepareAsync(
        string modelPath,
        int threadCount,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(modelPath, threadCount, cancellationToken);
        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _ = GetOrCreateFactory(modelPath);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> TranscribeAsync(
        string modelPath,
        ReadOnlyMemory<byte> wavData,
        string language,
        int threadCount,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(modelPath, threadCount, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var factory = GetOrCreateFactory(modelPath);
            var builder = factory.CreateBuilder().WithThreads(threadCount);
            builder = string.Equals(language, "auto", StringComparison.Ordinal)
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(language);
            await using var processor = builder.Build();
            var wavCopy = wavData.ToArray();
            try
            {
                using var stream = new MemoryStream(wavCopy, writable: false);
                var transcript = new StringBuilder();

                await foreach (var segment in processor
                    .ProcessAsync(stream, cancellationToken)
                    .ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    transcript.Append(segment.Text);
                }

                return transcript.ToString().Trim();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wavCopy);
            }
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    /// <summary>
    /// Releases the cached native model and the inference gate.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The gate serializes disposal with a running processor. Disposal is synchronous because
        // WhisperFactory exposes IDisposable, and this method is only called during application
        // shutdown after the session controller has already stopped capture.
        _inferenceGate.Wait();
        try
        {
            _factory?.Dispose();
            _factory = null;
            _factoryStamp = null;
        }
        finally
        {
            _inferenceGate.Release();
            _inferenceGate.Dispose();
        }
    }

    private WhisperFactory GetOrCreateFactory(string modelPath)
    {
        var fullPath = Path.GetFullPath(modelPath);
        var stamp = ReadModelStamp(fullPath);
        if (_factory is not null && _factoryStamp == stamp)
        {
            return _factory;
        }

        // Create the replacement before disposing the previous factory. If the model was
        // modified or replaced, a failed load must not destroy a still-usable cached factory.
        var replacement = WhisperFactory.FromPath(fullPath);
        var previous = _factory;
        _factory = replacement;
        _factoryStamp = stamp;
        previous?.Dispose();
        return replacement;
    }

    private static ModelStamp ReadModelStamp(string modelPath)
    {
        var information = new FileInfo(modelPath);
        if (!information.Exists)
        {
            throw new FileNotFoundException("No se encontró el modelo Whisper verificado.", modelPath);
        }

        return new ModelStamp(
            information.FullName,
            information.Length,
            information.LastWriteTimeUtc);
    }

    private static void ValidateArguments(
        string modelPath,
        int threadCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record ModelStamp(string FullPath, long Length, DateTime LastWriteTimeUtc);
}
