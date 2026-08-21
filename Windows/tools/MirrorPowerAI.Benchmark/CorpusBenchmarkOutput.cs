using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MirrorPowerAI.Benchmark;

/// <summary>
/// Renders aggregate corpus evidence only. Neither the JSON nor the console
/// summary contains per-item identifiers, local paths, corpus text or hashes
/// for individual audio/reference files.
/// </summary>
internal static class CorpusBenchmarkOutput
{
    private const int OutputSchemaVersion = 1;

    public static string Serialize(CorpusBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Indented = true,
                SkipValidation = false,
            }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", OutputSchemaVersion);
            writer.WriteString("kind", "mirrorpowerai-whisper-corpus-benchmark");
            writer.WriteBoolean("stable", result.Stable);
            writer.WriteStartObject("corpus");
            writer.WriteString("id", result.CorpusId);
            writer.WriteString("revision", result.CorpusRevision);
            writer.WriteString("license", result.CorpusLicense);
            writer.WriteString("source", result.CorpusSource);
            writer.WriteString("manifestSha256", result.ManifestSha256);
            writer.WriteEndObject();
            writer.WriteStartObject("configuration");
            writer.WriteString("model", BenchmarkCommand.ToOptionValue(result.Model));
            writer.WriteString("language", result.Language);
            writer.WriteNumber("threads", result.ThreadCount);
            writer.WriteEndObject();
            writer.WriteStartObject("aggregate");
            writer.WriteNumber("itemCount", result.ItemCount);
            writer.WriteNumber("audioSeconds", RoundSeconds(result.AudioDuration));
            writer.WriteNumber("modelVerificationSeconds", RoundSeconds(result.ModelVerificationElapsed));
            writer.WriteNumber("whisperSeconds", RoundSeconds(result.WhisperElapsed));
            writer.WriteNumber("editCount", result.EditCount);
            writer.WriteNumber("referenceWordCount", result.ReferenceWordCount);
            writer.WriteNumber("wordErrorRate", RoundRate(result.WordErrorRate));
            writer.WriteNumber("realTimeFactor", RoundRate(result.RealTimeFactor));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine;
    }

    public static void WriteSummary(TextWriter output, CorpusBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(result);

        output.WriteLine("MirrorPowerAI Whisper benchmark de corpus");
        output.WriteLine($"  Modo: {(result.Stable ? "estable sin red" : "corpus")}");
        output.WriteLine($"  Corpus: {result.CorpusId} revisión {result.CorpusRevision}");
        output.WriteLine($"  Licencia: {result.CorpusLicense}");
        output.WriteLine($"  Origen: {result.CorpusSource}");
        output.WriteLine($"  SHA-256 del manifiesto: {result.ManifestSha256}");
        output.WriteLine($"  Modelo: {BenchmarkCommand.ToOptionValue(result.Model)}");
        output.WriteLine($"  Idioma: {result.Language}");
        output.WriteLine($"  Hilos: {result.ThreadCount.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"  Elementos: {result.ItemCount.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"  Duración total: {FormatSeconds(result.AudioDuration)} s");
        output.WriteLine($"  Verificación de modelo: {FormatSeconds(result.ModelVerificationElapsed)} s");
        output.WriteLine($"  Whisper total: {FormatSeconds(result.WhisperElapsed)} s");
        output.WriteLine(
            $"  WER normalizado: {(result.WordErrorRate * 100).ToString("F2", CultureInfo.InvariantCulture)}% " +
            $"({result.EditCount.ToString(CultureInfo.InvariantCulture)} ediciones / " +
            $"{result.ReferenceWordCount.ToString(CultureInfo.InvariantCulture)} palabras de referencia)");
        output.WriteLine($"  RTF agregado: {result.RealTimeFactor.ToString("F4", CultureInfo.InvariantCulture)}x");
        output.WriteLine("  JSON: <resultado seguro escrito atómicamente>");
    }

    private static double RoundSeconds(TimeSpan value) =>
        Math.Round(value.TotalSeconds, 6, MidpointRounding.AwayFromZero);

    private static double RoundRate(double value) =>
        Math.Round(value, 8, MidpointRounding.AwayFromZero);

    private static string FormatSeconds(TimeSpan duration) =>
        RoundSeconds(duration).ToString("F6", CultureInfo.InvariantCulture);
}
