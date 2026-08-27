using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Core.Privacy;
using MirrorPowerAI.Core.Security;
using MirrorPowerAI.Windows.Audio;
using MirrorPowerAI.Windows.Platform;
using MirrorPowerAI.Windows.Resources;
using Forms = System.Windows.Forms;

namespace MirrorPowerAI.Windows;

/// <summary>
/// Accessible per-user settings window for privacy, transcription, language, and output device.
/// </summary>
public partial class MainWindow : Window
{
    private const double DefaultDpi = 96;

    /// <summary>Current Gemini Audio consent wording revision.</summary>
    public const int CurrentGeminiAudioConsentVersion = GeminiAudioConsentPolicy.CurrentVersion;

    /// <summary>Stable secret-store key for the Gemini API key.</summary>
    public const string GeminiApiKeySecretName = "gemini-api-key";

    /// <summary>Stable secret-store key for the project context.</summary>
    public const string ProjectContextSecretName = "project-context";

    private readonly IAppSettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly IAudioDeviceCatalog _audioDeviceCatalog;
    private readonly IAudioApplicationCatalog _audioApplicationCatalog;
    private readonly LocalizationService _localization;
    private readonly IGeminiAudioConsentGate _geminiAudioConsentGate;
    private readonly bool _ownsGeminiAudioConsentGate;
    private bool _allowClose;
    private bool _isLoading;
    private bool _applicationExitRequested;
    private bool _apiKeyWasRead;
    private bool _contextWasRead;
    private bool _statusAnnouncementPending;
    private bool _statusAnnouncementScheduled;
    private bool _initialFocusScheduled;
    private bool _initialFocusPending = true;
    private bool _isPositioningBeforeShow;
    private bool _persistedSettingsLoaded;
    private TaskCompletionSource? _activeSaveCompletion;
    private AppSettings _persistedSettings = new();

    /// <summary>Gets whether protected and non-secret settings are being persisted.</summary>
    public bool IsSaving { get; private set; }

    /// <summary>Initializes the settings window with testable platform services.</summary>
    /// <param name="settingsStore">Non-secret JSON settings store.</param>
    /// <param name="secretStore">DPAPI-backed secret store.</param>
    /// <param name="audioDeviceCatalog">Available output-device source.</param>
    /// <param name="localization">Live localized string source.</param>
    /// <param name="geminiAudioConsentGate">Shared process-level fail-closed Gemini Audio privacy barrier.</param>
    /// <param name="audioApplicationCatalog">Running applications with render-audio sessions.</param>
    public MainWindow(
        IAppSettingsStore settingsStore,
        ISecretStore secretStore,
        IAudioDeviceCatalog audioDeviceCatalog,
        LocalizationService localization,
        IGeminiAudioConsentGate? geminiAudioConsentGate = null,
        IAudioApplicationCatalog? audioApplicationCatalog = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _audioDeviceCatalog = audioDeviceCatalog ?? throw new ArgumentNullException(nameof(audioDeviceCatalog));
        _audioApplicationCatalog = audioApplicationCatalog ?? new EmptyAudioApplicationCatalog();
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _geminiAudioConsentGate = geminiAudioConsentGate ?? new GeminiAudioConsentGate();
        _ownsGeminiAudioConsentGate = geminiAudioConsentGate is null;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        Activated += OnWindowActivated;
    }

    /// <summary>Raised after both protected and non-secret settings are saved.</summary>
    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

    /// <summary>
    /// Shows the settings window after bounding it on the monitor currently under the pointer.
    /// </summary>
    /// <remarks>
    /// The window is hidden, not destroyed, when a user dismisses it. Recomputing placement for each
    /// call therefore prevents a later tray-menu invocation on another monitor from reopening it off-screen.
    /// </remarks>
    public new void Show()
    {
        _initialFocusPending = true;
        PositionBeforeShow();
        base.Show();
        ScheduleInitialFocus();
    }

