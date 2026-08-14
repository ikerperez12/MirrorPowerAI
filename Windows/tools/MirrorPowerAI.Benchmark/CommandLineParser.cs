using System.Globalization;

namespace MirrorPowerAI.Benchmark;

internal sealed record BenchmarkOptions(
    string AudioPath,
    string ModelDirectory,
    string Language,
    int ThreadCount,
    string? ReferenceText,
    string? ReferenceFilePath);

internal sealed record CommandLineParseResult(
    BenchmarkOptions? Options,
    string? Error,
    bool ShowHelp);

internal static class CommandLineParser
{
    private const int MaximumThreadCount = 32;

    public static string HelpText =>
        """
        MirrorPowerAI Whisper benchmark

        Uso:
          MirrorPowerAI.Benchmark --audio <archivo.wav> [opciones]

        Opciones:
          --audio <ruta>             WAV PCM normalizado: 16 kHz, mono, 16 bits (obligatorio).
          --reference <texto>        Referencia literal para calcular WER normalizado.
          --reference-file <ruta>    Archivo UTF-8 de referencia para calcular WER normalizado.
          --language <código|auto>   Idioma de Whisper; valor predeterminado: auto.
          --threads <1-32>           Hilos de inferencia; por defecto: mitad de CPU, máximo 8.
          --model-dir <ruta>         Directorio del modelo verificado. Por defecto:
                                     %LOCALAPPDATA%\MirrorPowerAI\models
          -h, --help, /?             Muestra esta ayuda.

        --reference y --reference-file son mutuamente excluyentes. La descarga o
        verificación del modelo se mide por separado y no forma parte del RTF.
        """;

    public static CommandLineParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string? audioPath = null;
        string? modelDirectory = null;
        string? language = null;
        string? referenceText = null;
        string? referenceFilePath = null;
        int? threadCount = null;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "-h" or "--help" or "/?")
            {
                return new CommandLineParseResult(null, null, ShowHelp: true);
            }

            if (argument is not (
                "--audio" or
                "--model-dir" or
                "--language" or
                "--threads" or
                "--reference" or
                "--reference-file"))
            {
                return Error($"Opción desconocida: {argument}");
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
                case "--model-dir":
                    modelDirectory = value;
                    break;
                case "--language":
                    language = value.Trim().ToLowerInvariant();
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
                    break;
                case "--reference":
                    referenceText = value;
                    break;
                case "--reference-file":
                    referenceFilePath = value;
                    break;
            }
        }

        if (audioPath is null)
        {
            return Error("Falta la opción obligatoria --audio.");
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
        threadCount ??= Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

        var options = new BenchmarkOptions(
            audioPath,
            modelDirectory,
            language,
            threadCount.Value,
            referenceText,
            referenceFilePath);
        return new CommandLineParseResult(options, null, ShowHelp: false);
    }

    private static bool IsValidLanguage(string language) =>
        language.Length is > 0 and <= 16
        && language.All(static character => character is >= 'a' and <= 'z' or '-');

    private static CommandLineParseResult Error(string message) =>
        new(null, message, ShowHelp: false);
}
