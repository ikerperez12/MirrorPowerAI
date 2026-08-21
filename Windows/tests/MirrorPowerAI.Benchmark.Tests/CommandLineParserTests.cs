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
        Assert.Equal(BenchmarkModel.Base, options.Model);
        Assert.Equal("es", options.Language);
        Assert.Equal(3, options.ThreadCount);
        Assert.Equal("hola mundo", options.ReferenceText);
        Assert.Null(options.ReferenceFilePath);
    }

    [Fact]
    public void Parse_ModelIsOmitted_UsesBaseByDefault()
    {
        var result = CommandLineParser.Parse(["--audio", "sample.wav"]);

        Assert.Null(result.Error);
        var options = Assert.IsType<BenchmarkOptions>(result.Options);
        Assert.Equal(BenchmarkModel.Base, options.Model);
    }

    [Theory]
    [InlineData("base", 0)]
    [InlineData("SMALL", 1)]
    public void Parse_SupportedModel_ReturnsPinnedModelSelection(
        string suppliedModel,
        int expectedModelValue)
    {
        var result = CommandLineParser.Parse(
            ["--audio", "sample.wav", "--model", suppliedModel]);

        Assert.Null(result.Error);
        var options = Assert.IsType<BenchmarkOptions>(result.Options);
        Assert.Equal((BenchmarkModel)expectedModelValue, options.Model);
    }

    [Theory]
    [InlineData("https://example.test/ggml-small.bin")]
    [InlineData("..\\ggml-small.bin")]
    [InlineData("medium")]
    public void Parse_UnsupportedModelReference_ReturnsClearError(string suppliedModel)
    {
        var result = CommandLineParser.Parse(
            ["--audio", "sample.wav", "--model", suppliedModel]);

        Assert.Null(result.Options);
        Assert.Contains("base' o 'small", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "ggml-base.bin")]
    [InlineData(1, "ggml-small.bin")]
    public void SelectDescriptor_AllowedModel_ReturnsOnlyPinnedDescriptor(
        int suppliedModelValue,
        string expectedFileName)
    {
        var descriptor = BenchmarkCommand.SelectDescriptor((BenchmarkModel)suppliedModelValue);

        Assert.Equal(expectedFileName, descriptor.FileName);
        Assert.Contains(
            "5359861c739e955e79d9a303bcbc70fb988958b1",
            descriptor.DownloadUri.AbsoluteUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SelectDescriptor_UnknownModel_RejectsBeforeModelManagerCanUseIt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BenchmarkCommand.SelectDescriptor((BenchmarkModel)999));
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
