using MirrorPowerAI.Benchmark;

namespace MirrorPowerAI.Benchmark.Tests;

public sealed class CorpusCommandLineParserTests
{
    [Fact]
    public void Parse_CorpusStableWithExplicitSettings_RequiresCachedSpanishModel()
    {
        var result = CommandLineParser.Parse(
        [
            "--corpus-manifest",
            "corpus.json",
            "--output-json",
            "result.json",
            "--model",
            "base",
            "--language",
            "es",
            "--threads",
            "8",
            "--stable",
        ]);

        Assert.Null(result.Error);
        Assert.Null(result.Options);
        var options = Assert.IsType<CorpusBenchmarkOptions>(result.CorpusOptions);
        Assert.Equal(BenchmarkModel.Base, options.Model);
        Assert.Equal("es", options.Language);
        Assert.Equal(8, options.ThreadCount);
        Assert.True(options.Stable);
        Assert.True(options.RequireCachedModel);
    }

    [Theory]
    [InlineData("--model", "base")]
    [InlineData("--language", "es")]
    [InlineData("--threads", "4")]
    public void Parse_CorpusMissingExplicitReproducibilityOption_RejectsInvocation(
        string omittedOption,
        string omittedValue)
    {
        var arguments = new List<string>
        {
            "--corpus-manifest",
            "corpus.json",
            "--output-json",
            "result.json",
            "--model",
            "base",
            "--language",
            "es",
            "--threads",
            "4",
        };
        arguments.Remove(omittedOption);
        arguments.Remove(omittedValue);

        var result = CommandLineParser.Parse(arguments);

        Assert.Null(result.Options);
        Assert.Null(result.CorpusOptions);
        Assert.Contains("requiere --model, --language y --threads", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CorpusStableWithNonSpanishLanguage_RejectsInvocation()
    {
        var result = CommandLineParser.Parse(
        [
            "--corpus-manifest",
            "corpus.json",
            "--output-json",
            "result.json",
            "--model",
            "base",
            "--language",
            "en",
            "--threads",
            "4",
            "--stable",
        ]);

        Assert.Null(result.CorpusOptions);
        Assert.Contains("--language es", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CorpusCannotEnableTranscriptOrSingleFileReference()
    {
        var result = CommandLineParser.Parse(
        [
            "--corpus-manifest",
            "corpus.json",
            "--output-json",
            "result.json",
            "--model",
            "base",
            "--language",
            "es",
            "--threads",
            "4",
            "--show-transcript",
        ]);

        Assert.Null(result.CorpusOptions);
        Assert.Contains("sólo se permiten con --audio", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_IndividualBenchmark_RemainsCompatibleWithoutCorpusFields()
    {
        var result = CommandLineParser.Parse(["--audio", "sample.wav"]);

        Assert.Null(result.Error);
        Assert.Null(result.CorpusOptions);
        Assert.IsType<BenchmarkOptions>(result.Options);
    }
}
