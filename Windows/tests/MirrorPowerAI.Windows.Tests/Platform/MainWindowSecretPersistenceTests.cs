using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MirrorPowerAI.Core.Security;
using MirrorPowerAI.Windows.Platform;
using MirrorPowerAI.Windows.Resources;

namespace MirrorPowerAI.Windows.Tests.Platform;

[Collection(nameof(WpfSettingsWindowSerialTestSuite))]
public sealed class MainWindowSecretPersistenceTests
{
    [Fact]
    public async Task SaveAndStartButton_PersistsOnceAndRequestsCaptureAfterSave()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsStore = new JsonSettingsStore(Path.Combine(testDirectory.Path, "settings.json"));
        var secretStore = new RecordingSecretStore();

        await StaDispatcher.RunAsync(async () =>
        {
            var window = CreateWindow(settingsStore, secretStore);
            await window.ReloadAsync();
            GetPasswordBox(window, "ApiKeyBox").Password = "test-api-key";
            var saved = new TaskCompletionSource<SettingsSavedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            window.SettingsSaved += (_, eventArgs) => saved.TrySetResult(eventArgs);

            GetButton(window, "SaveAndStartButtonControl").RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var eventArgs = await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(eventArgs.StartAfterSave);
            Assert.Equal(
                "test-api-key",
                secretStore.Mutations.Single(
                    mutation => mutation.Name == MainWindow.GeminiApiKeySecretName).Value);
            Assert.Equal(
                TranscriptionProviders.LocalWhisper,
                (await settingsStore.LoadAsync()).TranscriptionProvider);
        });
    }

    [Fact]
    public async Task ReloadAsync_ReadFailuresWithEmptyFields_PreservesBothSecretsWhileSavingOtherSettings()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsStore = new JsonSettingsStore(Path.Combine(testDirectory.Path, "settings.json"));
        var secretStore = new RecordingSecretStore(
            readFailures: new Dictionary<string, Exception>
            {
                [MainWindow.GeminiApiKeySecretName] = new CryptographicException("Unreadable API key."),
                [MainWindow.ProjectContextSecretName] = new IOException("Unreadable context."),
            });

        await StaDispatcher.RunAsync(async () =>
        {
            var window = CreateWindow(settingsStore, secretStore);

            await window.ReloadAsync();
            Assert.Equal(string.Empty, GetPasswordBox(window, "ApiKeyBox").Password);
            Assert.Equal(string.Empty, GetTextBox(window, "ContextBox").Text);

            GetComboBox(window, "DeviceBox").SelectedValue = "test-device";
            await window.SaveAsync();

            Assert.Empty(secretStore.Mutations);
            Assert.Equal("test-device", (await settingsStore.LoadAsync()).AudioDeviceId);
        });
    }

    [Fact]
    public async Task ReloadAsync_OneSecretReadAndCleared_DeletesOnlyThatSecretWhenOtherReadFailed()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsStore = new JsonSettingsStore(Path.Combine(testDirectory.Path, "settings.json"));
        var secretStore = new RecordingSecretStore(
            initialSecrets: new Dictionary<string, string>
            {
                [MainWindow.GeminiApiKeySecretName] = "existing-api-key",
            },
            readFailures: new Dictionary<string, Exception>
            {
                [MainWindow.ProjectContextSecretName] = new IOException("Unreadable context."),
            });

        await StaDispatcher.RunAsync(async () =>
        {
            var window = CreateWindow(settingsStore, secretStore);

            await window.ReloadAsync();
            GetPasswordBox(window, "ApiKeyBox").Password = string.Empty;
            await window.SaveAsync();

            Assert.Collection(
                secretStore.Mutations,
                mutation => Assert.Equal(
                    new SecretMutation(SecretMutationKind.Delete, MainWindow.GeminiApiKeySecretName, null),
                    mutation));
            Assert.DoesNotContain(
                secretStore.Mutations,
                mutation => mutation.Name == MainWindow.ProjectContextSecretName);
        });
    }

    [Fact]
    public async Task ReloadAsync_ReadFailuresWithReplacements_SetsBothSecrets()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsStore = new JsonSettingsStore(Path.Combine(testDirectory.Path, "settings.json"));
        var secretStore = new RecordingSecretStore(
            readFailures: new Dictionary<string, Exception>
            {
                [MainWindow.GeminiApiKeySecretName] = new CryptographicException("Unreadable API key."),
                [MainWindow.ProjectContextSecretName] = new IOException("Unreadable context."),
            });

        await StaDispatcher.RunAsync(async () =>
        {
            var window = CreateWindow(settingsStore, secretStore);

            await window.ReloadAsync();
            GetPasswordBox(window, "ApiKeyBox").Password = "replacement-api-key";
            GetTextBox(window, "ContextBox").Text = "replacement context";
            await window.SaveAsync();

            Assert.Equal(
                [
                    new SecretMutation(
                        SecretMutationKind.Set,
                        MainWindow.GeminiApiKeySecretName,
                        "replacement-api-key"),
                    new SecretMutation(
                        SecretMutationKind.Set,
                        MainWindow.ProjectContextSecretName,
                        "replacement context"),
                ],
                secretStore.Mutations);
        });
    }

    [Fact]
    public async Task ReloadAsync_SaveAsync_PreservesHiddenValidGeminiModel()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsStore = new JsonSettingsStore(Path.Combine(testDirectory.Path, "settings.json"));
        await settingsStore.SaveAsync(new AppSettings { GeminiModel = "gemini-2.5-flash" });
        var secretStore = new RecordingSecretStore();

        await StaDispatcher.RunAsync(async () =>
        {
            var window = CreateWindow(settingsStore, secretStore);

            await window.ReloadAsync();
            GetComboBox(window, "DeviceBox").SelectedValue = "test-device";
            await window.SaveAsync();

            Assert.Equal("gemini-2.5-flash", (await settingsStore.LoadAsync()).GeminiModel);
        });
    }

    [Fact]
    public async Task SaveAsync_WithoutReload_PreservesHiddenValidGeminiModel()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsStore = new JsonSettingsStore(Path.Combine(testDirectory.Path, "settings.json"));
        await settingsStore.SaveAsync(new AppSettings { GeminiModel = "gemini-2.5-flash" });
        var secretStore = new RecordingSecretStore();

        await StaDispatcher.RunAsync(async () =>
        {
            var window = CreateWindow(settingsStore, secretStore);

            // Public callers can save without first showing the window. The hidden model must not
            // silently reset in that supported programmatic path.
            await window.SaveAsync();

            Assert.Equal("gemini-2.5-flash", (await settingsStore.LoadAsync()).GeminiModel);
        });
    }

    [Fact]
    public async Task ReloadAsync_ApplicationCaptureSelection_IsVisibleAndPersistsExactApplication()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsStore = new JsonSettingsStore(Path.Combine(testDirectory.Path, "settings.json"));
        var secretStore = new RecordingSecretStore();
        var application = new AudioApplicationOption(4242, "discord", "Discord");

        await StaDispatcher.RunAsync(async () =>
        {
            var window = CreateWindow(
                settingsStore,
                secretStore,
                new TestAudioApplicationCatalog(application));

            await window.ReloadAsync();
            Assert.Equal(Visibility.Visible, GetPanel(window, "SystemOutputPanel").Visibility);
            Assert.Equal(Visibility.Collapsed, GetPanel(window, "ApplicationOutputPanel").Visibility);

            GetComboBox(window, "CaptureSourceBox").SelectedValue = AudioCaptureSources.Application;
            Assert.Equal(Visibility.Collapsed, GetPanel(window, "SystemOutputPanel").Visibility);
            Assert.Equal(Visibility.Visible, GetPanel(window, "ApplicationOutputPanel").Visibility);

            await window.SaveAsync();

            var saved = await settingsStore.LoadAsync();
            Assert.Equal(AudioCaptureSources.Application, saved.AudioCaptureSource);
            Assert.Equal(application.ProcessName, saved.AudioProcessName);
            Assert.Equal(application.ProcessId, saved.AudioProcessId);
        });
    }

    [Fact]
    public async Task SaveAsync_ApplicationCaptureWithoutRunningSelection_FailsClosedWithoutPersisting()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        var settingsStore = new JsonSettingsStore(settingsPath);
        var secretStore = new RecordingSecretStore();

        await StaDispatcher.RunAsync(async () =>
        {
            var window = CreateWindow(settingsStore, secretStore);

            await window.ReloadAsync();
            GetComboBox(window, "CaptureSourceBox").SelectedValue = AudioCaptureSources.Application;
            await window.SaveAsync();

            Assert.False(File.Exists(settingsPath));
            Assert.Empty(secretStore.Mutations);
            Assert.Equal(Visibility.Visible, GetTextBlock(window, "StatusText").Visibility);
            Assert.Contains(
                LocalizationService.Current["AudioApplicationRequired"],
                GetTextBlock(window, "StatusText").Text,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task TryPrepareForApplicationExitAsync_ActiveSave_WaitsAndPreventsAnotherSave()
    {
        var settingsStore = new BlockingSettingsStore();
        var secretStore = new RecordingSecretStore();

        await StaDispatcher.RunAsync(async () =>
        {
            var window = CreateWindow(settingsStore, secretStore);
            var saveTask = window.SaveAsync();
            await settingsStore.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var prepareExitTask = window.TryPrepareForApplicationExitAsync(TimeSpan.FromSeconds(5));
            Assert.False(prepareExitTask.IsCompleted);

            settingsStore.ReleaseFirstSave();
            Assert.True(await prepareExitTask);
            await saveTask;
            Assert.Equal(1, settingsStore.SaveCallCount);

            await window.SaveAsync();
            Assert.Equal(1, settingsStore.SaveCallCount);
        });
    }

    [Fact]
    public async Task TryPrepareForApplicationExitAsync_SaveTimeout_AllowsRetryAfterSaveFinishes()
    {
        var settingsStore = new BlockingSettingsStore();
        var secretStore = new RecordingSecretStore();

        await StaDispatcher.RunAsync(async () =>
        {
            var window = CreateWindow(settingsStore, secretStore);
            var saveTask = window.SaveAsync();
            await settingsStore.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(await window.TryPrepareForApplicationExitAsync(TimeSpan.FromMilliseconds(50)));

            settingsStore.ReleaseFirstSave();
            await saveTask;
            await window.SaveAsync();
            Assert.Equal(2, settingsStore.SaveCallCount);
        });
    }

    [Fact]
    public async Task SaveAsync_ActiveSave_DisablesEditingAndPreventsDismissalUntilComplete()
    {
        var settingsStore = new BlockingSettingsStore();
        var secretStore = new RecordingSecretStore();

        await StaDispatcher.RunAsync(async () =>
        {
            var window = CreateWindow(settingsStore, secretStore);
            window.Show();
            Task? saveTask = null;

            try
            {
                saveTask = window.SaveAsync();
                await settingsStore.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.True(window.IsSaving);
                Assert.False(GetPasswordBox(window, "ApiKeyBox").IsEnabled);
                Assert.False(GetTextBox(window, "ContextBox").IsEnabled);
                Assert.False(GetComboBox(window, "ProviderBox").IsEnabled);
                Assert.False(GetButton(window, "SaveAndStartButtonControl").IsEnabled);
                Assert.False(GetButton(window, "SaveButtonControl").IsEnabled);
                Assert.False(GetButton(window, "CancelButtonControl").IsEnabled);

                window.Close();
                Assert.True(window.IsVisible);

                GetButton(window, "CancelButtonControl").RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(window.IsVisible);

                settingsStore.ReleaseFirstSave();
                await saveTask;

                Assert.False(window.IsSaving);
                Assert.True(GetPasswordBox(window, "ApiKeyBox").IsEnabled);
                Assert.True(GetComboBox(window, "ProviderBox").IsEnabled);
                Assert.True(GetButton(window, "SaveAndStartButtonControl").IsEnabled);
                Assert.True(GetButton(window, "SaveButtonControl").IsEnabled);
                Assert.True(GetButton(window, "CancelButtonControl").IsEnabled);
            }
            finally
            {
                settingsStore.ReleaseFirstSave();
                if (saveTask is not null)
                {
                    await saveTask;
                }

                window.CloseForApplicationExit();
            }
        });
    }

    private static MainWindow CreateWindow(
        IAppSettingsStore settingsStore,
        ISecretStore secretStore,
        IAudioApplicationCatalog? audioApplicationCatalog = null) => new(
            settingsStore,
            secretStore,
            new TestAudioDeviceCatalog(),
            LocalizationService.Current,
            audioApplicationCatalog: audioApplicationCatalog);

    private static PasswordBox GetPasswordBox(MainWindow window, string name) =>
        Assert.IsType<PasswordBox>(window.FindName(name));

    private static TextBox GetTextBox(MainWindow window, string name) =>
        Assert.IsType<TextBox>(window.FindName(name));

    private static TextBlock GetTextBlock(MainWindow window, string name) =>
        Assert.IsType<TextBlock>(window.FindName(name));

    private static ComboBox GetComboBox(MainWindow window, string name) =>
        Assert.IsType<ComboBox>(window.FindName(name));

    private static Button GetButton(MainWindow window, string name) =>
        Assert.IsType<Button>(window.FindName(name));

    private static StackPanel GetPanel(MainWindow window, string name) =>
        Assert.IsType<StackPanel>(window.FindName(name));

    private sealed class TestAudioDeviceCatalog : IAudioDeviceCatalog
    {
        public Task<IReadOnlyList<AudioDeviceOption>> GetOutputDevicesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AudioDeviceOption> devices =
            [
                new AudioDeviceOption(AudioDeviceOption.DefaultDeviceId, "Default"),
                new AudioDeviceOption("test-device", "Test device"),
            ];
            return Task.FromResult(devices);
        }
    }

    private sealed class TestAudioApplicationCatalog(AudioApplicationOption application)
        : IAudioApplicationCatalog
    {
        public Task<IReadOnlyList<AudioApplicationOption>> GetAudioApplicationsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AudioApplicationOption>>([application]);
        }
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets;
        private readonly IReadOnlyDictionary<string, Exception> _readFailures;

        public RecordingSecretStore(
            IDictionary<string, string>? initialSecrets = null,
            IReadOnlyDictionary<string, Exception>? readFailures = null)
        {
            _secrets = initialSecrets is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(initialSecrets, StringComparer.Ordinal);
            _readFailures = readFailures ?? new Dictionary<string, Exception>(StringComparer.Ordinal);
        }

        public List<SecretMutation> Mutations { get; } = [];

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _readFailures.TryGetValue(name, out var exception)
                ? Task.FromException<string?>(exception)
                : Task.FromResult(_secrets.GetValueOrDefault(name));
        }

        public Task SetSecretAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets[name] = value;
            Mutations.Add(new SecretMutation(SecretMutationKind.Set, name, value));
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets.Remove(name);
            Mutations.Add(new SecretMutation(SecretMutationKind.Delete, name, null));
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingSettingsStore : IAppSettingsStore
    {
        private readonly TaskCompletionSource _releaseFirstSave =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SaveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SaveCallCount { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AppSettings());
        }

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settings);
            cancellationToken.ThrowIfCancellationRequested();
            SaveCallCount++;
            SaveEntered.TrySetResult();
            if (SaveCallCount == 1)
            {
                await _releaseFirstSave.Task.WaitAsync(cancellationToken);
            }
        }

        public void ReleaseFirstSave() => _releaseFirstSave.TrySetResult();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MirrorPowerAI.Tests", Guid.NewGuid().ToString("N"));
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

    private sealed record SecretMutation(SecretMutationKind Kind, string Name, string? Value);

    private enum SecretMutationKind
    {
        Set,
        Delete,
    }
}

[CollectionDefinition(nameof(WpfSettingsWindowSerialTestSuite), DisableParallelization = true)]
public sealed class WpfSettingsWindowSerialTestSuite;

internal static class StaDispatcher
{
    public static Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                _ = dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await action();
                        completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                    finally
                    {
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                });
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
