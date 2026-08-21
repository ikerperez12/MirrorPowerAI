using System.Net;
using System.Text.Json;
using MirrorPowerAI.Core.Gemini;
using MirrorPowerAI.Core.Privacy;

namespace MirrorPowerAI.Core.Tests;

public sealed class GeminiClientTests
{
    private const string SuccessfulResponse =
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Respuesta breve\"}]},\"finishReason\":\"STOP\"}]}";

    [Fact]
    public async Task GenerateAnswerAsync_ValidInput_PreservesUpstreamPayloadAndHeader()
    {
        using var handler = RecordingHttpMessageHandler.Json(SuccessfulResponse);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.GenerateAnswerAsync("  ¿Qué hace?  ", "  contexto privado  ");

        Assert.Equal("Respuesta breve", result);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("test-api-key", handler.ApiKey);
        Assert.Equal(
            $"https://generativelanguage.googleapis.com/v1beta/models/{GeminiClientOptions.DefaultModel}:generateContent",
            handler.RequestUri?.AbsoluteUri);
        Assert.DoesNotContain("test-api-key", handler.RequestUri?.AbsoluteUri ?? string.Empty, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        var root = document.RootElement;
        var systemPrompt = root.GetProperty("system_instruction")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();
        Assert.Contains("Estás ayudando a alguien", systemPrompt, StringComparison.Ordinal);
        Assert.Contains(
            "Contexto del sistema que se está enseñando:\ncontexto privado",
            systemPrompt,
            StringComparison.Ordinal);
        Assert.Equal(
            "¿Qué hace?",
            root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task GenerateAnswerAsync_CustomValidModel_UsesConfiguredEndpointPath()
    {
        using var handler = RecordingHttpMessageHandler.Json(SuccessfulResponse);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, new GeminiClientOptions { Model = "gemini-2.5-flash" });

        _ = await client.GenerateAnswerAsync("pregunta", null);

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
            handler.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task TranscribeAudioAsync_ValidAudio_UsesInlineWavWithoutProjectContext()
    {
        using var handler = RecordingHttpMessageHandler.Json(SuccessfulResponse);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);
        using var audio = TestAudio.Create([1, 2, 3, 4]);

        _ = await client.TranscribeAudioAsync(audio, "es");

        using var document = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        var parts = document.RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal("audio/wav", parts[1].GetProperty("inline_data").GetProperty("mime_type").GetString());
        Assert.Equal("AQIDBA==", parts[1].GetProperty("inline_data").GetProperty("data").GetString());
        Assert.DoesNotContain("system_instruction", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Contexto del sistema", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAnswerAsync_MissingKey_FailsBeforeNetwork()
    {
        using var handler = RecordingHttpMessageHandler.Json(SuccessfulResponse);
        using var httpClient = new HttpClient(handler);
        var client = new GeminiClient(httpClient, new StaticApiKeyProvider("  "), new GeminiClientOptions());

        var exception = await Assert.ThrowsAsync<GeminiApiException>(() =>
            client.GenerateAnswerAsync("pregunta", null));

        Assert.Equal(GeminiErrorKind.MissingApiKey, exception.Kind);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(401, GeminiErrorKind.Unauthorized)]
    [InlineData(403, GeminiErrorKind.Unauthorized)]
    [InlineData(429, GeminiErrorKind.RateLimited)]
    [InlineData(500, GeminiErrorKind.ServiceUnavailable)]
    [InlineData(503, GeminiErrorKind.ServiceUnavailable)]
    [InlineData(400, GeminiErrorKind.HttpError)]
    public async Task GenerateAnswerAsync_HttpFailure_ReturnsTypedError(
        int statusCode,
        GeminiErrorKind expectedKind)
    {
        const string sensitiveBody = "{\"error\":{\"message\":\"secret response body\"}}";
        using var handler = RecordingHttpMessageHandler.Json(sensitiveBody, (HttpStatusCode)statusCode);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<GeminiApiException>(() =>
            client.GenerateAnswerAsync("pregunta", "contexto"));

        Assert.Equal(expectedKind, exception.Kind);
        Assert.Equal((HttpStatusCode)statusCode, exception.StatusCode);
        Assert.DoesNotContain("secret response body", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAnswerAsync_Timeout_ReturnsTypedTimeout()
    {
        using var handler = new RecordingHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        var options = new GeminiClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(20) };
        var client = CreateClient(httpClient, options);

        var exception = await Assert.ThrowsAsync<GeminiApiException>(() =>
            client.GenerateAnswerAsync("pregunta", null));

        Assert.Equal(GeminiErrorKind.Timeout, exception.Kind);
    }

    [Fact]
    public async Task GenerateAnswerAsync_CallerCancellation_RemainsCancellation()
    {
        using var handler = new RecordingHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GenerateAnswerAsync("pregunta", null, cancellation.Token));
    }

    [Fact]
    public async Task GenerateAnswerAsync_NetworkFailure_ReturnsServiceUnavailable()
    {
        using var handler = new RecordingHttpMessageHandler((_, _) =>
            throw new HttpRequestException("sensitive network detail"));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<GeminiApiException>(() =>
            client.GenerateAnswerAsync("pregunta", null));

        Assert.Equal(GeminiErrorKind.ServiceUnavailable, exception.Kind);
        Assert.DoesNotContain("sensitive network detail", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("not-json", GeminiErrorKind.InvalidResponse)]
    [InlineData("{}", GeminiErrorKind.EmptyResponse)]
    [InlineData("{\"candidates\":[]}", GeminiErrorKind.EmptyResponse)]
    [InlineData("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}", GeminiErrorKind.Blocked)]
    [InlineData("{\"candidates\":[{\"finishReason\":\"SAFETY\"}]}", GeminiErrorKind.Blocked)]
    public async Task GenerateAnswerAsync_UnusableResponse_ReturnsTypedError(
        string response,
        GeminiErrorKind expectedKind)
    {
        using var handler = RecordingHttpMessageHandler.Json(response);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<GeminiApiException>(() =>
            client.GenerateAnswerAsync("pregunta", null));

        Assert.Equal(expectedKind, exception.Kind);
    }

    [Fact]
    public async Task GenerateAnswerAsync_OversizedResponse_IsRejected()
    {
        using var handler = RecordingHttpMessageHandler.Json(new string('x', 100));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, new GeminiClientOptions { MaximumResponseBytes = 20 });

        var exception = await Assert.ThrowsAsync<GeminiApiException>(() =>
            client.GenerateAnswerAsync("pregunta", null));

        Assert.Equal(GeminiErrorKind.InvalidResponse, exception.Kind);
    }

    [Fact]
    public async Task TranscribeAsync_WithoutCurrentConsent_DoesNotSendAudio()
    {
        using var handler = RecordingHttpMessageHandler.Json(SuccessfulResponse);
        using var httpClient = new HttpClient(handler);
        var service = new GeminiAudioTranscriptionService(CreateClient(httpClient), () => null);
        using var audio = TestAudio.Create();

        await Assert.ThrowsAsync<GeminiAudioConsentRequiredException>(() =>
            service.TranscribeAsync(audio, "es"));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_WithCurrentConsent_SendsExactlyOnce()
    {
        using var handler = RecordingHttpMessageHandler.Json(SuccessfulResponse);
        using var httpClient = new HttpClient(handler);
        var service = new GeminiAudioTranscriptionService(
            CreateClient(httpClient),
            () => GeminiAudioConsentPolicy.Grant());
        using var audio = TestAudio.Create();

        var transcript = await service.TranscribeAsync(audio, "auto");

        Assert.Equal("Respuesta breve", transcript);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(MirrorPowerAI.Core.Transcription.TranscriptionProvider.GeminiAudio, service.Provider);
    }

    [Fact]
    public async Task TranscribeAudioAsync_OverConfiguredLimit_FailsBeforeNetwork()
    {
        using var handler = RecordingHttpMessageHandler.Json(SuccessfulResponse);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, new GeminiClientOptions { MaximumAudioBytes = 3 });
        using var audio = TestAudio.Create([1, 2, 3, 4]);

        var exception = await Assert.ThrowsAsync<GeminiApiException>(() =>
            client.TranscribeAudioAsync(audio, "es"));

        Assert.Equal(GeminiErrorKind.InputTooLarge, exception.Kind);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Constructor_InsecureBaseUri_RejectsConfiguration()
    {
        using var httpClient = new HttpClient();
        var options = new GeminiClientOptions { ApiBaseUri = new Uri("http://example.test/") };

        Assert.Throws<ArgumentException>(() =>
            new GeminiClient(httpClient, new StaticApiKeyProvider("key"), options));
    }

    [Theory]
    [InlineData("https://example.test/v1beta/")]
    [InlineData("https://generativelanguage.googleapis.com.evil.test/v1beta/")]
    [InlineData("https://user@generativelanguage.googleapis.com/v1beta/")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/?redirect=evil")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/#fragment")]
    [InlineData("https://generativelanguage.googleapis.com/v1/")]
    public void Constructor_NonOfficialBaseUri_RejectsBeforeApiKeyCanBeSent(string endpoint)
    {
        using var httpClient = new HttpClient();
        var options = new GeminiClientOptions { ApiBaseUri = new Uri(endpoint) };

        Assert.Throws<ArgumentException>(() =>
            new GeminiClient(httpClient, new StaticApiKeyProvider("key"), options));
    }

    private static GeminiClient CreateClient(
        HttpClient httpClient,
        GeminiClientOptions? options = null) =>
        new(httpClient, new StaticApiKeyProvider("test-api-key"), options ?? new GeminiClientOptions());
}
