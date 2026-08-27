using System.Net;
using MirrorPowerAI.Core.Answers;
using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Core.Gemini;
using MirrorPowerAI.Core.Transcription;

namespace MirrorPowerAI.Core.Tests;

internal sealed class FakeAudioCaptureService : IAudioCaptureService
{
    public CapturedAudio Audio { get; set; } = TestAudio.Create();

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public bool IsCapturing { get; private set; }

    public Func<CancellationToken, Task>? StartHandler { get; set; }

    public bool CaptureBeforeStartHandler { get; set; }

    public Func<CancellationToken, Task<CapturedAudio>>? StopHandler { get; set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCount++;
        if (CaptureBeforeStartHandler)
        {
            IsCapturing = true;
        }

        if (StartHandler is not null)
        {
            await StartHandler(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        IsCapturing = true;
    }

    public async Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        StopCount++;
        if (StopHandler is not null)
        {
            var result = await StopHandler(cancellationToken);
            IsCapturing = false;
            return result;
        }

        cancellationToken.ThrowIfCancellationRequested();
        IsCapturing = false;
        return Audio;
    }
}

internal sealed class FakeContinuousAudioCaptureService : IAudioCaptureService, IAudioSegmentSource
{
    public event EventHandler<AudioSegmentAvailableEventArgs>? SegmentAvailable;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public bool IsCapturing { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        IsCapturing = true;
        return Task.CompletedTask;
    }

    public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        IsCapturing = false;
        return Task.FromResult(TestAudio.Create());
    }

    public void Publish(CapturedAudio audio, bool forcedBoundary = false)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (!IsCapturing || SegmentAvailable is null)
        {
            audio.Dispose();
            return;
        }

        SegmentAvailable(this, new AudioSegmentAvailableEventArgs(audio, forcedBoundary));
    }
}

internal sealed class FakeTranscriptionService(TranscriptionProvider provider) : ITranscriptionService
{
    public TranscriptionProvider Provider { get; } = provider;

    public int CallCount { get; private set; }

    public Func<CapturedAudio, string, CancellationToken, Task<string>> Handler { get; set; } =
        static (_, _, _) => Task.FromResult("¿Qué hace el sistema?");

    public Task<string> TranscribeAsync(
        CapturedAudio audio,
        string language,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Handler(audio, language, cancellationToken);
    }
}

internal sealed class FakeAnswerService : IAnswerService
{
    public int CallCount { get; private set; }

    public string? LastQuestion { get; private set; }

    public string? LastContext { get; private set; }

    public Func<string, string?, CancellationToken, Task<string>> Handler { get; set; } =
        static (_, _, _) => Task.FromResult("Captura audio de salida y responde.");

    public Task<string> AskAsync(
        string question,
        string? context,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastQuestion = question;
        LastContext = context;
        return Handler(question, context, cancellationToken);
    }
}

internal sealed class StaticApiKeyProvider(string? apiKey) : IGeminiApiKeyProvider
{
    public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(apiKey);
    }
}

internal sealed class RecordingHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    public int CallCount { get; private set; }

    public Uri? RequestUri { get; private set; }

    public HttpMethod? Method { get; private set; }

    public string? ApiKey { get; private set; }

    public string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        RequestUri = request.RequestUri;
        Method = request.Method;
        ApiKey = request.Headers.TryGetValues("x-goog-api-key", out var values)
            ? values.Single()
            : null;
        Body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return await handler(request, cancellationToken);
    }

    public static RecordingHttpMessageHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json),
        }));
}

internal static class TestAudio
{
    public static CapturedAudio Create(byte[]? bytes = null) =>
        new(bytes ?? [82, 73, 70, 70, 1, 2, 3, 4], TimeSpan.FromSeconds(1));
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"MirrorPowerAI.Core.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
