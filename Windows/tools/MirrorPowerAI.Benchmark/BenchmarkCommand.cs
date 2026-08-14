using System.Diagnostics;
using System.Text;
using MirrorPowerAI.Core.Models;

namespace MirrorPowerAI.Benchmark;

internal sealed record BenchmarkResult(
    string AudioPath,
    TimeSpan AudioDuration,
    string ModelPath,
    TimeSpan ModelPreparationElapsed,
    string Language,
    int ThreadCount,
    string Transcript,
    TimeSpan WhisperElapsed,
    double RealTimeFactor,
    WordErrorRateResult? WordErrorRate);

internal static class BenchmarkCommand
{
    private const long MaximumReferenceFileBytes = 1024 * 1024;

    public static async Task<BenchmarkResult> RunAsync(
        BenchmarkOptions options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();

        using var wave = NormalizedWaveFile.Open(options.AudioPath);
        var reference = await ResolveReferenceAsync(options, cancellationToken).ConfigureAwait(false);

        output.WriteLine("MirrorPowerAI Whisper benchmark");
        output.WriteLine($"Audio: {wave.Path}");
        output.WriteLine("Preparando y verificando el modelo fijado...");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var modelManager = new WhisperModelManager(
            httpClient,
            WhisperModelDescriptor.DefaultBase);
        var modelStopwatch = Stopwatch.StartNew();
        var modelPath = await modelManager
            .EnsureAvailableAsync(options.ModelDirectory, cancellationToken)
            .ConfigureAwait(false);
        modelStopwatch.Stop();

        output.WriteLine("Ejecutando Whisper local...");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        var whisperResult = await WhisperBenchmarkRunner
            .RunAsync(
                modelPath,
                wave.Stream,
                options.Language,
                options.ThreadCount,
                cancellationToken)
            .ConfigureAwait(false);

        var realTimeFactor = whisperResult.Elapsed.TotalSeconds / wave.Duration.TotalSeconds;
        WordErrorRateResult? wordErrorRate = reference is null
            ? null
            : MirrorPowerAI.Benchmark.WordErrorRate.Calculate(reference, whisperResult.Transcript);

        return new BenchmarkResult(
            wave.Path,
            wave.Duration,
            modelPath,
            modelStopwatch.Elapsed,
            options.Language,
            options.ThreadCount,
            whisperResult.Transcript,
            whisperResult.Elapsed,
            realTimeFactor,
            wordErrorRate);
    }

    private static async Task<string?> ResolveReferenceAsync(
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        if (options.ReferenceText is not null)
        {
            return options.ReferenceText;
        }

        if (options.ReferenceFilePath is null)
        {
            return null;
        }

        var referencePath = Path.GetFullPath(options.ReferenceFilePath);
        var referenceFile = new FileInfo(referencePath);
        if (!referenceFile.Exists)
        {
            throw new FileNotFoundException("No existe el archivo de referencia.", referencePath);
        }

        if (referenceFile.Length > MaximumReferenceFileBytes)
        {
            throw new InvalidDataException("El archivo de referencia supera el límite de 1 MiB.");
        }

        return await File.ReadAllTextAsync(referencePath, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }
}