    /// <summary>
    /// Raised after a visible settings status was submitted to UI Automation. The event contains no
    /// message, so tests can verify announcement timing without observing protected settings.
    /// </summary>
    internal event EventHandler? StatusAnnouncementRaised;

    /// <summary>Reloads all fields from per-user storage before the window is shown.</summary>
    /// <param name="cancellationToken">Cancels storage and device enumeration.</param>
    /// <returns>A task that completes when controls contain the saved values.</returns>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _isLoading = true;
        HideStatus();

        try
        {
            var persistedSettings = await _settingsStore.LoadAsync(cancellationToken);
            var devices = await GetOutputDevicesSafelyAsync(cancellationToken);
            var applications = await GetAudioApplicationsSafelyAsync(cancellationToken);
            var apiKey = await ReadSecretSafelyAsync(GeminiApiKeySecretName, cancellationToken);
            var context = await ReadSecretSafelyAsync(ProjectContextSecretName, cancellationToken);
            _apiKeyWasRead = apiKey.WasRead;
            _contextWasRead = context.WasRead;
            _persistedSettings = persistedSettings.Normalize();
            _persistedSettingsLoaded = true;
            var settings = (_persistedSettings with { Context = context.Value ?? string.Empty }).Normalize();
            ApiKeyBox.Password = apiKey.Value ?? string.Empty;
            ContextBox.Text = settings.Context;
            PopulateProviderOptions(settings.TranscriptionProvider);
            PopulateLanguageOptions(settings.Language);
            PopulateDeviceOptions(devices, settings.AudioDeviceId);
            PopulateAudioSourceOptions(settings.AudioCaptureSource);
            PopulateApplicationOptions(
                applications,
                settings.AudioProcessName,
                settings.AudioProcessId);
            var persistedConsent = CreatePersistedGeminiAudioConsent(settings);
            var hasEffectiveGeminiAudioConsent =
                _geminiAudioConsentGate.GetEffectiveConsent(persistedConsent) is not null;
            CloudConsentBox.IsChecked = hasEffectiveGeminiAudioConsent;
            UpdateConsentVisibility();
            UpdateAudioSourceVisibility();

            if (!apiKey.WasRead || !context.WasRead)
            {
                ShowStatus(_localization["SettingsLoadError"], isError: true);
            }
            else if (string.IsNullOrWhiteSpace(apiKey.Value))
            {
                ShowStatus(_localization["ApiKeyRequired"], isError: true);
            }
            else if (settings.TranscriptionProvider == TranscriptionProviders.GeminiAudio &&
                     !hasEffectiveGeminiAudioConsent)
            {
                ShowStatus(_localization["GeminiAudioReauthorizationRequired"], isError: false);
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task<SecretReadResult> ReadSecretSafelyAsync(
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            return new SecretReadResult(
                await _secretStore.GetSecretAsync(name, cancellationToken),
                WasRead: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return new SecretReadResult(null, WasRead: false);
        }
    }

    private static GeminiAudioConsent? CreatePersistedGeminiAudioConsent(AppSettings settings) =>
        settings.GeminiAudioConsentVersion == CurrentGeminiAudioConsentVersion &&
        settings.GeminiAudioConsentGrantedAtUtc is DateTimeOffset grantedAtUtc
            ? new GeminiAudioConsent(settings.GeminiAudioConsentVersion, grantedAtUtc)
            : null;

    private async Task<IReadOnlyList<AudioDeviceOption>> GetOutputDevicesSafelyAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _audioDeviceCatalog.GetOutputDevicesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is COMException or AudioCaptureException)
        {
            return [new AudioDeviceOption(
                AudioDeviceOption.DefaultDeviceId,
                _localization["DefaultAudioDevice"])];
        }
    }

    private async Task<IReadOnlyList<AudioApplicationOption>> GetAudioApplicationsSafelyAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _audioApplicationCatalog.GetAudioApplicationsAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is COMException or AudioCaptureException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Allows the application shutdown path to close rather than hide this window.</summary>
    public void CloseForApplicationExit()
    {
        _applicationExitRequested = true;
        _allowClose = true;
        Close();
    }

