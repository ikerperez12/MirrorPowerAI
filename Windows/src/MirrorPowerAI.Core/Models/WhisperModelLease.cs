using System.IO;

namespace MirrorPowerAI.Core.Models;

/// <summary>
/// Retains a verified Whisper model file as read-only until its consumer has finished inference.
/// </summary>
/// <remarks>
/// The retained handle is intentionally opened with <see cref="FileShare.Read"/>. This permits
/// Whisper to open its own read handle while preventing a concurrent replacement or deletion of the
/// model after its size and SHA-256 have been verified.
/// </remarks>
public sealed class WhisperModelLease : IDisposable
{
    private FileStream? _lockStream;

    internal WhisperModelLease(string modelPath, FileStream lockStream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(lockStream);

        ModelPath = modelPath;
        _lockStream = lockStream;
    }

    /// <summary>
    /// Gets the full path of the model protected by this lease.
    /// </summary>
    public string ModelPath { get; }

    /// <summary>
    /// Releases the read-only model handle.
    /// </summary>
    public void Dispose()
    {
        var lockStream = Interlocked.Exchange(ref _lockStream, null);
        lockStream?.Dispose();
        GC.SuppressFinalize(this);
    }
}
