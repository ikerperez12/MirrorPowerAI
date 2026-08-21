using MirrorPowerAI.Benchmark;
using MirrorPowerAI.Core.Models;

namespace MirrorPowerAI.Benchmark.Tests;

public sealed class BenchmarkOutputTests
{
    [Fact]
    public void WriteResult_SmallModel_EmitsPinnedAliasOriginAndHash()
    {
        var result = new BenchmarkResult(
            TimeSpan.FromSeconds(1),
            BenchmarkModel.Small,
            WhisperModelDescriptor.DefaultSmall,
            TimeSpan.FromSeconds(2),
            "es",
            4,
            "transcripción sintética",
            TimeSpan.FromSeconds(0.2),
            0.2,
            null);
        using var output = new StringWriter();

        BenchmarkOutput.WriteResult(output, result);

        var text = output.ToString();
        Assert.Contains("small (ggml-small.bin)", text, StringComparison.Ordinal);
        Assert.Contains(
            "5359861c739e955e79d9a303bcbc70fb988958b1",
            text,
            StringComparison.Ordinal);
        Assert.Contains(WhisperModelDescriptor.DefaultSmall.Sha256, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Ruta local del modelo", text, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Transcript, text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteRunHeader_UsesFixedInputLabelInsteadOfAnAudioPath()
    {
        using var output = new StringWriter();

        BenchmarkCommand.WriteRunHeader(output, BenchmarkModel.Base);

        var text = output.ToString();
        Assert.Contains("Audio: <entrada WAV validada>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\PrivateUser\\Documents\\audio-sensible.wav", text, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteResult_Default_HidesTranscriptAndPreservesWerAndRtfMetrics()
    {
        var result = CreateResult(
            "transcripción privada que no debe aparecer",
            WordErrorRate.Calculate("hola mundo", "hola"));
        using var output = new StringWriter();

        BenchmarkOutput.WriteResult(output, result);

        var text = output.ToString();
        Assert.DoesNotContain(result.Transcript, text, StringComparison.Ordinal);
        Assert.Contains("Transcripción: <oculta por privacidad", text, StringComparison.Ordinal);
        Assert.Contains("RTF: 0.2000x", text, StringComparison.Ordinal);
        Assert.Contains("WER normalizado: 50.00%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteResult_ShowTranscript_EmitsRawTranscriptOnlyAfterExplicitOptIn()
    {
        var result = CreateResult("transcripción de depuración autorizada", null);
        using var output = new StringWriter();

        BenchmarkOutput.WriteResult(output, result, showTranscript: true);

        var text = output.ToString();
        Assert.Contains("Transcripción (solicitada explícitamente)", text, StringComparison.Ordinal);
        Assert.Contains(result.Transcript, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveReferenceAsync_MissingReferenceFile_DoesNotExposeItsAbsolutePath()
    {
        var referencePath = Path.Combine(
            Path.GetTempPath(),
            $"MirrorPowerAI-Benchmark-referencia-privada-{Guid.NewGuid():N}.txt");
        var options = new BenchmarkOptions(
            "unused.wav",
            "unused-model-directory",
            BenchmarkModel.Base,
            "es",
            1,
            null,
            referencePath,
            ShowTranscript: false);

        var exception = await Assert.ThrowsAsync<IOException>(
            () => BenchmarkCommand.ResolveReferenceAsync(options, CancellationToken.None));

        Assert.DoesNotContain(referencePath, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(referencePath), exception.Message, StringComparison.Ordinal);
        Assert.Contains("archivo de referencia", exception.Message, StringComparison.Ordinal);
    }

    private static BenchmarkResult CreateResult(string transcript, WordErrorRateResult? wordErrorRate) =>
        new(
            TimeSpan.FromSeconds(1),
            BenchmarkModel.Base,
            WhisperModelDescriptor.DefaultBase,
            TimeSpan.FromSeconds(2),
            "es",
            4,
            transcript,
            TimeSpan.FromSeconds(0.2),
            0.2,
            wordErrorRate);
}