    /// <summary>
    /// Prevents another save from starting and waits a bounded interval for the current atomic save
    /// to finish before application-owned dependencies are disposed.
    /// </summary>
    /// <param name="timeout">Maximum interval allowed for an already active save.</param>
    /// <param name="cancellationToken">Cancels the exit attempt without cancelling the save itself.</param>
    /// <returns><see langword="true"/> when shutdown may proceed safely; otherwise <see langword="false"/>.</returns>
    internal async Task<bool> TryPrepareForApplicationExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        _applicationExitRequested = true;
        var activeSave = _activeSaveCompletion?.Task;
        if (activeSave is null)
        {
            return true;
        }

        try
        {
            await activeSave.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            _applicationExitRequested = false;
            return false;
        }
        catch (OperationCanceledException)
        {
            _applicationExitRequested = false;
            throw;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        // Calls that are statically typed as Window (such as local UI diagnostics) bypass the hidden
        // Show method. Source initialization still occurs before the window is rendered, so it is a
        // safe fallback for that path.
        PositionBeforeShow();
    }

    private void PositionBeforeShow()
    {
        if (_isPositioningBeforeShow)
        {
            return;
        }

        _isPositioningBeforeShow = true;
        try
        {
            var pointer = Forms.Cursor.Position;
            var screen = Forms.Screen.FromPoint(pointer);
            var workingArea = screen.WorkingArea;
            var dpiScale = GetDpiScale(pointer.X, pointer.Y);
            if (!SettingsWindowGeometry.TryCalculate(
                    new PhysicalWorkArea(
                        workingArea.Left,
                        workingArea.Top,
                        workingArea.Right,
                        workingArea.Bottom),
                    dpiScale,
                    out var placement))
            {
                return;
            }

            ApplyDpiScaledSizeConstraints(placement, dpiScale);
            // EnsureHandle raises SourceInitialized. The reentrancy guard makes that pre-show event a
            // no-op here, then lets this call place the real HWND in physical monitor coordinates.
            var windowHandle = new WindowInteropHelper(this).EnsureHandle();
            if (NativeMethods.SetWindowPos(
                    windowHandle,
                    nint.Zero,
                    placement.Left,
                    placement.Top,
                    placement.Width,
                    placement.Height,
                    NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate))
            {
                return;
            }

            ApplyWpfPlacementFallback(windowHandle, placement);
        }
        finally
        {
            _isPositioningBeforeShow = false;
        }
    }

    private void ApplyDpiScaledSizeConstraints(SettingsWindowPlacement placement, double dpiScale)
    {
        // Lower the existing XAML minima before applying maxima so a small working area never creates
        // an invalid MinWidth > MaxWidth relationship. The central ScrollViewer retains the form fields
        // while the heading and action buttons stay visible.
        MinWidth = placement.MinimumWidth / dpiScale;
        MinHeight = placement.MinimumHeight / dpiScale;
        MaxWidth = placement.MaximumWidth / dpiScale;
        MaxHeight = placement.MaximumHeight / dpiScale;
    }

    private void ApplyWpfPlacementFallback(nint windowHandle, SettingsWindowPlacement placement)
    {
        // Use the source's own device transform rather than dividing virtual-desktop coordinates by
        // the target monitor DPI. That remains correct when monitors have different scaling factors.
        var source = HwndSource.FromHwnd(windowHandle);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(placement.Left, placement.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(
            placement.Left + placement.Width,
            placement.Top + placement.Height));
        Left = topLeft.X;
        Top = topLeft.Y;
        Width = Math.Max(1, bottomRight.X - topLeft.X);
        Height = Math.Max(1, bottomRight.Y - topLeft.Y);
    }

