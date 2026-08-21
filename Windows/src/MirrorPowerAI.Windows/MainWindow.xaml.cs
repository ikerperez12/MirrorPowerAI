using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using MirrorPowerAI.Core.Privacy;
using MirrorPowerAI.Core.Security;
using MirrorPowerAI.Windows.Audio;
using MirrorPowerAI.Windows.Platform;
using MirrorPowerAI.Windows.Resources;

namespace MirrorPowerAI.Windows;

/// <summary>
/// Accessible per-user settings window for privacy, transcription, language, and output device.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Current Gemini Audio consent wording revision.</summary>
    public const int CurrentGeminiAudioConsentVersion = GeminiAudioConsentPolicy.CurrentVersion;

    /// <summary>Stable secret-store key for the Gemini API key.</summary>
    public const string GeminiApiKeySecretName = "gemini-api-key";

    /// <summary>Stable secret-store key for the project context.</summary>
    public const string ProjectContextSecretName = "project-context";

    private readonly JsonSettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly IAudioDeviceCatalog _audioDeviceCatalog;
    private readonly LocalizationService _localization;
    private bool _allowClose;
    private bool _isLoading;
    private bool _apiKeyWasRead;
    private bool _contextWasRead;

    /// <summary>Gets whether protected and non-secret settings are being persisted.</summary>
    public bool IsSaving { get; private set; }

    /// <summary>Initializes the settings window with testable platform services.</summary>
    /// <param name="settingsStore">Non-secret JSON settings store.</param>
    /// <param name="secretStore">DPAPI-backed secret store.</param>
    /// <param name="audioDeviceCatalog">Available output-device source.</param>
    /// <param name="localization">Live localized string source.</param>
    public MainWindow(
        JsonSettingsStore settingsStore,
        ISecretStore secretStore,
        IAudioDeviceCatalog audioDeviceCatalog,
        LocalizationService localization)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _audioDeviceCatalog = audioDeviceCatalog ?? throw new ArgumentNullException(nameof(audioDeviceCatalog));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        InitializeComponent();
    }

    /// <summary>Raised after both protected and non-secret settings are saved.</summary>
    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

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
            var settings = (persistedSettings with { Context = context.Value ?? string.Empty }).Normalize();
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
        _allowClose = true;
        Close();
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
        if (IsSaving)
        {
            return;
        }

        HideStatus();
        var provider = ProviderBox.SelectedValue as string ?? TranscriptionProviders.LocalWhisper;
        var hasCloudConsent = CloudConsentBox.IsChecked == true;
        if (provider == TranscriptionProviders.GeminiAudio && !hasCloudConsent)
        {
            ShowStatus(_localization["ConsentRequired"], isError: true);
            CloudConsentBox.Focus();
            return;
        }

        IsSaving = true;
        var settings = new AppSettings
        {
            Context = ContextBox.Text,
            TranscriptionProvider = provider,
            Language = LanguageBox.SelectedValue as string ?? "es",
            AudioDeviceId = DeviceBox.SelectedValue as string ?? AudioDeviceOption.DefaultDeviceId,
            GeminiAudioConsentVersion = provider == TranscriptionProviders.GeminiAudio && hasCloudConsent
                ? CurrentGeminiAudioConsentVersion
                : 0,
            GeminiAudioConsentGrantedAtUtc = provider == TranscriptionProviders.GeminiAudio && hasCloudConsent
                ? DateTimeOffset.UtcNow
                : null,
        }.Normalize();

        try
        {
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
            IsSaving = false;
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs eventArgs) => await SaveAsync();

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs) => Hide();

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
        StatusText.Visibility = Visibility.Visible;
        var peer = UIElementAutomationPeer.CreatePeerForElement(StatusText)
            ?? new TextBlockAutomationPeer(StatusText);
        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void HideStatus()
    {
        StatusText.Text = string.Empty;
        StatusText.Visibility = Visibility.Collapsed;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        Hide();
    }

    private sealed record LocalizedOption(string Id, string DisplayName);
    private sealed record SecretReadResult(string? Value, bool WasRead);
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
