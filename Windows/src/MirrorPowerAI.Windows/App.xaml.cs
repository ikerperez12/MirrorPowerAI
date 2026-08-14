using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Windows;
using MirrorPowerAI.Core.Gemini;
using MirrorPowerAI.Core.Models;
using MirrorPowerAI.Windows.Audio;
using MirrorPowerAI.Windows.Platform;
using MirrorPowerAI.Windows.Resources;
using MirrorPowerAI.Windows.Shell;
using MirrorPowerAI.Windows.Transcription;
using MirrorPowerAI.Windows.UI;

namespace MirrorPowerAI.Windows;

/// <summary>
/// Composes the Windows shell and owns its explicit tray-application lifetime.
/// </summary>
public partial class App : System.Windows.Application, IDisposable
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private SingleInstanceGuard? _singleInstance;
    private TrayIconService? _trayIcon;
    private GlobalHotKeyService? _hotKey;
    private OverlayPresenter? _overlayPresenter;
    private MainWindow? _settingsWindow;
    [SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible",
        Justification = "ISessionCommands is the deliberate test and platform boundary around the Core controller.")]
    private ISessionCommands? _sessionCommands;
    private HttpClient? _geminiHttpClient;
    private HttpClient? _modelHttpClient;
    private WhisperModelManager? _modelManager;
    private readonly SemaphoreSlim _privacyTransitionGate = new(1, 1);
    private int _toggleInProgress;
    private int _privacyTransitionsPending;
    private bool _resourcesDisposed;
    private string? _lastDisplayedAnswer;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(static argument => string.Equals(
                argument,
                "--verify-overlay",
                StringComparison.Ordinal)))
        {
            VerifyOverlayProtectionAndExit();
            return;
        }

        var localization = LocalizationService.Current;
        var dpiResult = DpiAwareness.TryEnablePerMonitorV2();

        if (!SingleInstanceGuard.TryAcquire(out _singleInstance))
        {
            System.Windows.MessageBox.Show(
                localization["AlreadyRunningMessage"],
                localization["AlreadyRunningTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var settingsStore = new JsonSettingsStore();
        var initialSettings = settingsStore.LoadAsync().GetAwaiter().GetResult();
        localization.SetLanguage(initialSettings.Language);
        var secretStore = new DpapiSecretStore();
        var audioDevices = new NAudioDeviceCatalog(
            new NAudioEndpointProvider(),
            localization["DefaultAudioDevice"]);

        _settingsWindow = new MainWindow(settingsStore, secretStore, audioDevices, localization);
        _settingsWindow.SettingsSaved += OnSettingsSaved;
        _overlayPresenter = new OverlayPresenter(new OverlayProtectionService());
        _sessionCommands = CreateSessionCommands(settingsStore, secretStore);
        _sessionCommands.StateChanged += OnSessionStateChanged;
        _trayIcon = new TrayIconService(localization);
        _trayIcon.ToggleRequested += OnToggleRequested;
        _trayIcon.ShowResponseRequested += OnShowResponseRequested;
        _trayIcon.SettingsRequested += OnSettingsRequested;
        _trayIcon.ExitRequested += OnExitRequested;
        _trayIcon.SetState(_sessionCommands.Snapshot.Activity, _sessionCommands.Snapshot.HasResult);

        _hotKey = new GlobalHotKeyService();
        _hotKey.Pressed += OnToggleRequested;
        if (!_hotKey.Registration.IsRegistered)
        {
            _trayIcon.ShowError(localization["HotKeyUnavailable"]);
        }
        else
        {
            _trayIcon.ShowInformation(localization["TrayReady"]);
        }

        if (!dpiResult.IsUsable)
        {
            _trayIcon.ShowError(localization["OverlayProtectionFailed"]);
        }
    }

    private void VerifyOverlayProtectionAndExit()
    {
        var overlay = new OverlayWindow();
        var exitCode = 1;

        try
        {
            var protection = new OverlayProtectionService().ProtectAndVerify(overlay);
            exitCode = protection.IsProtected ? 0 : 1;
        }
        catch (Exception)
        {
            // This diagnostic intentionally returns only a pass/fail exit code and never emits window data.
        }
        finally
        {
            overlay.ClearSensitiveContent();
            overlay.Close();
        }

        Shutdown(exitCode);
    }

    /// <summary>
    /// Creates the long-lived Gemini and Whisper dependencies plus the per-session Core adapter.
    /// </summary>
    /// <param name="settingsStore">Bounded non-secret settings storage.</param>
    /// <param name="secretStore">DPAPI-protected key and context storage.</param>
    /// <returns>A session command adapter.</returns>
    private CoreSessionCommands CreateSessionCommands(JsonSettingsStore settingsStore, DpapiSecretStore secretStore)
    {
        _modelHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _modelManager = new WhisperModelManager(_modelHttpClient, WhisperModelDescriptor.DefaultBase);
        var modelDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MirrorPowerAI",
            "models");
        var localTranscription = new WhisperLocalTranscriptionService(_modelManager, modelDirectory);

        _geminiHttpClient = GeminiHttpClientFactory.Create();
        var apiKeyProvider = new SecretStoreGeminiApiKeyProvider(secretStore);
        var geminiClient = new GeminiClient(
            _geminiHttpClient,
            apiKeyProvider,
            new GeminiClientOptions { Model = "gemini-3.5-flash" });
        return new CoreSessionCommands(settingsStore, secretStore, localTranscription, geminiClient);
    }

    private async void OnToggleRequested(object? sender, EventArgs eventArgs)
    {
        if (_sessionCommands is null)
        {
            return;
        }

        if (Volatile.Read(ref _privacyTransitionsPending) != 0 || _settingsWindow?.IsSaving == true)
        {
            return;
        }

        if (_sessionCommands.Snapshot.Activity == ShellActivityState.Processing)
        {
            try
            {
                await _sessionCommands.CancelAsync(_lifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                _trayIcon?.ShowError(LocalizationService.Current["UnexpectedError"]);
            }

            return;
        }

        if (Interlocked.Exchange(ref _toggleInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            if (_sessionCommands.Snapshot.Activity is ShellActivityState.Idle or ShellActivityState.Error)
            {
                _settingsWindow?.Hide();
            }

            await _sessionCommands.ToggleAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _trayIcon?.ShowError(LocalizationService.Current["UnexpectedError"]);
        }
        finally
        {
            Interlocked.Exchange(ref _toggleInProgress, 0);
        }
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs eventArgs)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => ApplySessionSnapshot(eventArgs.Snapshot));
            return;
        }

        ApplySessionSnapshot(eventArgs.Snapshot);
    }

    private void ApplySessionSnapshot(SessionSnapshot snapshot)
    {
        _trayIcon?.SetState(snapshot.Activity, snapshot.HasResult);
        if (snapshot.Activity == ShellActivityState.Capturing)
        {
            _lastDisplayedAnswer = null;
            _overlayPresenter?.Close();
        }

        if (snapshot.Activity == ShellActivityState.Error && !string.IsNullOrWhiteSpace(snapshot.UserMessage))
        {
            _trayIcon?.ShowError(LocalizeSessionMessage(snapshot.UserMessage));
        }

        if (snapshot.HasResult && !string.Equals(snapshot.Answer, _lastDisplayedAnswer, StringComparison.Ordinal))
        {
            ShowProtectedResponse(snapshot);
        }
    }

    private static string LocalizeSessionMessage(string resourceKey)
    {
        var localized = LocalizationService.Current[resourceKey];
        return localized == $"[{resourceKey}]"
            ? LocalizationService.Current["UnexpectedError"]
            : localized;
    }

    private void OnShowResponseRequested(object? sender, EventArgs eventArgs)
    {
        if (_sessionCommands?.Snapshot is not { HasResult: true } snapshot)
        {
            _trayIcon?.ShowInformation(LocalizationService.Current["NoResponse"]);
            return;
        }

        ShowProtectedResponse(snapshot);
    }

    private void ShowProtectedResponse(SessionSnapshot snapshot)
    {
        if (_overlayPresenter is null || string.IsNullOrWhiteSpace(snapshot.Answer))
        {
            return;
        }

        var result = _overlayPresenter.TryShow(snapshot.Question, snapshot.Answer);
        if (!result.WasShown)
        {
            _lastDisplayedAnswer = null;
            _trayIcon?.ShowError(LocalizationService.Current["OverlayProtectionFailed"]);
            return;
        }

        _lastDisplayedAnswer = snapshot.Answer;
    }

    private async void OnSettingsRequested(object? sender, EventArgs eventArgs)
    {
        if (_settingsWindow is null)
        {
            return;
        }

        _overlayPresenter?.Close();
        _lastDisplayedAnswer = null;

        try
        {
            await RunPrivacyTransitionAsync(async cancellationToken =>
            {
                if (_sessionCommands is not null)
                {
                    await _sessionCommands.ResetAsync(cancellationToken);
                }

                await _settingsWindow.ReloadAsync(cancellationToken);
                if (!_settingsWindow.IsVisible)
                {
                    _settingsWindow.Show();
                }

                if (_settingsWindow.WindowState == WindowState.Minimized)
                {
                    _settingsWindow.WindowState = WindowState.Normal;
                }

                _settingsWindow.Activate();
            });
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _trayIcon?.ShowError(LocalizationService.Current["UnexpectedError"]);
        }
    }

    private async void OnSettingsSaved(object? sender, SettingsSavedEventArgs eventArgs)
    {
        _overlayPresenter?.Close();
        _lastDisplayedAnswer = null;

        try
        {
            await RunPrivacyTransitionAsync(async cancellationToken =>
            {
                if (_sessionCommands is not null)
                {
                    await _sessionCommands.ResetAsync(cancellationToken);
                }

                _trayIcon?.SetState(ShellActivityState.Idle, hasResponse: false);
            });
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _trayIcon?.ShowError(LocalizationService.Current["UnexpectedError"]);
        }
    }

    private async Task RunPrivacyTransitionAsync(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Interlocked.Increment(ref _privacyTransitionsPending);

        try
        {
            await _privacyTransitionGate.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                await action(_lifetimeCancellation.Token);
            }
            finally
            {
                _privacyTransitionGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _privacyTransitionsPending);
        }
    }

    private async void OnExitRequested(object? sender, EventArgs eventArgs)
    {
        try
        {
            await RunPrivacyTransitionAsync(async cancellationToken =>
            {
                if (_sessionCommands is not null)
                {
                    await _sessionCommands.ResetAsync(cancellationToken);
                }
            });
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Shutdown still proceeds; DisposeResources performs a final best-effort cancellation.
        }

        Shutdown();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        DisposeResources();
        base.OnExit(e);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    private void DisposeResources()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        _resourcesDisposed = true;
        _lifetimeCancellation.Cancel();
        _overlayPresenter?.Close();

        if (_settingsWindow is not null)
        {
            _settingsWindow.SettingsSaved -= OnSettingsSaved;
            _settingsWindow.CloseForApplicationExit();
        }

        if (_sessionCommands is not null)
        {
            _sessionCommands.StateChanged -= OnSessionStateChanged;
            if (_sessionCommands is IAsyncDisposable asyncDisposable)
            {
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        if (_hotKey is not null)
        {
            _hotKey.Pressed -= OnToggleRequested;
            _hotKey.Dispose();
        }

        if (_trayIcon is not null)
        {
            _trayIcon.ToggleRequested -= OnToggleRequested;
            _trayIcon.ShowResponseRequested -= OnShowResponseRequested;
            _trayIcon.SettingsRequested -= OnSettingsRequested;
            _trayIcon.ExitRequested -= OnExitRequested;
            _trayIcon.Dispose();
        }

        _singleInstance?.Dispose();
        _modelManager?.Dispose();
        _modelHttpClient?.Dispose();
        _geminiHttpClient?.Dispose();
        _lifetimeCancellation.Dispose();
        if (Volatile.Read(ref _privacyTransitionsPending) == 0)
        {
            _privacyTransitionGate.Dispose();
        }
    }
}