    private static double GetDpiScale(int pointX, int pointY)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.NativePoint(pointX, pointY),
            NativeMethods.MonitorDefaultToNearest);
        if (monitor == nint.Zero ||
            NativeMethods.GetDpiForMonitor(
                monitor,
                NativeMethods.MonitorDpiTypeEffective,
                out var dpiX,
                out _) < 0 ||
            dpiX == 0)
        {
            return 1;
        }

        return Math.Clamp(dpiX / DefaultDpi, 0.5, 8);
    }

    private void PopulateProviderOptions(string selectedId)
    {
        ProviderBox.ItemsSource = new[]
        {
            new LocalizedOption(TranscriptionProviders.LocalWhisper, _localization["ProviderLocal"]),
            new LocalizedOption(TranscriptionProviders.GeminiAudio, _localization["ProviderGemini"]),
        };
        ProviderBox.SelectedValue = selectedId;
        if (ProviderBox.SelectedIndex < 0)
        {
            ProviderBox.SelectedIndex = 0;
        }
    }

    private void PopulateLanguageOptions(string selectedId)
    {
        LanguageBox.ItemsSource = new[]
        {
            new LocalizedOption("es", _localization["LanguageSpanish"]),
            new LocalizedOption("en", _localization["LanguageEnglish"]),
            new LocalizedOption("auto", _localization["LanguageAuto"]),
        };
        LanguageBox.SelectedValue = selectedId;
        if (LanguageBox.SelectedIndex < 0)
        {
            LanguageBox.SelectedIndex = 0;
        }
    }

    private void PopulateAudioSourceOptions(string selectedId)
    {
        CaptureSourceBox.ItemsSource = new[]
        {
            new LocalizedOption(AudioCaptureSources.SystemOutput, _localization["AudioSourceSystem"]),
            new LocalizedOption(AudioCaptureSources.Application, _localization["AudioSourceApplication"]),
        };
        CaptureSourceBox.SelectedValue = selectedId;
        if (CaptureSourceBox.SelectedIndex < 0)
        {
            CaptureSourceBox.SelectedIndex = 0;
        }
    }

    private void PopulateApplicationOptions(
        IReadOnlyList<AudioApplicationOption> applications,
        string selectedProcessName,
        int? selectedProcessId)
    {
        var options = applications.ToList();
        var selected = options.FirstOrDefault(application =>
                application.ProcessId == selectedProcessId &&
                string.Equals(
                    application.ProcessName,
                    selectedProcessName,
                    StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(application => string.Equals(
                application.ProcessName,
                selectedProcessName,
                StringComparison.OrdinalIgnoreCase));

        if (selected is null && !string.IsNullOrWhiteSpace(selectedProcessName))
        {
            selected = new AudioApplicationOption(
                selectedProcessId.GetValueOrDefault(),
                selectedProcessName,
                $"{_localization["AudioApplicationUnavailable"]} ({selectedProcessName}.exe)");
            options.Insert(0, selected);
        }

        if (options.Count == 0)
        {
            options.Add(new AudioApplicationOption(
                0,
                string.Empty,
                _localization["NoAudioApplications"]));
        }

        ApplicationBox.ItemsSource = options;
        ApplicationBox.SelectedItem = selected ?? options.FirstOrDefault();
    }

    private void PopulateDeviceOptions(IReadOnlyList<AudioDeviceOption> devices, string selectedId)
    {
        var options = devices
            .Select(device => device.Id == AudioDeviceOption.DefaultDeviceId
                ? device with { DisplayName = _localization["DefaultAudioDevice"] }
                : device)
            .ToList();
        if (options.All(device => device.Id != AudioDeviceOption.DefaultDeviceId))
        {
            options.Insert(0, new AudioDeviceOption(
                AudioDeviceOption.DefaultDeviceId,
                _localization["DefaultAudioDevice"]));
        }

        DeviceBox.ItemsSource = options;
        DeviceBox.SelectedValue = selectedId;
        if (DeviceBox.SelectedIndex < 0)
        {
            DeviceBox.SelectedIndex = 0;
        }
    }

    /// <summary>Persists the values currently edited in the settings controls.</summary>
    /// <remarks>
    /// Storage failures are presented in the window status region rather than propagated to callers.
    /// Successful saves raise <see cref="SettingsSaved"/>.
    /// </remarks>
    /// <returns>A task that completes after the save attempt has finished.</returns>
    public Task SaveAsync() => SaveAsyncCore(startAfterSave: false, verifyApiKey: false);

    private async Task<bool> SaveAsyncCore(bool startAfterSave, bool verifyApiKey)
    {
        if (IsSaving || _applicationExitRequested)
        {
            return false;
        }

        HideStatus();
        var provider = ProviderBox.SelectedValue as string ?? TranscriptionProviders.LocalWhisper;
        var audioCaptureSource = CaptureSourceBox.SelectedValue as string ?? AudioCaptureSources.SystemOutput;
        var selectedApplication = ApplicationBox.SelectedItem as AudioApplicationOption;
        if (audioCaptureSource == AudioCaptureSources.Application &&
            (selectedApplication is null ||
             selectedApplication.ProcessId <= 0 ||
             string.IsNullOrWhiteSpace(selectedApplication.ProcessName)))
        {
            ShowStatus(_localization["AudioApplicationRequired"], isError: true);
            ApplicationBox.Focus();
            return false;
        }

        var hasCloudConsent = CloudConsentBox.IsChecked == true;
        var hasExplicitCloudConsent = provider == TranscriptionProviders.GeminiAudio && hasCloudConsent;
        if (!hasExplicitCloudConsent)
        {
            // Revocation is effective before any further storage operation. If persistence subsequently
            // fails, existing and future in-process services must still be unable to upload audio.
            _geminiAudioConsentGate.Revoke();
        }

        if (provider == TranscriptionProviders.GeminiAudio && !hasCloudConsent)
        {
            ShowStatus(_localization["ConsentRequired"], isError: true);
            CloudConsentBox.Focus();
            return false;
        }

        SetSavingState(isSaving: true);
        ShowStatus(_localization["SettingsSaving"], isError: false);
        var saveCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeSaveCompletion = saveCompletion;
        try
        {
            // SaveAsync is public for keyboard/UI automation tests and callers. Preserve hidden,
            // non-secret configuration even when such a caller did not invoke ReloadAsync first.
            if (!_persistedSettingsLoaded)
            {
                _persistedSettings = (await _settingsStore.LoadAsync()).Normalize();
                _persistedSettingsLoaded = true;
            }

            var settings = new AppSettings
            {
                Context = ContextBox.Text,
                TranscriptionProvider = provider,
                Language = LanguageBox.SelectedValue as string ?? "es",
                AudioDeviceId = DeviceBox.SelectedValue as string ?? AudioDeviceOption.DefaultDeviceId,
                AudioCaptureSource = audioCaptureSource,
                AudioProcessName = audioCaptureSource == AudioCaptureSources.Application
                    ? selectedApplication!.ProcessName
                    : string.Empty,
                AudioProcessId = audioCaptureSource == AudioCaptureSources.Application
                    ? selectedApplication!.ProcessId
                    : null,
                GeminiModel = _persistedSettings.GeminiModel,
                GeminiAudioConsentVersion = provider == TranscriptionProviders.GeminiAudio && hasCloudConsent
                    ? CurrentGeminiAudioConsentVersion
                    : 0,
                GeminiAudioConsentGrantedAtUtc = provider == TranscriptionProviders.GeminiAudio && hasCloudConsent
                    ? DateTimeOffset.UtcNow
                    : null,
            }.Normalize();

            _apiKeyWasRead = await SecretWritePolicy.PersistAsync(
                _secretStore,
                GeminiApiKeySecretName,
                ApiKeyBox.Password,
                _apiKeyWasRead);
            _contextWasRead = await SecretWritePolicy.PersistAsync(
                _secretStore,
                ProjectContextSecretName,
                settings.Context,
                _contextWasRead);

            await _settingsStore.SaveAsync(settings);
            _persistedSettings = settings with { Context = string.Empty };
            _persistedSettingsLoaded = true;
            if (hasExplicitCloudConsent)
            {
                // Do not reopen the cloud path until every preceding storage mutation, including the
                // versioned consent in settings.json, has completed successfully.
                _geminiAudioConsentGate.AllowAfterSuccessfulExplicitConsentSave();
            }

            _localization.SetLanguage(settings.Language);
            var deviceOptions = (DeviceBox.ItemsSource as IEnumerable<AudioDeviceOption>)?.ToList() ?? [];
            var applicationOptions = (ApplicationBox.ItemsSource as IEnumerable<AudioApplicationOption>)?.ToList() ?? [];
            PopulateProviderOptions(settings.TranscriptionProvider);
            PopulateLanguageOptions(settings.Language);
            PopulateDeviceOptions(deviceOptions, settings.AudioDeviceId);
            PopulateAudioSourceOptions(settings.AudioCaptureSource);
            PopulateApplicationOptions(
                applicationOptions,
                settings.AudioProcessName,
                settings.AudioProcessId);
            UpdateAudioSourceVisibility();
            ShowStatus(_localization["SettingsSaved"], isError: false);
            SettingsSaved?.Invoke(this, new SettingsSavedEventArgs(settings, startAfterSave, verifyApiKey));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            ShowStatus(_localization["SettingsSaveError"], isError: true);
            return false;
        }
        finally
        {
            SetSavingState(isSaving: false);
            if (ReferenceEquals(_activeSaveCompletion, saveCompletion))
            {
                _activeSaveCompletion = null;
            }

            saveCompletion.TrySetResult();
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs eventArgs) => await SaveAsync();

    private async void OnSaveAndStartClick(object sender, RoutedEventArgs eventArgs) =>
        // Starting from onboarding must validate the persisted key first. The application never
        // opens a capture session that is guaranteed to fail at its first answer request.
        _ = await SaveAsyncCore(startAfterSave: true, verifyApiKey: true);

    private async void OnVerifyApiKeyClick(object sender, RoutedEventArgs eventArgs) =>
        _ = await SaveAsyncCore(startAfterSave: false, verifyApiKey: true);

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!IsSaving)
        {
            Hide();
        }
    }

    private void SetSavingState(bool isSaving)
    {
        IsSaving = isSaving;
        ApiKeyBox.IsEnabled = !isSaving;
        VerifyApiKeyButton.IsEnabled = !isSaving;
        ContextBox.IsEnabled = !isSaving;
        ProviderBox.IsEnabled = !isSaving;
        LanguageBox.IsEnabled = !isSaving;
        DeviceBox.IsEnabled = !isSaving;
        CaptureSourceBox.IsEnabled = !isSaving;
        ApplicationBox.IsEnabled = !isSaving;
        RefreshApplicationsButton.IsEnabled = !isSaving;
        CloudConsentBox.IsEnabled = !isSaving;
        SaveAndStartButtonControl.IsEnabled = !isSaving;
        SaveButtonControl.IsEnabled = !isSaving;
        CancelButtonControl.IsEnabled = !isSaving;
    }

    private void OnProviderSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_isLoading)
        {
            UpdateConsentVisibility();
        }
    }

    private void OnAudioSourceSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_isLoading)
        {
            UpdateAudioSourceVisibility();
        }
    }

    private async void OnRefreshApplicationsClick(object sender, RoutedEventArgs eventArgs)
    {
        if (IsSaving)
        {
            return;
        }

        var selected = ApplicationBox.SelectedItem as AudioApplicationOption;
        RefreshApplicationsButton.IsEnabled = false;
        try
        {
            var applications = await GetAudioApplicationsSafelyAsync(CancellationToken.None);
            PopulateApplicationOptions(
                applications,
                selected?.ProcessName ?? string.Empty,
                selected?.ProcessId);
        }
        finally
        {
            RefreshApplicationsButton.IsEnabled = true;
        }
    }

    private void UpdateConsentVisibility()
    {
        CloudConsentGroup.Visibility = ProviderBox.SelectedValue as string == TranscriptionProviders.GeminiAudio
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateAudioSourceVisibility()
    {
        var captureApplication = CaptureSourceBox.SelectedValue as string == AudioCaptureSources.Application;
        SystemOutputPanel.Visibility = captureApplication ? Visibility.Collapsed : Visibility.Visible;
        ApplicationOutputPanel.Visibility = captureApplication ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = isError ? $"⚠ {message}" : message;
        AutomationProperties.SetName(StatusText, StatusText.Text);
        StatusText.Visibility = Visibility.Visible;
        _statusAnnouncementPending = true;
        ScheduleStatusAnnouncement();
    }

    /// <summary>Shows the result of the explicit Gemini API-key verification workflow.</summary>
    internal void ShowApiKeyVerificationStatus(string message, bool isError) => ShowStatus(message, isError);

    private void OnContentRendered(object? sender, EventArgs eventArgs)
    {
        ScheduleStatusAnnouncement();
        ScheduleInitialFocus();
    }

    private void OnWindowActivated(object? sender, EventArgs eventArgs)
    {
        ScheduleStatusAnnouncement();
        ScheduleInitialFocus();
    }

    private void ScheduleInitialFocus()
    {
        if (!_initialFocusPending ||
            _initialFocusScheduled ||
            !IsVisible ||
            Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _initialFocusScheduled = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusInitialControlWhenVisible));
    }

    private void FocusInitialControlWhenVisible()
    {
        _initialFocusScheduled = false;
        if (IsVisible &&
            ApiKeyBox.IsVisible &&
            ApiKeyBox.IsEnabled)
        {
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(ApiKeyBox), ApiKeyBox);
            _ = ApiKeyBox.Focus();
            _initialFocusPending = false;
        }
    }

    private void ScheduleStatusAnnouncement()
    {
        if (!_statusAnnouncementPending ||
            _statusAnnouncementScheduled ||
            !IsVisible ||
            !StatusText.IsVisible ||
            Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _statusAnnouncementScheduled = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(AnnounceStatusWhenVisible));
    }

    private void AnnounceStatusWhenVisible()
    {
        _statusAnnouncementScheduled = false;
        if (!_statusAnnouncementPending ||
            !IsVisible ||
            !StatusText.IsVisible ||
            string.IsNullOrWhiteSpace(StatusText.Text))
        {
            return;
        }

        var peer = UIElementAutomationPeer.CreatePeerForElement(StatusText)
            ?? new TextBlockAutomationPeer(StatusText);
        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _statusAnnouncementPending = false;
        StatusAnnouncementRaised?.Invoke(this, EventArgs.Empty);
    }

    private void HideStatus()
    {
        _statusAnnouncementPending = false;
        StatusText.Text = string.Empty;
        StatusText.ClearValue(AutomationProperties.NameProperty);
        StatusText.Visibility = Visibility.Collapsed;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (!IsSaving)
        {
            Hide();
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        if (_ownsGeminiAudioConsentGate && _geminiAudioConsentGate is IDisposable disposableGate)
        {
            disposableGate.Dispose();
        }

        base.OnClosed(e);
    }

    private sealed record LocalizedOption(string Id, string DisplayName);
    private sealed record SecretReadResult(string? Value, bool WasRead);
}

