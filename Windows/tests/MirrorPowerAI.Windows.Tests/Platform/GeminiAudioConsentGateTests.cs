using System.Net;
using System.Net.Http;
using System.Windows.Controls;
using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Core.Gemini;
using MirrorPowerAI.Core.Privacy;
using MirrorPowerAI.Core.Security;
using MirrorPowerAI.Windows.Platform;
using MirrorPowerAI.Windows.Resources;

namespace MirrorPowerAI.Windows.Tests.Platform;

[Collection(nameof(WpfSettingsWindowSerialTestSuite))]
public sealed class GeminiAudioConsentGateTests
{
    [Fact]
    public async Task ReloadAsync_PersistedConsentAfterRestart_ShowsUncheckedReauthorizationState()
    {
        var persistedConsent = GeminiAudioConsentPolicy.Grant();
        var settingsStore = new ControllableSettingsStore(new AppSettings
        {
            TranscriptionProvider = TranscriptionProviders.GeminiAudio,
            GeminiAudioConsentVersion = persistedConsent.Version,
            GeminiAudioConsentGrantedAtUtc = persistedConsent.GrantedAtUtc,
        });
        using var restartedGate = new GeminiAudioConsentGate();

        await StaDispatcher.RunAsync(async () =>
        {
            var window = new MainWindow(
                settingsStore,
                new InMemorySecretStore(),
                new TestAudioDeviceCatalog(),
                LocalizationService.Current,
                restartedGate);

            await window.ReloadAsync();

            Assert.False(GetCheckBox(window, "CloudConsentBox").IsChecked);
            var status = Assert.IsType<TextBlock>(window.FindName("StatusText"));
            Assert.Equal(System.Windows.Visibility.Visible, status.Visibility);
            Assert.Contains(
                LocalizationService.Current["GeminiAudioReauthorizationRequired"],
                status.Text,
                StringComparison.Ordinal);
            Assert.Null(restartedGate.GetEffectiveConsent(persistedConsent));
        });
    }

