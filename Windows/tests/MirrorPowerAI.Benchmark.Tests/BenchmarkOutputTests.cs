using MirrorPowerAI.Benchmark;
using MirrorPowerAI.Core.Models;

namespace MirrorPowerAI.Benchmark.Tests;

public sealed class BenchmarkOutputTests
{
    [Fact]
    public void WriteResult_SmallModel_EmitsPinnedAliasOriginAndHash()
    {
        var result = new BenchmarkResult(
            "audio-publico.wav",
            TimeSpan.FromSeconds(1),
            "C:\\models\\ggml-small.bin",
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
    }
}
