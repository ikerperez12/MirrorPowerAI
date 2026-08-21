using System.Diagnostics;
using System.Text;
using MirrorPowerAI.Core.Models;

namespace MirrorPowerAI.Benchmark;

internal sealed record BenchmarkResult(
    TimeSpan AudioDuration,
    BenchmarkModel Model,
    WhisperModelDescriptor ModelDescriptor,
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

        WriteRunHeader(output, options.Model);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        var descriptor = SelectDescriptor(options.Model);
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var modelManager = new WhisperModelManager(
            httpClient,
            descriptor);
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
            wave.Duration,
            options.Model,
            descriptor,
            modelStopwatch.Elapsed,
            options.Language,
            options.ThreadCount,
            whisperResult.Transcript,
            whisperResult.Elapsed,
            realTimeFactor,
            wordErrorRate);
    }

    internal static void WriteRunHeader(TextWriter output, BenchmarkModel model)
    {
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine("MirrorPowerAI Whisper benchmark");
        output.WriteLine("Audio: <entrada WAV validada>");
        output.WriteLine($"Preparando y verificando el modelo fijado ({ToOptionValue(model)})...");
    }

    internal static WhisperModelDescriptor SelectDescriptor(BenchmarkModel model) => model switch
    {
        BenchmarkModel.Base => WhisperModelDescriptor.DefaultBase,
        BenchmarkModel.Small => WhisperModelDescriptor.DefaultSmall,
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    internal static string ToOptionValue(BenchmarkModel model) => model switch
    {
        BenchmarkModel.Base => "base",
        BenchmarkModel.Small => "small",
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    internal static async Task<string?> ResolveReferenceAsync(
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

        string referencePath;
        try
        {
            referencePath = Path.GetFullPath(options.ReferenceFilePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new ArgumentException(
                "La ruta del archivo de referencia no es válida.",
                nameof(options),
                exception);
        }

        try
        {
            var referenceFile = new FileInfo(referencePath);
            if (!referenceFile.Exists)
            {
                throw new FileNotFoundException("No existe el archivo de referencia.");
            }

            if (referenceFile.Length > MaximumReferenceFileBytes)
            {
                throw new InvalidDataException("El archivo de referencia supera el límite de 1 MiB.");
            }

            return await File.ReadAllTextAsync(referencePath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new UnauthorizedAccessException("No se pudo acceder al archivo de referencia.", exception);
        }
        catch (IOException exception)
        {
            throw new IOException("No se pudo leer el archivo de referencia.", exception);
        }
    }
}
