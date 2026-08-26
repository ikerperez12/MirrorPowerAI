using System.Net;
using MirrorPowerAI.Core.Gemini;
using MirrorPowerAI.Core.Security;
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

        var snapshot = Assert.Single(snapshots);
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
