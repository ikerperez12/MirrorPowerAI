using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Windows;
using MirrorPowerAI.Core.Gemini;
using MirrorPowerAI.Core.Models;
using MirrorPowerAI.Windows.Audio;
using MirrorPowerAI.Windows.Diagnostics;
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
    private static readonly TimeSpan SettingsSaveExitTimeout = TimeSpan.FromSeconds(15);
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
    private GeminiAudioConsentGate? _geminiAudioConsentGate;
    private readonly SemaphoreSlim _privacyTransitionGate = new(1, 1);
    private int _toggleInProgress;
    private int _privacyTransitionsPending;
    private int _resourcesDisposed;
    private int _exitInProgress;
    private string? _lastDisplayedAnswer;
    private string? _lastShownSessionStatus;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var diagnosticInvocation = DiagnosticCommandLine.Parse(e.Args);
        if (diagnosticInvocation.Kind == DiagnosticKind.Invalid)
        {
            Shutdown(1);
            return;
        }

        if (diagnosticInvocation.Kind == DiagnosticKind.Overlay)
        {
            VerifyOverlayProtectionAndExit();
            return;
        }

        if (diagnosticInvocation.Kind == DiagnosticKind.Wasapi)
        {
            VerifyWasapiLoopbackAndExit(diagnosticInvocation.RequireAudibleSignal);
            return;
        }

        if (diagnosticInvocation.Kind == DiagnosticKind.Shell)
        {
            VerifyShellAndExit();
            return;
        }

        if (diagnosticInvocation.Kind == DiagnosticKind.Ui)
        {
            VerifyUiAndExit();
            return;
        }

        if (diagnosticInvocation.Kind == DiagnosticKind.WhisperRuntime)
        {
            VerifyWhisperRuntimeAndExit();
            return;
        }

        var localization = LocalizationService.Current;
        try
        {
            InitializeNormalApplication(localization);
        }
        catch (Exception)
        {
            StartupFailureRecovery.Handle(
                DisposeResources,
                () => System.Windows.MessageBox.Show(
                    localization["StartupFailedMessage"],
                    localization["StartupFailedTitle"],
                    MessageBoxButton.OK,
                    MessageBoxImage.Error),
                () => Shutdown(1));
        }
    }

    private void InitializeNormalApplication(LocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
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
        var geminiAudioConsentGate = new GeminiAudioConsentGate();
        _geminiAudioConsentGate = geminiAudioConsentGate;
        var audioDevices = new NAudioDeviceCatalog(
            new NAudioEndpointProvider(),
            localization["DefaultAudioDevice"]);
        var audioApplications = new NAudioApplicationCatalog();

        _settingsWindow = new MainWindow(
            settingsStore,
            secretStore,
            audioDevices,
            localization,
            geminiAudioConsentGate,
            audioApplications);
        _settingsWindow.SettingsSaved += OnSettingsSaved;
        _overlayPresenter = new OverlayPresenter(new OverlayProtectionService());
        _overlayPresenter.StopRequested += OnToggleRequested;
        _sessionCommands = CreateSessionCommands(
            settingsStore,
            secretStore,
            initialSettings,
            geminiAudioConsentGate);
        _sessionCommands.StateChanged += OnSessionStateChanged;
        var trayIcon = new TrayIconService(localization);
        _trayIcon = trayIcon;
        trayIcon.ToggleRequested += OnToggleRequested;
        trayIcon.ShowResponseRequested += OnShowResponseRequested;
        trayIcon.SettingsRequested += OnSettingsRequested;
        trayIcon.ExitRequested += OnExitRequested;
        trayIcon.SetState(_sessionCommands.Snapshot.Activity, _sessionCommands.Snapshot.HasResult);

        _hotKey = new GlobalHotKeyService();
        _hotKey.Pressed += OnToggleRequested;
        StartupNotificationPolicy.Publish(
            _hotKey.Registration.IsRegistered,
            dpiResult.IsUsable,
            new LocalizedTrayStartupNotificationSink(trayIcon, resourceKey => localization[resourceKey]));

        // A visible configuration window makes first use discoverable. Closing it still leaves the
        // application in the tray, preserving the lightweight shell after onboarding.
        _ = Dispatcher.BeginInvoke(new Action(() => OnSettingsRequested(this, EventArgs.Empty)));
    }

    private void VerifyOverlayProtectionAndExit()
    {
        if (!IsInteractiveLocalDiagnosticSession())
        {
            Shutdown(1);
            return;
        }

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

    private void VerifyWasapiLoopbackAndExit(bool requireAudibleSignal)
    {
        if (!IsInteractiveLocalDiagnosticSession())
        {
            Shutdown(1);
            return;
        }

        var exitCode = 1;
        try
        {
            var result = new WasapiLoopbackDiagnostic()
                .VerifyAsync(
                    new WasapiLoopbackAudioCaptureService(),
                    WasapiLoopbackDiagnostic.DefaultCaptureDuration,
                    requireAudibleSignal)
                .GetAwaiter()
                .GetResult();
            exitCode = result.IsSuccessful ? 0 : 2;
        }
        catch (Exception)
        {
            // This diagnostic deliberately returns only an exit code and never exposes endpoint or audio data.
        }

        Shutdown(exitCode);
    }

    private void VerifyShellAndExit()
    {
        if (!IsInteractiveLocalDiagnosticSession())
        {
            Shutdown(1);
            return;
        }

        // NotifyIcon creates WinForms message resources. Run after WPF has entered its dispatcher loop so
        // both creation and disposal receive their normal native message processing, while normal startup
        // remains skipped entirely.
        _ = Dispatcher.BeginInvoke(RunShellDiagnosticAndExit);
    }

    private void VerifyUiAndExit()
    {
        if (!IsInteractiveLocalDiagnosticSession())
        {
            Shutdown(1);
            return;
        }

        // The diagnostic must enter the WPF dispatcher loop before showing either window. It never
        // constructs normal-startup services, so no settings, DPAPI, audio, model, network, or session
        // resource exists on this route.
        _ = Dispatcher.BeginInvoke(new Action(() => _ = RunUiDiagnosticAndExitAsync()));
    }

    private void VerifyWhisperRuntimeAndExit()
    {
        var exitCode = 1;
        try
        {
            exitCode = WhisperRuntimeDiagnostic.Verify() ? 0 : 1;
        }
        catch (Exception)
        {
            // Native loader details may contain machine paths. The diagnostic exposes only its exit code.
        }

        Shutdown(exitCode);
    }

    private void RunShellDiagnosticAndExit()
    {
        var exitCode = 1;
        try
        {
            exitCode = new ShellDiagnostic().Verify().IsSuccessful ? 0 : 1;
        }
        catch (Exception)
        {
            // This diagnostic intentionally returns only a pass/fail exit code and never emits shell details.
        }

        Shutdown(exitCode);
    }

    private async Task RunUiDiagnosticAndExitAsync()
    {
        var exitCode = 1;
        try
        {
            exitCode = (await UiDiagnostic.VerifyAsync()).ToProcessExitCode();
        }
        catch (Exception)
        {
            // This diagnostic intentionally returns only a pass/fail exit code and never emits UI data.
        }

        Shutdown(exitCode);
    }

    private static bool IsInteractiveLocalDiagnosticSession() =>
        Environment.UserInteractive
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CI"));

    /// <summary>
    /// Creates the long-lived Gemini and Whisper dependencies plus the per-session Core adapter.
    /// </summary>
    /// <param name="settingsStore">Bounded non-secret settings storage.</param>
    /// <param name="secretStore">DPAPI-protected key and context storage.</param>
    /// <param name="initialSettings">Normalized non-secret settings loaded during startup.</param>
    /// <param name="geminiAudioConsentGate">Shared process-level fail-closed Gemini Audio privacy barrier.</param>
    /// <returns>A session command adapter.</returns>
    private CoreSessionCommands CreateSessionCommands(
        IAppSettingsStore settingsStore,
        DpapiSecretStore secretStore,
        AppSettings initialSettings,
        IGeminiAudioConsentGate geminiAudioConsentGate)
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
            CreateGeminiClientOptions(initialSettings));
        return new CoreSessionCommands(
            settingsStore,
            secretStore,
            localTranscription,
            geminiClient,
            geminiAudioConsentGate);
    }

    /// <summary>
    /// Maps normalized persisted settings to the bounded Gemini client options used at application startup.
    /// </summary>
    /// <remarks>
    /// The Gemini client is deliberately long-lived. An internal model selection is therefore applied on
    /// the next application startup, while ordinary settings saves preserve that value without exposing it
    /// in the UI.
    /// </remarks>
    /// <param name="settings">The untrusted persisted settings to normalize.</param>
    /// <returns>Options that keep the official Gemini endpoint fixed and select only a valid model identifier.</returns>
    internal static GeminiClientOptions CreateGeminiClientOptions(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new GeminiClientOptions
        {
            Model = settings.Normalize().GeminiModel,
        };
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

        if (Interlocked.Exchange(ref _toggleInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            if (_sessionCommands.Snapshot.Activity is ShellActivityState.Idle or ShellActivityState.Paused or ShellActivityState.Error)
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
        _trayIcon?.SetStateAndNotify(snapshot.Activity, snapshot.HasResult);
        var allowResultDisplay = snapshot.Activity is not (ShellActivityState.Paused or ShellActivityState.Error);
        if (snapshot.Activity == ShellActivityState.Capturing)
        {
            if (!snapshot.HasResult)
            {
                // A fresh session has no previous answer. Drop the deduplication marker so an
                // identical answer from a later session is still shown to the user.
                _lastDisplayedAnswer = null;
                ShowProtectedSessionStatus(
                    LocalizationService.Current[
                        snapshot.AudioSignalDetected
                            ? "OverlayStatusAudioDetected"
                            : "OverlayStatusListening"],
                    isBusy: true,
                    showStopAction: true);
            }
            // When a segment is being analyzed, the controller still carries the last successful
            // result. Keep that protected answer visible over the meeting instead of replacing it
            // with a rapidly toggling status panel on every segment.
        }
        else if (snapshot.Activity == ShellActivityState.Processing)
        {
            // Transcription is intentionally a transient internal state. The capture overlay stays
            // on the stable listening message while each short segment is analyzed, preventing a
            // visible Capturing↔Processing flicker. The tray status still exposes Processing.
            if (!snapshot.HasResult &&
                (_lastShownSessionStatus is null || _overlayPresenter?.IsVisible != true))
            {
                ShowProtectedSessionStatus(
                    string.IsNullOrWhiteSpace(snapshot.UserMessage)
                        ? LocalizationService.Current["OverlayStatusProcessing"]
                        : LocalizeSessionMessage(snapshot.UserMessage),
                    isBusy: true,
                    showStopAction: string.IsNullOrWhiteSpace(snapshot.UserMessage));
            }
        }
        else if (snapshot.Activity == ShellActivityState.Paused)
        {
            // A paused session is an explicit user-visible boundary. Hide the previous answer in
            // favour of the paused indicator, but restore it automatically when listening resumes.
            _lastDisplayedAnswer = null;
            ShowProtectedSessionStatus(
                LocalizationService.Current["OverlayStatusPaused"],
                isBusy: false,
                showStopAction: false);
        }
        else if (snapshot.Activity == ShellActivityState.Error)
        {
            _lastDisplayedAnswer = null;
            var errorMessage = string.IsNullOrWhiteSpace(snapshot.UserMessage)
                ? LocalizationService.Current["UnexpectedError"]
                : LocalizeSessionMessage(snapshot.UserMessage);
            ShowProtectedSessionStatus(errorMessage, isBusy: false, showStopAction: false);
        }
        else if (!snapshot.HasResult)
        {
            _overlayPresenter?.Close();
        }

        if (snapshot.Activity == ShellActivityState.Error && !string.IsNullOrWhiteSpace(snapshot.UserMessage))
        {
            _trayIcon?.ShowError(LocalizeSessionMessage(snapshot.UserMessage));
        }

        if (allowResultDisplay &&
            snapshot.HasResult &&
            !string.Equals(snapshot.Answer, _lastDisplayedAnswer, StringComparison.Ordinal))
        {
            // Answers discovered during an active meeting are passive. The overlay is protected,
            // topmost, and readable, but it must not activate or focus itself over Teams, a
            // browser, or Discord. The tray's explicit “show response” command opts into focus.
            ShowProtectedResponse(snapshot, activate: false);
        }
    }

    private void ShowProtectedSessionStatus(string status, bool isBusy, bool showStopAction)
    {
        if (_overlayPresenter is null)
        {
            return;
        }

        if (string.Equals(status, _lastShownSessionStatus, StringComparison.Ordinal) &&
            _overlayPresenter.IsVisible)
        {
            return;
        }

        var result = _overlayPresenter.TryShowStatus(status, isBusy, showStopAction);
        if (!result.WasShown)
        {
            _trayIcon?.ShowError(LocalizationService.Current["OverlayProtectionFailed"]);
            _lastShownSessionStatus = null;
            return;
        }

        _lastShownSessionStatus = status;
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

        ShowProtectedResponse(snapshot, activate: true);
    }

    private void ShowProtectedResponse(SessionSnapshot snapshot, bool activate)
    {
        if (_overlayPresenter is null || string.IsNullOrWhiteSpace(snapshot.Answer))
        {
            return;
        }

        var result = _overlayPresenter.TryShow(snapshot.Question, snapshot.Answer, activate);
        if (!result.WasShown)
        {
            _lastDisplayedAnswer = null;
            _trayIcon?.ShowError(LocalizationService.Current["OverlayProtectionFailed"]);
            return;
        }

        _lastShownSessionStatus = null;
        _lastDisplayedAnswer = snapshot.Answer;
    }

    private async void OnSettingsRequested(object? sender, EventArgs eventArgs)
    {
        if (_settingsWindow is null)
        {
            return;
        }

        if (_settingsWindow.IsSaving)
        {
            BestEffortCleanup.Run(
                () => _settingsWindow.Activate(),
                () => _trayIcon?.ShowInformation(LocalizationService.Current["SettingsSaveInProgress"]));
            return;
        }

        _overlayPresenter?.Close();
        _lastDisplayedAnswer = null;
        _lastShownSessionStatus = null;

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
        _lastShownSessionStatus = null;

        try
        {
            await RunPrivacyTransitionAsync(async cancellationToken =>
            {
                if (_sessionCommands is not null)
                {
                    await _sessionCommands.ResetAsync(cancellationToken);
                }

                _trayIcon?.SetStateAndNotify(ShellActivityState.Idle, hasResponse: false);
                // "Guardar e iniciar" performs the same authenticated preflight inside ToggleAsync.
                // Avoid two consecutive model lookups on the latency-critical startup path. The
                // standalone verification button still reports its result in this window.
                var verifyWithoutStarting = eventArgs.VerifyApiKey && !eventArgs.StartAfterSave;
                var verificationSucceeded = !verifyWithoutStarting;
                if (verifyWithoutStarting && _sessionCommands is not null)
                {
                    _settingsWindow?.ShowApiKeyVerificationStatus(
                        LocalizationService.Current["ApiKeyVerificationInProgress"],
                        isError: false);
                    try
                    {
                        await _sessionCommands.VerifyApiKeyAsync(cancellationToken);
                        verificationSucceeded = true;
                        _settingsWindow?.ShowApiKeyVerificationStatus(
                            LocalizationService.Current["ApiKeyVerificationSuccess"],
                            isError: false);
                    }
                    catch (GeminiApiException exception)
                    {
                        _settingsWindow?.ShowApiKeyVerificationStatus(
                            LocalizeApiKeyVerificationFailure(exception),
                            isError: true);
                    }
                    catch (Exception)
                    {
                        _settingsWindow?.ShowApiKeyVerificationStatus(
                            LocalizationService.Current["ApiKeyVerificationFailed"],
                            isError: true);
                    }
                }

                if (eventArgs.StartAfterSave && verificationSucceeded && _sessionCommands is not null)
                {
                    _settingsWindow?.Hide();
                    await _sessionCommands.ToggleAsync(cancellationToken);
                }
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

    private static string LocalizeApiKeyVerificationFailure(GeminiApiException exception) =>
        exception.Kind switch
        {
            GeminiErrorKind.MissingApiKey => LocalizeSessionMessage("SessionApiKeyRequired"),
            GeminiErrorKind.Unauthorized => LocalizationService.Current["ApiKeyVerificationUnauthorized"],
            GeminiErrorKind.RateLimited => LocalizationService.Current["ApiKeyVerificationRateLimited"],
            GeminiErrorKind.Timeout or GeminiErrorKind.ServiceUnavailable or GeminiErrorKind.HttpError =>
                LocalizationService.Current["ApiKeyVerificationNetwork"],
            _ => LocalizationService.Current["ApiKeyVerificationFailed"],
        };

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
        if (Interlocked.Exchange(ref _exitInProgress, 1) != 0)
        {
            return;
        }

        var mayRetryExit = true;
        try
        {
            if (_settingsWindow is not null &&
                !await _settingsWindow.TryPrepareForApplicationExitAsync(
                    SettingsSaveExitTimeout,
                    _lifetimeCancellation.Token))
            {
                BestEffortCleanup.Run(
                    () => _trayIcon?.ShowError(LocalizationService.Current["SettingsSaveInProgress"]));
                return;
            }

            mayRetryExit = false;
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
            mayRetryExit = false;
        }
        catch (Exception)
        {
            mayRetryExit = false;
            // Shutdown still proceeds; DisposeResources performs a final best-effort cancellation.
        }
        finally
        {
            if (mayRetryExit)
            {
                Interlocked.Exchange(ref _exitInProgress, 0);
            }
        }

        if (!mayRetryExit)
        {
            Shutdown();
        }
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
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        BestEffortCleanup.Run(
            () => _lifetimeCancellation.Cancel(),
            () =>
            {
                if (_overlayPresenter is not null)
                {
                    _overlayPresenter.StopRequested -= OnToggleRequested;
                }
            },
            () => _overlayPresenter?.Close(),
            () =>
            {
                if (_settingsWindow is not null)
                {
                    _settingsWindow.SettingsSaved -= OnSettingsSaved;
                }
            },
            () => _settingsWindow?.CloseForApplicationExit(),
            () =>
            {
                if (_sessionCommands is not null)
                {
                    _sessionCommands.StateChanged -= OnSessionStateChanged;
                }
            },
            () =>
            {
                if (_sessionCommands is IAsyncDisposable asyncDisposable)
                {
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            },
            () =>
            {
                if (_hotKey is not null)
                {
                    _hotKey.Pressed -= OnToggleRequested;
                }
            },
            () => _hotKey?.Dispose(),
            () =>
            {
                if (_trayIcon is not null)
                {
                    _trayIcon.ToggleRequested -= OnToggleRequested;
                }
            },
            () =>
            {
                if (_trayIcon is not null)
                {
                    _trayIcon.ShowResponseRequested -= OnShowResponseRequested;
                }
            },
            () =>
            {
                if (_trayIcon is not null)
                {
                    _trayIcon.SettingsRequested -= OnSettingsRequested;
                }
            },
            () =>
            {
                if (_trayIcon is not null)
                {
                    _trayIcon.ExitRequested -= OnExitRequested;
                }
            },
            () => _trayIcon?.Dispose(),
            () => _singleInstance?.Dispose(),
            () => _modelManager?.Dispose(),
            () => _modelHttpClient?.Dispose(),
            () => _geminiHttpClient?.Dispose(),
            () => _geminiAudioConsentGate?.Dispose(),
            () => _lifetimeCancellation.Dispose(),
            () =>
            {
                if (Volatile.Read(ref _privacyTransitionsPending) == 0)
                {
                    _privacyTransitionGate.Dispose();
                }
            });
    }
}
