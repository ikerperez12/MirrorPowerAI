using System.Diagnostics;
using System.Text;
using Whisper.net;

namespace MirrorPowerAI.Benchmark;

internal sealed class WhisperBenchmarkException : Exception
{
    public WhisperBenchmarkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record WhisperRunResult(string Transcript, TimeSpan Elapsed);

internal static class WhisperBenchmarkRunner
{
    public static async Task<WhisperRunResult> RunAsync(
        string modelPath,
        Stream normalizedWave,
        string language,
        int threadCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(normalizedWave);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        cancellationToken.ThrowIfCancellationRequested();

        normalizedWave.Position = 0;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var factory = WhisperFactory.FromPath(modelPath);
            var builder = factory.CreateBuilder().WithThreads(threadCount);
            builder = string.Equals(language, "auto", StringComparison.Ordinal)
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(language);
            await using var processor = builder.Build();
            var transcript = new StringBuilder();

            await foreach (var segment in processor
                .ProcessAsync(normalizedWave, cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                transcript.Append(segment.Text);
            }

            stopwatch.Stop();
            return new WhisperRunResult(transcript.ToString().Trim(), stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new WhisperBenchmarkException(
                "La carga del modelo o la inferencia local han fallado.",
                exception);
        }
    }
}
