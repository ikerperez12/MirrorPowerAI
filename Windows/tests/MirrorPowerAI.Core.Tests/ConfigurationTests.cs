using MirrorPowerAI.Core.Configuration;
using MirrorPowerAI.Core.Gemini;
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
        Assert.Equal(GeminiClientOptions.DefaultModel, options.GeminiModel);
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

    [Theory]
    [InlineData("gemini-2.5-flash", true)]
    [InlineData("gemini_2.5-flash", true)]
    [InlineData("../unsafe-model", false)]
    [InlineData("https://example.test/model", false)]
    [InlineData("model with spaces", false)]
    public void Validate_GeminiModel_UsesGeminiClientIdentifierPolicy(string model, bool isValid)
    {
        // Arrange
        var options = new MirrorPowerAIOptions { GeminiModel = model };

        // Act
        var errors = options.Validate();

        // Assert
        Assert.Equal(isValid, GeminiClientOptions.IsValidModelIdentifier(model));
        Assert.Equal(
            !isValid,
            errors.Any(error => error.PropertyName == nameof(MirrorPowerAIOptions.GeminiModel)));
    }
}
