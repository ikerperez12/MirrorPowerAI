using MirrorPowerAI.Core.Privacy;
using MirrorPowerAI.Core.Security;

namespace MirrorPowerAI.Core.Tests;

public sealed class ConsentAndRedactionTests
{
    [Fact]
    public void Grant_Always_CreatesCurrentVersionConsent()
    {
        var timestamp = DateTimeOffset.Parse(
            "2026-08-14T10:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        var consent = GeminiAudioConsentPolicy.Grant(timestamp);

        Assert.Equal(GeminiAudioConsentPolicy.CurrentVersion, consent.Version);
        Assert.Equal(timestamp, consent.GrantedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void IsValid_WrongVersion_ReturnsFalse(int version)
    {
        var consent = new GeminiAudioConsent(version, DateTimeOffset.UtcNow);

        Assert.False(GeminiAudioConsentPolicy.IsValid(consent));
    }

    [Fact]
    public void IsValid_CurrentRecentConsent_ReturnsTrue()
    {
        Assert.True(GeminiAudioConsentPolicy.IsValid(GeminiAudioConsentPolicy.Grant()));
    }

    [Fact]
    public void Redact_KnownSensitiveValues_RemovesEveryOccurrence()
    {
        const string apiKey = "AIza-secret";
        const string transcript = "pregunta privada";
        var diagnostic = $"key={apiKey}; transcript={transcript}; repeated={apiKey}";

        var redacted = SensitiveDataRedactor.Redact(diagnostic, apiKey, transcript, string.Empty, null);

        Assert.DoesNotContain(apiKey, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(transcript, redacted, StringComparison.Ordinal);
        Assert.Equal(3, redacted.Split(SensitiveDataRedactor.RedactionMarker).Length - 1);
    }
}
