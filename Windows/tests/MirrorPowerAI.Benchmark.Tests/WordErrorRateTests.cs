using MirrorPowerAI.Benchmark;

namespace MirrorPowerAI.Benchmark.Tests;

public sealed class WordErrorRateTests
{
    [Fact]
    public void Calculate_CaseAccentsAndPunctuation_NormalizesToZeroErrors()
    {
        var result = WordErrorRate.Calculate(
            "¡Qué rápida está la transcripción!",
            "que rapida esta la TRANSCRIPCION");

        Assert.Equal(0, result.EditCount);
        Assert.Equal(5, result.ReferenceWordCount);
        Assert.Equal(5, result.HypothesisWordCount);
        Assert.Equal(0, result.Rate);
    }

    [Fact]
    public void Calculate_OneInsertion_ComputesExpectedRate()
    {
        var result = WordErrorRate.Calculate("hola mundo", "hola gran mundo");

        Assert.Equal(1, result.EditCount);
        Assert.Equal(2, result.ReferenceWordCount);
        Assert.Equal(3, result.HypothesisWordCount);
        Assert.Equal(0.5, result.Rate);
    }

    [Fact]
    public void Calculate_EmptyHypothesis_CountsEveryReferenceWordAsDeletion()
    {
        var result = WordErrorRate.Calculate("uno dos tres", string.Empty);

        Assert.Equal(3, result.EditCount);
        Assert.Equal(1, result.Rate);
    }

    [Fact]
    public void Calculate_ReferenceWithoutWords_RejectsUndefinedWer()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => WordErrorRate.Calculate("...", "hola"));

        Assert.Contains("al menos una palabra", exception.Message, StringComparison.Ordinal);
    }
}
