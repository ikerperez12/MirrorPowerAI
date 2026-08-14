using MirrorPowerAI.Core.Configuration;
using MirrorPowerAI.Core.Transcription;

namespace MirrorPowerAI.Core.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("es-ES")]
    [InlineData("en-US")]
    public void Validate_ValidLanguage_ReturnsNoErrors(string language)
    {
        var options = new MirrorPowerAIOptions { Language = language };

        var errors = options.Validate();

        Assert.Empty(errors);
    }

    [Fact]
    public void Defaults_Always_PreferLocalWhisperAndFiveMinuteLimit()
    {
        var options = new MirrorPowerAIOptions();

        Assert.Equal(TranscriptionProvider.LocalWhisper, options.Provider);
        Assert.Equal("es", options.Language);
        Assert.Equal(TimeSpan.FromSeconds(300), options.MaxCaptureDuration);
        Assert.Equal("gemini-3.5-flash", options.GeminiModel);
    }

    [Fact]
    public void EnsureValid_OverFiveMinutes_ThrowsWithoutIncludingValues()
    {
        var options = new MirrorPowerAIOptions
        {
            MaxCaptureDuration = TimeSpan.FromSeconds(301),
        };

        var exception = Assert.Throws<ConfigurationValidationException>(options.EnsureValid);

        Assert.Contains(exception.Errors, error =>
            error.PropertyName == nameof(MirrorPowerAIOptions.MaxCaptureDuration));
        Assert.DoesNotContain("301", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AutomaticLanguageDetection_IgnoresLanguageCode()
    {
        var options = new MirrorPowerAIOptions
        {
            AutomaticLanguageDetection = true,
            Language = string.Empty,
        };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Validate_MultipleInvalidValues_ReturnsPropertyScopedErrors()
    {
        var options = new MirrorPowerAIOptions
        {
            Provider = (TranscriptionProvider)99,
            Language = "not a language!",
            Context = new string('x', 65_537),
            OutputDeviceId = new string('d', 2_049),
            GeminiModel = "../unsafe/model",
            MaxCaptureDuration = TimeSpan.Zero,
        };

        var errors = options.Validate();

        Assert.Equal(6, errors.Count);
    }
}