/// <summary>Describes a monitor working area in physical pixels.</summary>
internal readonly record struct PhysicalWorkArea(int Left, int Top, int Right, int Bottom)
{
    internal bool HasUsableArea => Right > Left && Bottom > Top;
}

/// <summary>Contains native physical-pixel bounds and WPF sizing limits for the settings window.</summary>
internal readonly record struct SettingsWindowPlacement(
    int Left,
    int Top,
    int Width,
    int Height,
    int MinimumWidth,
    int MinimumHeight,
    int MaximumWidth,
    int MaximumHeight);

/// <summary>Calculates bounded settings-window geometry without accessing WPF or Win32 state.</summary>
internal static class SettingsWindowGeometry
{
    internal const int PreferredWidthDip = 760;
    internal const int PreferredHeightDip = 720;
    internal const int MinimumWidthDip = 520;
    internal const int MinimumHeightDip = 520;
    internal const int CompactMinimumWidthDip = 320;
    internal const int CompactMinimumHeightDip = 360;

    private const double MaximumWidthFraction = 0.92;
    private const double MaximumHeightFraction = 0.90;

    /// <summary>
    /// Centers the preferred settings window inside a physical working area, reducing its dynamic
    /// minimum when the monitor is too small for the normal 520 DIP minimum.
    /// </summary>
    /// <param name="workArea">The usable monitor area in physical pixels.</param>
    /// <param name="dpiScale">Physical pixels per WPF device-independent pixel.</param>
    /// <param name="placement">The calculated physical bounds and limits when successful.</param>
    /// <returns><see langword="true"/> when a non-empty, finite geometry could be calculated.</returns>
    internal static bool TryCalculate(
        PhysicalWorkArea workArea,
        double dpiScale,
        out SettingsWindowPlacement placement)
    {
        placement = default;
        if (!workArea.HasUsableArea || !double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            return false;
        }

        var workingWidth = (long)workArea.Right - workArea.Left;
        var workingHeight = (long)workArea.Bottom - workArea.Top;
        var maximumWidth = CalculateMaximum(workingWidth, MaximumWidthFraction);
        var maximumHeight = CalculateMaximum(workingHeight, MaximumHeightFraction);
        var minimumWidth = CalculateMinimum(
            maximumWidth,
            ScaleToPixels(MinimumWidthDip, dpiScale),
            ScaleToPixels(CompactMinimumWidthDip, dpiScale));
        var minimumHeight = CalculateMinimum(
            maximumHeight,
            ScaleToPixels(MinimumHeightDip, dpiScale),
            ScaleToPixels(CompactMinimumHeightDip, dpiScale));
        var width = Math.Clamp(ScaleToPixels(PreferredWidthDip, dpiScale), minimumWidth, maximumWidth);
        var height = Math.Clamp(ScaleToPixels(PreferredHeightDip, dpiScale), minimumHeight, maximumHeight);
        var left = Center(workArea.Left, workingWidth, width);
        var top = Center(workArea.Top, workingHeight, height);

        placement = new SettingsWindowPlacement(
            left,
            top,
            width,
            height,
            minimumWidth,
            minimumHeight,
            maximumWidth,
            maximumHeight);
        return true;
    }

