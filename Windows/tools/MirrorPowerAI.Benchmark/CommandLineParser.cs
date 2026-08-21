using System.Globalization;

namespace MirrorPowerAI.Benchmark;

internal sealed record BenchmarkOptions(
    string AudioPath,
    string ModelDirectory,
    BenchmarkModel Model,
    string Language,
    int ThreadCount,
    string? ReferenceText,
    string? ReferenceFilePath,
    bool ShowTranscript);

/// <summary>
/// Describes an all-or-nothing corpus benchmark invocation. The corpus mode is
/// deliberately distinct from the one-file diagnostic command so that stable
/// measurements cannot silently inherit non-reproducible defaults.
/// </summary>
internal sealed record CorpusBenchmarkOptions(
    string ManifestPath,
    string OutputJsonPath,
    string ModelDirectory,
    BenchmarkModel Model,
    string Language,
    int ThreadCount,
    bool RequireCachedModel,
    bool Stable);

internal enum BenchmarkModel
{
    Base,
    Small,
}

internal sealed record CommandLineParseResult(
    BenchmarkOptions? Options,
    CorpusBenchmarkOptions? CorpusOptions,
    string? Error,
    bool ShowHelp);

internal static class CommandLineParser
{
    private const int MaximumThreadCount = 32;

    public static string HelpText =>
        """
        MirrorPowerAI Whisper benchmark

        Uso individual:
          MirrorPowerAI.Benchmark --audio <archivo.wav> [opciones]

        Uso de corpus reproducible:
          MirrorPowerAI.Benchmark --corpus-manifest <corpus.json> --output-json <resultado.json>
            --model <base|small> --language <código> --threads <1-32> [--stable]

        Opciones de entrada individual:
          --audio <ruta>             WAV PCM normalizado: 16 kHz, mono, 16 bits (obligatorio).
          --reference <texto>        Referencia literal para calcular WER normalizado.
          --reference-file <ruta>    Archivo UTF-8 de referencia para calcular WER normalizado.
          --show-transcript          Muestra la transcripción completa. Puede contener datos sensibles.

        Opciones de corpus:
          --corpus-manifest <ruta>   Manifiesto JSON local v1 con WAV y referencias hasheados.
          --output-json <ruta>       Resultado JSON seguro, escrito atómicamente tras éxito completo.
          --stable                   Exige idioma es y un modelo local verificado; no usa red.
          --require-cached-model     No descarga el modelo si falta o no pasa tamaño/SHA-256.

        Opciones compartidas:
          --language <código|auto>   Idioma de Whisper; individual: auto por defecto.
          --model <base|small>       Modelo fijado para medir; individual: base por defecto.
          --threads <1-32>           Hilos de inferencia; individual: mitad de CPU, máximo 8.
          --model-dir <ruta>         Directorio del modelo verificado. Por defecto:
                                     %LOCALAPPDATA%\MirrorPowerAI\models
          -h, --help, /?             Muestra esta ayuda.

        --reference y --reference-file son mutuamente excluyentes. En corpus,
        --model, --language, --threads y --output-json son obligatorios. Por
        privacidad, la salida normal no muestra la ruta del WAV ni la transcripción.
        """;

