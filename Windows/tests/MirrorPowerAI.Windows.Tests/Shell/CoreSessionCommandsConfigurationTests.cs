using System.Net;
using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Core.Gemini;
using MirrorPowerAI.Core.Security;
using MirrorPowerAI.Windows.Audio;
using MirrorPowerAI.Windows.Platform;
using MirrorPowerAI.Windows.Shell;
using MirrorPowerAI.Windows.Transcription;

namespace MirrorPowerAI.Windows.Tests.Shell;

public sealed class CoreSessionCommandsConfigurationTests
{
    [Fact]
    public async Task ToggleAsync_MissingApiKey_PublishesActionableErrorBeforeAudioOrNetworkWork()
    {
        var leaseProvider = new FailingIfUsedModelLeaseProvider();
        var inference = new FailingIfUsedInferenceEngine();
        var localTranscription = new WhisperLocalTranscriptionService(
            leaseProvider,
            inference,
            Path.GetTempPath());
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var geminiClient = new GeminiClient(
            httpClient,
            new StaticApiKeyProvider("unused-test-key"),
            new GeminiClientOptions());
        await using var commands = new CoreSessionCommands(
            new InMemorySettingsStore(),
            new MissingSecretStore(),
            localTranscription,
            geminiClient);
        var snapshots = new List<SessionSnapshot>();
        commands.StateChanged += (_, eventArgs) => snapshots.Add(eventArgs.Snapshot);

        await commands.ToggleAsync(CancellationToken.None);

        Assert.Equal(ShellActivityState.Processing, snapshots[0].Activity);
        Assert.Equal("SessionPreparing", snapshots[0].UserMessage);
        var snapshot = snapshots[^1];
        Assert.Equal(ShellActivityState.Error, snapshot.Activity);
        Assert.Equal("SessionApiKeyRequired", snapshot.UserMessage);
        Assert.Equal(0, leaseProvider.CallCount);
        Assert.Equal(0, inference.CallCount);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("key\rwith-control")]
    public void IsUsableGeminiApiKey_MissingOrStructurallyInvalidKey_FailsClosed(string? apiKey)
    {
        Assert.False(CoreSessionCommands.IsUsableGeminiApiKey(apiKey));
    }

    [Fact]
    public void IsUsableGeminiApiKey_OverlongKey_FailsClosed()
    {
        Assert.False(CoreSessionCommands.IsUsableGeminiApiKey(new string('k', 513)));
    }

    [Fact]
    public void IsUsableGeminiApiKey_BoundedNonControlKey_AllowsSessionPreflight()
    {
        Assert.True(CoreSessionCommands.IsUsableGeminiApiKey("  test-api-key  "));
    }

    [Theory]
    [InlineData(AudioCaptureFailure.SourceUnavailable, "SessionAudioSourceUnavailable")]
    [InlineData(AudioCaptureFailure.SourceDisconnected, "SessionAudioSourceDisconnected")]
    [InlineData(AudioCaptureFailure.DefaultDeviceChanged, "SessionAudioDeviceChanged")]
    [InlineData(AudioCaptureFailure.BufferLimitReached, "SessionAudioCaptureLimit")]
    [InlineData(AudioCaptureFailure.BackendFailure, "SessionAudioBackendError")]
    public void MapAudioCaptureFailureResourceKey_EachRecoverableFailure_IsActionable(
        AudioCaptureFailure failure,
        string expectedResourceKey)
    {
        Assert.Equal(
            expectedResourceKey,
            CoreSessionCommands.MapAudioCaptureFailureResourceKey(failure));
    }

    [Fact]
    public async Task ToggleAsync_AudioActivityChangesSnapshotFromWaitingToDetected()
    {
        var leaseProvider = new FailingIfUsedModelLeaseProvider();
        var inference = new FailingIfUsedInferenceEngine();
        var localTranscription = new WhisperLocalTranscriptionService(
            leaseProvider,
            inference,
            Path.GetTempPath());
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var geminiClient = new GeminiClient(
            httpClient,
            new StaticApiKeyProvider("unused-test-key"),
            new GeminiClientOptions());
        var audioCapture = new ActivityAudioCaptureService();
        await using var commands = new CoreSessionCommands(
            new InMemorySettingsStore(),
            new ApiKeySecretStore(),
            localTranscription,
            geminiClient,
            geminiAudioConsentGate: null,
            (_, _) => audioCapture);
        var snapshots = new List<SessionSnapshot>();
        commands.StateChanged += (_, eventArgs) => snapshots.Add(eventArgs.Snapshot);

        await commands.ToggleAsync(CancellationToken.None);
        var waiting = snapshots[^1];
        audioCapture.ReportAudibleSignal();
        var detected = snapshots[^1];

        Assert.Equal(ShellActivityState.Capturing, waiting.Activity);
        Assert.False(waiting.AudioSignalDetected);
        Assert.Equal(ShellActivityState.Capturing, detected.Activity);
        Assert.True(detected.AudioSignalDetected);
        Assert.Equal(0, leaseProvider.CallCount);
        Assert.Equal(0, inference.CallCount);
        Assert.Equal(1, handler.CallCount);
    }

    private sealed class InMemorySettingsStore : IAppSettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AppSettings());
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MissingSecretStore : ISecretStore
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }

        public Task SetSecretAsync(string name, string value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ApiKeySecretStore : ISecretStore
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(
                name == MainWindow.GeminiApiKeySecretName ? "test-api-key" : null);
        }

        public Task SetSecretAsync(string name, string value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ActivityAudioCaptureService :
        IAudioCaptureService,
        IAudioCaptureActivitySource
    {
        public event EventHandler? AudibleSignalDetected;

        public bool IsCapturing { get; private set; }

        public bool HasDetectedAudibleSignal { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsCapturing = true;
            HasDetectedAudibleSignal = false;
            return Task.CompletedTask;
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsCapturing = false;
            HasDetectedAudibleSignal = false;
            return Task.FromResult(new CapturedAudio(Array.Empty<byte>(), TimeSpan.Zero, false));
        }

        public void ReportAudibleSignal()
        {
            HasDetectedAudibleSignal = true;
            AudibleSignalDetected?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FailingIfUsedModelLeaseProvider : IWhisperModelLeaseProvider
    {
        public int CallCount { get; private set; }

        public Task<IWhisperModelLease> AcquireVerifiedLeaseAsync(
            string modelDirectory,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("Model access was not expected.");
        }
    }

    private sealed class FailingIfUsedInferenceEngine : IWhisperInferenceEngine
    {
        public int CallCount { get; private set; }

        public Task<string> TranscribeAsync(
            string modelPath,
            ReadOnlyMemory<byte> wavData,
            string language,
            int threadCount,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("Inference was not expected.");
        }
    }

    private sealed class StaticApiKeyProvider(string value) : IGeminiApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(value);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