    private static int CalculateMaximum(long availablePixels, double fraction)
    {
        var limited = Math.Floor(availablePixels * fraction);
        return (int)Math.Clamp(limited, 1, int.MaxValue);
    }

    private static int CalculateMinimum(int maximum, int preferredMinimum, int compactMinimum) =>
        maximum >= preferredMinimum
            ? preferredMinimum
            : Math.Min(maximum, compactMinimum);

    private static int ScaleToPixels(int dips, double dpiScale)
    {
        var physicalPixels = Math.Round(dips * dpiScale, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(physicalPixels, 1, int.MaxValue);
    }

    private static int Center(int start, long availablePixels, int size)
    {
        var centered = (long)start + ((availablePixels - size) / 2);
        return (int)Math.Clamp(centered, int.MinValue, int.MaxValue);
    }
}

/// <summary>Reports newly persisted non-secret settings to application composition.</summary>
public sealed class SettingsSavedEventArgs : EventArgs
{
    /// <summary>Initializes event data.</summary>
    /// <param name="settings">Normalized persisted settings.</param>
    /// <param name="startAfterSave">Whether the explicit UI action requested immediate capture.</param>
    /// <param name="verifyApiKey">Whether the explicit UI action requested Gemini key verification.</param>
    public SettingsSavedEventArgs(
        AppSettings settings,
        bool startAfterSave = false,
        bool verifyApiKey = false)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        StartAfterSave = startAfterSave;
        VerifyApiKey = verifyApiKey;
    }

    /// <summary>Gets normalized persisted settings.</summary>
    public AppSettings Settings { get; }

    /// <summary>Gets whether capture should start after the privacy reset consumes these settings.</summary>
    public bool StartAfterSave { get; }

    /// <summary>Gets whether the saved key must be verified against Gemini before returning.</summary>
    public bool VerifyApiKey { get; }
}
