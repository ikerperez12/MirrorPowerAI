using System.IO;
using System.Security.Cryptography;
using System.Text;
using Whisper.net;

namespace MirrorPowerAI.Windows.Transcription;

/// <summary>
/// Executes CPU-only local inference through Whisper.net 1.9.1.
/// </summary>
public sealed class WhisperNetInferenceEngine : IWhisperInferenceEngine
{
    /// <inheritdoc />
    public async Task<string> TranscribeAsync(
        string modelPath,
        ReadOnlyMemory<byte> wavData,
        string language,
        int threadCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var wavCopy = wavData.ToArray();
        try
        {
            using var factory = WhisperFactory.FromPath(modelPath);
            var builder = factory.CreateBuilder().WithThreads(threadCount);
            builder = string.Equals(language, "auto", StringComparison.Ordinal)
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(language);
            await using var processor = builder.Build();
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
}
