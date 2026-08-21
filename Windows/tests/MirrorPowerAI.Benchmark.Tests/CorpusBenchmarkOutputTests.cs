using System.Text.Json;
using MirrorPowerAI.Benchmark;

namespace MirrorPowerAI.Benchmark.Tests;

public sealed class CorpusBenchmarkOutputTests
{
    [Fact]
    public void Serialize_AggregateResult_IsDeterministicAndOmitsPrivateItemData()
    {
        var result = CreateResult();

        var first = CorpusBenchmarkOutput.Serialize(result);
        var second = CorpusBenchmarkOutput.Serialize(result);

        Assert.Equal(first, second);
        Assert.DoesNotContain("C:\\Users\\private\\audio.wav", first, StringComparison.Ordinal);
        Assert.DoesNotContain("item-private-01", first, StringComparison.Ordinal);
        Assert.DoesNotContain("referencia privada", first, StringComparison.Ordinal);
        Assert.DoesNotContain("transcripción privada", first, StringComparison.Ordinal);
        Assert.DoesNotContain("audioSha256", first, StringComparison.Ordinal);
        Assert.DoesNotContain("referenceSha256", first, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("qa-spanish", root.GetProperty("corpus").GetProperty("id").GetString());
        Assert.Equal(new string('a', 64), root.GetProperty("corpus").GetProperty("manifestSha256").GetString());
        Assert.Equal(0.02, root.GetProperty("aggregate").GetProperty("wordErrorRate").GetDouble(), precision: 10);
        Assert.Equal(0.19, root.GetProperty("aggregate").GetProperty("realTimeFactor").GetDouble(), precision: 10);
    }

    [Fact]
    public void WriteSummary_AggregateResult_OmitsPrivateItemDataAndPaths()
    {
        using var output = new StringWriter();

        CorpusBenchmarkOutput.WriteSummary(output, CreateResult());

        var text = output.ToString();
        Assert.Contains("qa-spanish revisión 2026.08", text, StringComparison.Ordinal);
        Assert.Contains("WER normalizado: 2.00%", text, StringComparison.Ordinal);
        Assert.Contains("RTF agregado: 0.1900x", text, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\private", text, StringComparison.Ordinal);
        Assert.DoesNotContain("item-private-01", text, StringComparison.Ordinal);
        Assert.DoesNotContain("transcripción privada", text, StringComparison.Ordinal);
        Assert.DoesNotContain("referencia privada", text, StringComparison.Ordinal);
    }

    private static CorpusBenchmarkResult CreateResult() =>
        new(
            "qa-spanish",
            "2026.08",
            "CC0-1.0",
            "https://example.test/dataset",
            new string('a', 64),
            BenchmarkModel.Base,
            "es",
            4,
            Stable: true,
            ItemCount: 2,
            AudioDuration: TimeSpan.FromSeconds(10),
            ModelVerificationElapsed: TimeSpan.FromMilliseconds(10),
            WhisperElapsed: TimeSpan.FromSeconds(1.9),
            EditCount: 2,
            ReferenceWordCount: 100);
}
