using MirrorPowerAI.Benchmark;

namespace MirrorPowerAI.Benchmark.Tests;

public sealed class CommandLineParserTests
{
    [Fact]
    public void Parse_AllExplicitOptions_ReturnsExpectedValues()
    {
        string[] arguments =
        [
            "--audio",
            "sample.wav",
            "--model-dir",
            "models",
            "--language",
            "ES",
            "--threads",
            "3",
            "--reference",
            "hola mundo",
        ];

        var result = CommandLineParser.Parse(arguments);

        Assert.Null(result.Error);
        Assert.False(result.ShowHelp);
        var options = Assert.IsType<BenchmarkOptions>(result.Options);
        Assert.Equal("sample.wav", options.AudioPath);
        Assert.Equal("models", options.ModelDirectory);
        Assert.Equal("es", options.Language);
        Assert.Equal(3, options.ThreadCount);
        Assert.Equal("hola mundo", options.ReferenceText);
        Assert.Null(options.ReferenceFilePath);
    }

    [Fact]
    public void Parse_BothReferenceForms_ReturnsClearError()
    {
        var result = CommandLineParser.Parse(
        [
            "--audio",
            "sample.wav",
            "--reference",
            "hola",
            "--reference-file",
            "reference.txt",
        ]);

        Assert.Null(result.Options);
        Assert.Contains("no se pueden usar a la vez", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("33")]
    [InlineData("not-a-number")]
    public void Parse_InvalidThreadCount_ReturnsClearError(string threadCount)
    {
        var result = CommandLineParser.Parse(
            ["--audio", "sample.wav", "--threads", threadCount]);

        Assert.Null(result.Options);
        Assert.Contains("entre 1 y 32", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Help_DoesNotRequireAudio()
    {
        var result = CommandLineParser.Parse(["--help"]);

        Assert.True(result.ShowHelp);
        Assert.Null(result.Error);
        Assert.Null(result.Options);
    }
}
