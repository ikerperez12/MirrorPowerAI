using MirrorPowerAI.Core.Models;

namespace MirrorPowerAI.Windows.Transcription;

/// <summary>
/// Represents a verified model handle that remains locked until local inference finishes.
/// </summary>
public interface IWhisperModelLease : IDisposable
{
    /// <summary>
    /// Gets the verified Whisper model path protected by the lease.
    /// </summary>
    string ModelPath { get; }
}

/// <summary>
/// Acquires a locally verified Whisper model lease.
/// </summary>
public interface IWhisperModelLeaseProvider
{
    /// <summary>
    /// Returns a verified model lease, downloading it atomically when necessary.
    /// </summary>
    /// <param name="modelDirectory">Application-owned model directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lease that prevents the verified model from being replaced during use.</returns>
    Task<IWhisperModelLease> AcquireVerifiedLeaseAsync(
        string modelDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts the supply-chain-verified Core model manager to the Windows transcription service.
/// </summary>
public sealed class WhisperModelLeaseProvider(WhisperModelManager modelManager)
    : IWhisperModelLeaseProvider
{
    private readonly WhisperModelManager _modelManager =
        modelManager ?? throw new ArgumentNullException(nameof(modelManager));

    /// <inheritdoc />
    public async Task<IWhisperModelLease> AcquireVerifiedLeaseAsync(
        string modelDirectory,
        CancellationToken cancellationToken = default) =>
        new WhisperModelLeaseAdapter(
            await _modelManager
                .AcquireVerifiedLeaseAsync(modelDirectory, cancellationToken)
                .ConfigureAwait(false));

    private sealed class WhisperModelLeaseAdapter(WhisperModelLease lease) : IWhisperModelLease
    {
        private WhisperModelLease? _lease = lease ?? throw new ArgumentNullException(nameof(lease));

        public string ModelPath => (_lease ?? throw new ObjectDisposedException(nameof(WhisperModelLeaseAdapter)))
            .ModelPath;

        public void Dispose()
        {
            var lease = Interlocked.Exchange(ref _lease, null);
            lease?.Dispose();
        }
    }
}

/// <summary>
/// Executes local Whisper inference without exposing Whisper.net types to orchestration code.
/// </summary>
public interface IWhisperInferenceEngine
{
    /// <summary>
    /// Transcribes one normalized in-memory WAVE payload.
    /// </summary>
    /// <param name="modelPath">Verified model path.</param>
    /// <param name="wavData">Normalized WAVE payload.</param>
    /// <param name="language">Whisper language code or <c>auto</c>.</param>
    /// <param name="threadCount">Maximum native inference threads.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Plain transcript text.</returns>
    Task<string> TranscribeAsync(
        string modelPath,
        ReadOnlyMemory<byte> wavData,
        string language,
        int threadCount,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability implemented by inference engines that can load the native Whisper model
/// before the first audio segment arrives.
/// </summary>
/// <remarks>
/// A preparation call is deliberately separate from <see cref="IWhisperInferenceEngine"/> so
/// deterministic test doubles and alternative engines do not have to pay an artificial warm-up
/// contract. Production Whisper.net uses this capability to keep the factory and native model
/// resident for the lifetime of the process.
/// </remarks>
public interface IWhisperInferencePrewarmer
{
    /// <summary>
    /// Loads the verified model/runtime and returns when the first inference can start.
    /// </summary>
    /// <param name="modelPath">Path to the verified model.</param>
    /// <param name="threadCount">Maximum native inference threads.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PrepareAsync(
        string modelPath,
        int threadCount,
        CancellationToken cancellationToken = default);
}
