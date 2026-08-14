using System.Buffers;
using System.Security.Cryptography;

namespace MirrorPowerAI.Core.Models;

/// <summary>
/// Downloads and activates a pinned Whisper model only after exact size and SHA-256 verification.
/// </summary>
public sealed class WhisperModelManager : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly WhisperModelDescriptor _descriptor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="WhisperModelManager"/> class.
    /// </summary>
    /// <param name="httpClient">A reusable HTTP client owned by the application.</param>
    /// <param name="descriptor">The pinned model descriptor.</param>
    public WhisperModelManager(HttpClient httpClient, WhisperModelDescriptor descriptor)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _descriptor.EnsureValid();
    }

    /// <summary>
    /// Reuses a verified model or downloads it to a same-volume temporary file and atomically activates it.
    /// </summary>
    /// <param name="modelDirectory">The application-owned model directory.</param>
    /// <param name="cancellationToken">A token used to cancel verification or download.</param>
    /// <returns>The full path of the verified model.</returns>
    public async Task<string> EnsureAvailableAsync(
        string modelDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        var directory = Path.GetFullPath(modelDirectory);
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, _descriptor.FileName);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsValidAsync(targetPath, cancellationToken).ConfigureAwait(false))
            {
                return targetPath;
            }

            var temporaryPath = Path.Combine(
                directory,
                $".{_descriptor.FileName}.{Guid.NewGuid():N}.download");

            try
            {
                await DownloadAndVerifyAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, targetPath, overwrite: true);
                return targetPath;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Checks whether a model file exactly matches the pinned descriptor.
    /// </summary>
    /// <param name="path">The model file path.</param>
    /// <param name="cancellationToken">A token used to cancel hashing.</param>
    /// <returns><see langword="true"/> only for an exact size and SHA-256 match.</returns>
    public async Task<bool> IsValidAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists || file.Length != _descriptor.ExpectedSize)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash), _descriptor.Sha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// Releases the manager's synchronization resource. The injected HTTP client remains caller-owned.
    /// </summary>
    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task DownloadAndVerifyAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _descriptor.DownloadUri);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new WhisperModelException(
                WhisperModelErrorKind.DownloadFailed,
                "No se pudo descargar el modelo Whisper desde el origen fijado.");
        }

        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength != _descriptor.ExpectedSize)
        {
            throw new WhisperModelException(
                WhisperModelErrorKind.SizeMismatch,
                "El tamaño anunciado del modelo Whisper no coincide con el valor fijado.");
        }

        await using var input = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long totalBytes = 0;

        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
                if (totalBytes > _descriptor.ExpectedSize)
                {
                    throw new WhisperModelException(
                        WhisperModelErrorKind.SizeMismatch,
                        "El modelo Whisper descargado supera el tamaño fijado.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        if (totalBytes != _descriptor.ExpectedSize)
        {
            throw new WhisperModelException(
                WhisperModelErrorKind.SizeMismatch,
                "El tamaño del modelo Whisper descargado no coincide con el valor fijado.");
        }

        var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actualHash, _descriptor.Sha256, StringComparison.Ordinal))
        {
            throw new WhisperModelException(
                WhisperModelErrorKind.HashMismatch,
                "El SHA-256 del modelo Whisper descargado no coincide con el valor fijado.");
        }
    }
}
