using System.Globalization;

namespace MirrorPowerAI.Benchmark;

internal static class BenchmarkOutput
{
    public static void WriteResult(TextWriter output, BenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(result);

        output.WriteLine();
        output.WriteLine("Resultado");
        output.WriteLine($"  Modelo: {result.ModelPath}");
        output.WriteLine($"  Idioma: {result.Language}");
        output.WriteLine($"  Hilos: {result.ThreadCount.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"  Duración de audio: {FormatSeconds(result.AudioDuration)} s");
        output.WriteLine($"  Preparación de modelo: {FormatSeconds(result.ModelPreparationElapsed)} s");
        output.WriteLine($"  Whisper (carga + inferencia): {FormatSeconds(result.WhisperElapsed)} s");
        output.WriteLine($"  RTF: {result.RealTimeFactor.ToString("F4", CultureInfo.InvariantCulture)}x");

        if (result.WordErrorRate is { } wordErrorRate)
        {
            output.WriteLine(
                $"  WER normalizado: {(wordErrorRate.Rate * 100).ToString("F2", CultureInfo.InvariantCulture)}% " +
                $"({wordErrorRate.EditCount.ToString(CultureInfo.InvariantCulture)} ediciones / " +
                $"{wordErrorRate.ReferenceWordCount.ToString(CultureInfo.InvariantCulture)} palabras de referencia)");
            output.WriteLine(
                $"  Palabras de hipótesis: " +
                wordErrorRate.HypothesisWordCount.ToString(CultureInfo.InvariantCulture));
        }

        output.WriteLine();
        output.WriteLine("Transcripción");
        output.WriteLine(string.IsNullOrEmpty(result.Transcript) ? "  <vacía>" : result.Transcript);
    }

    private static string FormatSeconds(TimeSpan duration) =>
        duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
}