    public static CommandLineParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string? audioPath = null;
        string? corpusManifestPath = null;
        string? outputJsonPath = null;
        string? modelDirectory = null;
        BenchmarkModel? model = null;
        string? language = null;
        string? referenceText = null;
        string? referenceFilePath = null;
        int? threadCount = null;
        var showTranscript = false;
        var requireCachedModel = false;
        var stable = false;
        var modelWasSpecified = false;
        var languageWasSpecified = false;
        var threadCountWasSpecified = false;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "-h" or "--help" or "/?")
            {
                return new CommandLineParseResult(null, null, null, ShowHelp: true);
            }

            if (argument is "--show-transcript" or "--require-cached-model" or "--stable")
            {
                if (!seenOptions.Add(argument))
                {
                    return Error($"La opción {argument} no se puede repetir.");
                }

                switch (argument)
                {
                    case "--show-transcript":
                        showTranscript = true;
                        break;
                    case "--require-cached-model":
                        requireCachedModel = true;
                        break;
                    default:
                        stable = true;
                        break;
                }

                continue;
            }

            if (argument is not (
                "--audio" or
                "--corpus-manifest" or
                "--output-json" or
                "--model-dir" or
                "--model" or
                "--language" or
                "--threads" or
                "--reference" or
                "--reference-file"))
            {
                return Error("Opción desconocida. Usa --help para ver las opciones disponibles.");
            }

            if (!seenOptions.Add(argument))
            {
                return Error($"La opción {argument} no se puede repetir.");
            }

            if (index + 1 >= arguments.Count)
            {
                return Error($"Falta el valor de {argument}.");
            }

            var value = arguments[++index];
            if (string.IsNullOrWhiteSpace(value))
            {
                return Error($"El valor de {argument} no puede estar vacío.");
            }

            switch (argument)
            {
                case "--audio":
                    audioPath = value;
                    break;
                case "--corpus-manifest":
                    corpusManifestPath = value;
                    break;
                case "--output-json":
                    outputJsonPath = value;
                    break;
                case "--model-dir":
                    modelDirectory = value;
                    break;
                case "--model":
                    model = value.Trim().ToLowerInvariant() switch
                    {
                        "base" => BenchmarkModel.Base,
                        "small" => BenchmarkModel.Small,
                        _ => null,
                    };
                    if (model is null)
                    {
                        return Error("--model debe ser 'base' o 'small'.");
                    }

                    modelWasSpecified = true;
                    break;
                case "--language":
                    language = value.Trim().ToLowerInvariant();
                    languageWasSpecified = true;
                    break;
                case "--threads":
                    if (!int.TryParse(
                            value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var parsedThreadCount)
                        || parsedThreadCount is < 1 or > MaximumThreadCount)
                    {
                        return Error("--threads debe ser un entero entre 1 y 32.");
                    }

                    threadCount = parsedThreadCount;
                    threadCountWasSpecified = true;
                    break;
                case "--reference":
                    referenceText = value;
                    break;
                case "--reference-file":
                    referenceFilePath = value;
                    break;
            }
        }

        if (audioPath is not null && corpusManifestPath is not null)
        {
            return Error("--audio y --corpus-manifest no se pueden usar a la vez.");
        }

        if (audioPath is null && corpusManifestPath is null)
        {
            return Error("Falta --audio o --corpus-manifest.");
        }

        if (referenceText is not null && referenceFilePath is not null)
        {
            return Error("--reference y --reference-file no se pueden usar a la vez.");
        }

        language ??= "auto";
        if (!IsValidLanguage(language))
        {
            return Error("--language debe ser 'auto' o un código corto formado por letras y guiones.");
        }

        modelDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MirrorPowerAI",
            "models");
        model ??= BenchmarkModel.Base;
        threadCount ??= Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

        if (corpusManifestPath is not null)
        {
            if (showTranscript || referenceText is not null || referenceFilePath is not null)
            {
                return Error("Las opciones de referencia y --show-transcript sólo se permiten con --audio.");
            }

            if (outputJsonPath is null)
            {
                return Error("Falta --output-json para el benchmark de corpus.");
            }

            if (!modelWasSpecified || !languageWasSpecified || !threadCountWasSpecified)
            {
                return Error("El benchmark de corpus requiere --model, --language y --threads explícitos.");
            }

            if (stable && !string.Equals(language, "es", StringComparison.Ordinal))
            {
                return Error("--stable requiere --language es.");
            }

            return new CommandLineParseResult(
                null,
                new CorpusBenchmarkOptions(
                    corpusManifestPath,
                    outputJsonPath,
                    modelDirectory,
                    model.Value,
                    language,
                    threadCount.Value,
                    RequireCachedModel: requireCachedModel || stable,
                    Stable: stable),
                null,
                ShowHelp: false);
        }

        if (outputJsonPath is not null || requireCachedModel || stable)
        {
            return Error("--output-json, --require-cached-model y --stable sólo se permiten con --corpus-manifest.");
        }

        var options = new BenchmarkOptions(
            audioPath!,
            modelDirectory,
            model.Value,
            language,
            threadCount.Value,
            referenceText,
            referenceFilePath,
            showTranscript);
        return new CommandLineParseResult(options, null, null, ShowHelp: false);
    }

    private static bool IsValidLanguage(string language) =>
        language.Length is > 0 and <= 16
        && language.All(static character => character is >= 'a' and <= 'z' or '-');

    private static CommandLineParseResult Error(string message) =>
        new(null, null, message, ShowHelp: false);
}
