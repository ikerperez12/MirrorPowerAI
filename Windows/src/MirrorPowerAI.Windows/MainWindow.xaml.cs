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
    public MainWindow(
        IAppSettingsStore settingsStore,
        ISecretStore secretStore,
        IAudioDeviceCatalog audioDeviceCatalog,
        LocalizationService localization,
        IGeminiAudioConsentGate? geminiAudioConsentGate = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _audioDeviceCatalog = audioDeviceCatalog ?? throw new ArgumentNullException(nameof(audioDeviceCatalog));
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
            CloudConsentBox.IsChecked =
                settings.GeminiAudioConsentVersion == CurrentGeminiAudioConsentVersion &&
                settings.GeminiAudioConsentGrantedAtUtc is not null;
            UpdateConsentVisibility();

            if (!apiKey.WasRead || !context.WasRead)
            {
                ShowStatus(_localization["SettingsLoadError"], isError: true);
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
    public async Task SaveAsync()
    {
        if (IsSaving || _applicationExitRequested)
        {
            return;
        }

        HideStatus();
        var provider = ProviderBox.SelectedValue as string ?? TranscriptionProviders.LocalWhisper;
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
            return;
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
            PopulateProviderOptions(settings.TranscriptionProvider);
            PopulateLanguageOptions(settings.Language);
            PopulateDeviceOptions(deviceOptions, settings.AudioDeviceId);
            ShowStatus(_localization["SettingsSaved"], isError: false);
            SettingsSaved?.Invoke(this, new SettingsSavedEventArgs(settings));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            ShowStatus(_localization["SettingsSaveError"], isError: true);
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
        ContextBox.IsEnabled = !isSaving;
        ProviderBox.IsEnabled = !isSaving;
        LanguageBox.IsEnabled = !isSaving;
        DeviceBox.IsEnabled = !isSaving;
        CloudConsentBox.IsEnabled = !isSaving;
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

    private void UpdateConsentVisibility()
    {
        CloudConsentGroup.Visibility = ProviderBox.SelectedValue as string == TranscriptionProviders.GeminiAudio
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = isError ? $"⚠ {message}" : message;
        AutomationProperties.SetName(StatusText, StatusText.Text);
        StatusText.Visibility = Visibility.Visible;
        _statusAnnouncementPending = true;
        ScheduleStatusAnnouncement();
    }

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
    public SettingsSavedEventArgs(AppSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Gets normalized persisted settings.</summary>
    public AppSettings Settings { get; }
}
