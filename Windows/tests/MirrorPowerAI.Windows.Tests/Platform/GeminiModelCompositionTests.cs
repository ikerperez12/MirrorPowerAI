using System.Net;
using MirrorPowerAI.Core.Gemini;
using MirrorPowerAI.Windows.Platform;

namespace MirrorPowerAI.Windows.Tests.Platform;

public sealed class GeminiModelCompositionTests
{
    private const string SuccessfulResponse =
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Respuesta breve\"}]},\"finishReason\":\"STOP\"}]}";

    [Fact]
    public async Task CreateGeminiClientOptions_ValidPersistedModel_ReachesConfiguredEndpointPath()
    {
        // Arrange
        var options = App.CreateGeminiClientOptions(new AppSettings { GeminiModel = "gemini-2.5-flash" });
        using var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GeminiClient(httpClient, new StaticApiKeyProvider(), options);

        // Act
        _ = await client.GenerateAnswerAsync("pregunta", null);

        // Assert
        Assert.Equal("gemini-2.5-flash", options.Model);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
            handler.RequestUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData("../unsafe-model")]
    [InlineData("https://example.test/model")]
    [InlineData("model with spaces")]
    public void CreateGeminiClientOptions_InvalidPersistedModel_FallsBackToDefault(string model)
    {
        // Act
        var options = App.CreateGeminiClientOptions(new AppSettings { GeminiModel = model });

        // Assert
        Assert.Equal(GeminiClientOptions.DefaultModel, options.Model);
        options.EnsureValid();
    }

    private sealed class StaticApiKeyProvider : IGeminiApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>("test-api-key");
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessfulResponse),
            });
        }
    }
}