    [Fact]
    public async Task SaveAsync_RevocationPersistenceFailure_BlocksExistingFreshAndRestartedGeminiServicesUntilExplicitSaveSucceeds()
    {
        // Arrange
        var persistedConsent = GeminiAudioConsentPolicy.Grant();
        var settingsStore = new ControllableSettingsStore(new AppSettings
        {
            TranscriptionProvider = TranscriptionProviders.GeminiAudio,
            GeminiAudioConsentVersion = persistedConsent.Version,
            GeminiAudioConsentGrantedAtUtc = persistedConsent.GrantedAtUtc,
        })
        {
            FailWrites = true,
        };
        using var gate = new GeminiAudioConsentGate();
        Assert.Null(gate.GetEffectiveConsent(persistedConsent));

        // Simulate the only valid opening path: an earlier successful explicit save in this process.
        gate.AllowAfterSuccessfulExplicitConsentSave();
        Assert.NotNull(gate.GetEffectiveConsent(persistedConsent));

        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var geminiClient = new GeminiClient(
            httpClient,
            new StaticApiKeyProvider("test-api-key"),
            new GeminiClientOptions());
        var existingService = new GeminiAudioTranscriptionService(
            geminiClient,
            () => persistedConsent,
            () => gate.TryAuthorize(persistedConsent));
        using var audio = new CapturedAudio(new byte[] { 1, 2, 3, 4 }, TimeSpan.FromMilliseconds(100), true);

        // Act: revocation is requested, but the settings file cannot be replaced.
        await StaDispatcher.RunAsync(async () =>
        {
            var window = new MainWindow(
                settingsStore,
                new InMemorySecretStore(),
                new TestAudioDeviceCatalog(),
                LocalizationService.Current,
                gate);
            var savedCount = 0;
            window.SettingsSaved += (_, _) => savedCount++;

            await window.ReloadAsync();
            GetComboBox(window, "ProviderBox").SelectedValue = TranscriptionProviders.LocalWhisper;
            GetCheckBox(window, "CloudConsentBox").IsChecked = false;
            await window.SaveAsync();

            Assert.Equal(0, savedCount);
            Assert.Equal(TranscriptionProviders.GeminiAudio, settingsStore.Stored.TranscriptionProvider);
            Assert.Null(gate.GetEffectiveConsent(persistedConsent));
        });

        // Assert: services created before and after revocation consult the same live gate. The mocked
        // HTTP handler proves no upload occurs while it is blocked and does not use a real network.
        var freshService = new GeminiAudioTranscriptionService(
            geminiClient,
            () => persistedConsent,
            () => gate.TryAuthorize(persistedConsent));

        await Assert.ThrowsAsync<GeminiAudioConsentRequiredException>(() =>
            existingService.TranscribeAsync(audio, "es"));
        await Assert.ThrowsAsync<GeminiAudioConsentRequiredException>(() =>
            freshService.TranscribeAsync(audio, "es"));
        Assert.Equal(0, handler.CallCount);

        // A logical restart must fail closed even though its persisted acceptance history remains valid.
        using var restartedGate = new GeminiAudioConsentGate();
        var restartedService = new GeminiAudioTranscriptionService(
            geminiClient,
            () => persistedConsent,
            () => restartedGate.TryAuthorize(persistedConsent));
        await Assert.ThrowsAsync<GeminiAudioConsentRequiredException>(() =>
            restartedService.TranscribeAsync(audio, "es"));
        Assert.Equal(0, handler.CallCount);

        // Only a later successful save with fresh explicit cloud consent may reopen the barrier.
        settingsStore.FailWrites = false;
        await StaDispatcher.RunAsync(async () =>
        {
            var window = new MainWindow(
                settingsStore,
                new InMemorySecretStore(),
                new TestAudioDeviceCatalog(),
                LocalizationService.Current,
                gate);
            var savedCount = 0;
            window.SettingsSaved += (_, _) => savedCount++;

            await window.ReloadAsync();
            GetComboBox(window, "ProviderBox").SelectedValue = TranscriptionProviders.GeminiAudio;
            GetCheckBox(window, "CloudConsentBox").IsChecked = true;
            await window.SaveAsync();

            Assert.Equal(1, savedCount);
            Assert.NotNull(gate.GetEffectiveConsent(persistedConsent));
        });

        var transcript = await freshService.TranscribeAsync(audio, "es");

        Assert.Equal("transcripción", transcript);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_RevocationWhileApiKeyReadIsPending_CancelsBeforeNetwork()
    {
        // Arrange
        var consent = GeminiAudioConsentPolicy.Grant();
        using var gate = new GeminiAudioConsentGate();
        gate.AllowAfterSuccessfulExplicitConsentSave();
        var apiKeyProvider = new BlockingApiKeyProvider();
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GeminiClient(httpClient, apiKeyProvider, new GeminiClientOptions());
        var service = new GeminiAudioTranscriptionService(
            client,
            () => consent,
            () => gate.TryAuthorize(consent));
        using var audio = new CapturedAudio(new byte[] { 1, 2, 3, 4 }, TimeSpan.FromMilliseconds(100), true);

        // Act: the key provider deliberately ignores cancellation until revocation has happened.
        var operation = service.TranscribeAsync(audio, "es");
        await apiKeyProvider.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        gate.Revoke();
        apiKeyProvider.Complete("test-api-key");

        // Assert: GeminiClient checks the linked revocation token again after key retrieval and before send.
        await Assert.ThrowsAsync<GeminiAudioConsentRequiredException>(() => operation);
        Assert.Equal(0, handler.CallCount);
    }

    private static ComboBox GetComboBox(MainWindow window, string name) =>
        Assert.IsType<ComboBox>(window.FindName(name));

    private static CheckBox GetCheckBox(MainWindow window, string name) =>
        Assert.IsType<CheckBox>(window.FindName(name));

    private sealed class ControllableSettingsStore(AppSettings stored) : IAppSettingsStore
    {
        public bool FailWrites { get; set; }

        public AppSettings Stored { get; private set; } = stored.Normalize();

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Stored);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWrites)
            {
                throw new IOException("Simulated settings write failure.");
            }

            Stored = settings.Normalize();
            return Task.CompletedTask;
        }
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(
                name == MainWindow.GeminiApiKeySecretName ? "test-api-key" : null);
        }

        public Task SetSecretAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class TestAudioDeviceCatalog : IAudioDeviceCatalog
    {
        public Task<IReadOnlyList<AudioDeviceOption>> GetOutputDevicesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AudioDeviceOption> devices =
            [new AudioDeviceOption(AudioDeviceOption.DefaultDeviceId, "Default")];
            return Task.FromResult(devices);
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

    private sealed class BlockingApiKeyProvider : IGeminiApiKeyProvider
    {
        private readonly TaskCompletionSource<string?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            return _completion.Task;
        }

        public void Complete(string value) => _completion.TrySetResult(value);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"transcripción\"}]},\"finishReason\":\"STOP\"}]}")
            });
        }
    }
}
