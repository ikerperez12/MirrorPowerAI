using System.IO;
using System.Security.Cryptography;
using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Core.Answers;
using MirrorPowerAI.Core.Configuration;
using MirrorPowerAI.Core.Gemini;
using MirrorPowerAI.Core.Privacy;
using MirrorPowerAI.Core.Security;
using MirrorPowerAI.Core.Sessions;
using MirrorPowerAI.Core.Transcription;
using MirrorPowerAI.Windows.Audio;
using MirrorPowerAI.Windows.Platform;
using MirrorPowerAI.Windows.Transcription;

namespace MirrorPowerAI.Windows.Shell;

/// <summary>
/// Adapts the shared session controller to the tray shell while rebuilding a controller from fresh settings for each session.
/// </summary>
public sealed class CoreSessionCommands : ISessionCommands, IAsyncDisposable
{
    private readonly IAppSettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly WhisperLocalTranscriptionService _localTranscriptionService;
    private readonly GeminiClient _geminiClient;
    private readonly IAnswerService _answerService;
    private readonly IGeminiAudioConsentGate _geminiAudioConsentGate;
    private readonly Func<AppSettings, string?, IAudioCaptureService> _audioCaptureFactory;
    private readonly bool _ownsGeminiAudioConsentGate;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private SessionSnapshot _snapshot = new(ShellActivityState.Idle);
    private SessionController? _controller;
    private IAudioCaptureService? _audioCaptureService;
    private IAudioCaptureActivitySource? _audioActivitySource;
    private bool _disposed;

    /// <summary>Initializes the Core session bridge with long-lived provider dependencies.</summary>
    /// <param name="settingsStore">Bounded non-secret settings storage.</param>
    /// <param name="secretStore">DPAPI-protected key and context storage.</param>
    /// <param name="localTranscriptionService">Long-lived local Whisper adapter.</param>
    /// <param name="geminiClient">Long-lived typed Gemini client.</param>
    /// <param name="geminiAudioConsentGate">Shared process-level fail-closed Gemini Audio privacy barrier.</param>
    public CoreSessionCommands(
        IAppSettingsStore settingsStore,
        ISecretStore secretStore,
        WhisperLocalTranscriptionService localTranscriptionService,
        GeminiClient geminiClient,
        IGeminiAudioConsentGate? geminiAudioConsentGate = null)
        : this(
            settingsStore,
            secretStore,
            localTranscriptionService,
            geminiClient,
            geminiAudioConsentGate,
            CreateAudioCapture)
    {
    }

    /// <summary>Initializes the bridge with an injectable capture factory for deterministic tests.</summary>
    /// <param name="settingsStore">Bounded non-secret settings storage.</param>
    /// <param name="secretStore">DPAPI-protected key and context storage.</param>
    /// <param name="localTranscriptionService">Long-lived local Whisper adapter.</param>
    /// <param name="geminiClient">Long-lived typed Gemini client.</param>
    /// <param name="geminiAudioConsentGate">Shared process-level fail-closed Gemini Audio privacy barrier.</param>
    /// <param name="audioCaptureFactory">Creates the source selected by fresh settings.</param>
    internal CoreSessionCommands(
        IAppSettingsStore settingsStore,
        ISecretStore secretStore,
        WhisperLocalTranscriptionService localTranscriptionService,
        GeminiClient geminiClient,
        IGeminiAudioConsentGate? geminiAudioConsentGate,
        Func<AppSettings, string?, IAudioCaptureService> audioCaptureFactory)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _localTranscriptionService = localTranscriptionService ??
            throw new ArgumentNullException(nameof(localTranscriptionService));
        _geminiClient = geminiClient ?? throw new ArgumentNullException(nameof(geminiClient));
        _geminiAudioConsentGate = geminiAudioConsentGate ?? new GeminiAudioConsentGate();
        _audioCaptureFactory = audioCaptureFactory ??
            throw new ArgumentNullException(nameof(audioCaptureFactory));
        _ownsGeminiAudioConsentGate = geminiAudioConsentGate is null;
        _answerService = new GeminiAnswerService(_geminiClient);
    }

    /// <inheritdoc />
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public SessionSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <inheritdoc />
    public async Task ToggleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var controller = _controller;
            if (controller is null ||
                controller.State is SessionState.Idle or SessionState.ShowingResult or SessionState.Error)
            {
                controller = await CreateFreshControllerAsync(cancellationToken).ConfigureAwait(false);
            }

            await controller.ToggleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Publish(new SessionSnapshot(
                ShellActivityState.Error,
                UserMessage: "SessionUnexpectedError"));
        }
        catch (AudioCaptureException exception)
        {
            Publish(new SessionSnapshot(
                ShellActivityState.Error,
                UserMessage: MapAudioCaptureFailureResourceKey(exception.Failure)));
        }
        catch (PlatformNotSupportedException)
        {
            Publish(new SessionSnapshot(
                ShellActivityState.Error,
                UserMessage: "SessionAudioSourceUnavailable"));
        }
        catch (ConfigurationValidationException)
        {
            Publish(new SessionSnapshot(
                ShellActivityState.Error,
                UserMessage: "SessionInvalidConfiguration"));
        }
        catch (GeminiApiException exception) when (exception.Kind == GeminiErrorKind.MissingApiKey)
        {
            Publish(new SessionSnapshot(
                ShellActivityState.Error,
                UserMessage: "SessionApiKeyRequired"));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var controller = Volatile.Read(ref _controller);
        if (controller is not null)
        {
            await controller.CancelAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Cancellation deliberately happens before waiting on the lifecycle gate: ToggleAsync owns
        // that gate while transcription/answering is in flight, and cancellation must be able to
        // stop that operation before ResetAsync waits to dispose its privacy-bound controller.
        var controller = Volatile.Read(ref _controller);
        if (controller is not null)
        {
            await controller.CancelAsync(cancellationToken).ConfigureAwait(false);
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await DisposeCurrentControllerAsync().ConfigureAwait(false);
            Publish(new SessionSnapshot(ShellActivityState.Idle));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await DisposeCurrentControllerAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_ownsGeminiAudioConsentGate && _geminiAudioConsentGate is IDisposable disposableGate)
            {
                disposableGate.Dispose();
            }

            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private async Task<SessionController> CreateFreshControllerAsync(CancellationToken cancellationToken)
    {
        await DisposeCurrentControllerAsync().ConfigureAwait(false);

        var settingsTask = _settingsStore.LoadAsync(cancellationToken);
        var contextTask = _secretStore.GetSecretAsync(MainWindow.ProjectContextSecretName, cancellationToken);
        var apiKeyTask = _secretStore.GetSecretAsync(MainWindow.GeminiApiKeySecretName, cancellationToken);
        await Task.WhenAll(settingsTask, contextTask, apiKeyTask).ConfigureAwait(false);
        var apiKey = await apiKeyTask.ConfigureAwait(false);
        if (!IsUsableGeminiApiKey(apiKey))
        {
            throw new GeminiApiException(
                GeminiErrorKind.MissingApiKey,
                "A valid Gemini API key is required before capture starts.");
        }

        var persistedSettings = await settingsTask.ConfigureAwait(false);
        var settings = (persistedSettings with
        {
            Context = await contextTask.ConfigureAwait(false) ?? string.Empty,
        }).Normalize();
        var options = settings.ToCoreOptions();
        var consent = CreateConsent(settings);
        var geminiTranscription = new GeminiAudioTranscriptionService(
            _geminiClient,
            () => consent,
            () => _geminiAudioConsentGate.TryAuthorize(consent));
        var audioCapture = _audioCaptureFactory(settings, options.OutputDeviceId);
        var controller = new SessionController(
            audioCapture,
            [_localTranscriptionService, geminiTranscription],
            _answerService,
            options);
        controller.StateChanged += OnControllerStateChanged;
        _audioCaptureService = audioCapture;
        _audioActivitySource = audioCapture as IAudioCaptureActivitySource;
        if (_audioActivitySource is not null)
        {
            _audioActivitySource.AudibleSignalDetected += OnAudibleSignalDetected;
        }
        Volatile.Write(ref _controller, controller);
        Publish(new SessionSnapshot(ShellActivityState.Idle));
        return controller;
    }

    /// <summary>
    /// Checks only the structural constraints enforced again by <see cref="GeminiClient"/> without
    /// retaining, logging, or comparing the secret.
    /// </summary>
    /// <param name="apiKey">DPAPI-unprotected key read for this user.</param>
    /// <returns>Whether capture may begin without a guaranteed missing-key failure later.</returns>
    internal static bool IsUsableGeminiApiKey(string? apiKey)
    {
        var normalized = apiKey?.Trim();
        return normalized is { Length: > 0 and <= 512 } &&
               !normalized.Any(char.IsControl);
    }

    private static GeminiAudioConsent? CreateConsent(AppSettings settings) =>
        settings.GeminiAudioConsentVersion == GeminiAudioConsentPolicy.CurrentVersion &&
        settings.GeminiAudioConsentGrantedAtUtc is DateTimeOffset grantedAtUtc
            ? new GeminiAudioConsent(settings.GeminiAudioConsentVersion, grantedAtUtc)
            : null;

    private static WasapiLoopbackAudioCaptureService CreateAudioCapture(
        AppSettings settings,
        string? outputDeviceId)
    {
        if (settings.AudioCaptureSource == AudioCaptureSources.Application)
        {
            if (string.IsNullOrWhiteSpace(settings.AudioProcessName))
            {
                throw new AudioCaptureException(
                    AudioCaptureFailure.SourceUnavailable,
                    "No audio application is selected.");
            }

            return new WasapiLoopbackAudioCaptureService(
                new ProcessAudioEndpointProvider(settings.AudioProcessName, settings.AudioProcessId),
                new ProcessLoopbackCaptureSessionFactory(),
                new Pcm16WaveConverter(),
                new SystemCaptureTimer(),
                requestedDeviceId: null,
                maximumDuration: MirrorPowerAI.Core.Configuration.MirrorPowerAIOptions.CaptureDurationLimit);
        }

        return new WasapiLoopbackAudioCaptureService(
            new NAudioEndpointProvider(),
            new NAudioLoopbackCaptureSessionFactory(),
            new Pcm16WaveConverter(),
            new SystemCaptureTimer(),
            outputDeviceId,
            MirrorPowerAI.Core.Configuration.MirrorPowerAIOptions.CaptureDurationLimit);
    }

    private void OnControllerStateChanged(object? sender, MirrorPowerAI.Core.Sessions.SessionStateChangedEventArgs eventArgs)
    {
        if (sender is not SessionController controller || !ReferenceEquals(controller, Volatile.Read(ref _controller)))
        {
            return;
        }

        Publish(CreateSnapshot(controller));
    }

    private void OnAudibleSignalDetected(object? sender, EventArgs eventArgs)
    {
        if (!ReferenceEquals(sender, Volatile.Read(ref _audioActivitySource)))
        {
            return;
        }

        var controller = Volatile.Read(ref _controller);
        if (controller?.State == SessionState.Capturing)
        {
            Publish(CreateSnapshot(controller));
        }
    }

    private SessionSnapshot CreateSnapshot(SessionController controller)
    {
        var result = controller.LastResult;
        return new SessionSnapshot(
            controller.State switch
            {
                SessionState.Capturing => ShellActivityState.Capturing,
                SessionState.Transcribing or SessionState.RequestingAnswer => ShellActivityState.Processing,
                SessionState.Error => ShellActivityState.Error,
                _ => ShellActivityState.Idle,
            },
            result?.Transcript,
            result?.Answer,
            MapFailureResourceKey(controller.LastFailure),
            AudioSignalDetected: controller.State == SessionState.Capturing &&
                _audioActivitySource?.HasDetectedAudibleSignal == true);
    }

    private static string? MapFailureResourceKey(SessionFailure? failure) => failure?.Kind switch
    {
        SessionErrorKind.InvalidConfiguration => "SessionInvalidConfiguration",
        SessionErrorKind.ProviderUnavailable => "SessionProviderUnavailable",
        SessionErrorKind.EmptyAudio => "SessionEmptyAudio",
        SessionErrorKind.EmptyTranscript => "SessionEmptyTranscript",
        SessionErrorKind.EmptyAnswer => "SessionEmptyAnswer",
        SessionErrorKind.ConsentRequired => "SessionConsentRequired",
        SessionErrorKind.Gemini => "SessionGeminiError",
        SessionErrorKind.AudioSourceUnavailable => "SessionAudioSourceUnavailable",
        SessionErrorKind.AudioSourceDisconnected => "SessionAudioSourceDisconnected",
        SessionErrorKind.AudioDeviceChanged => "SessionAudioDeviceChanged",
        SessionErrorKind.AudioCaptureLimit => "SessionAudioCaptureLimit",
        SessionErrorKind.AudioBackend => "SessionAudioBackendError",
        SessionErrorKind.Unexpected => "SessionUnexpectedError",
        _ => null,
    };

    internal static string MapAudioCaptureFailureResourceKey(AudioCaptureFailure failure) => failure switch
    {
        AudioCaptureFailure.SourceUnavailable => "SessionAudioSourceUnavailable",
        AudioCaptureFailure.SourceDisconnected => "SessionAudioSourceDisconnected",
        AudioCaptureFailure.DefaultDeviceChanged => "SessionAudioDeviceChanged",
        AudioCaptureFailure.BufferLimitReached => "SessionAudioCaptureLimit",
        _ => "SessionAudioBackendError",
    };

    private void Publish(SessionSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(snapshot));
    }

    private async Task DisposeCurrentControllerAsync()
    {
        var audioActivitySource = Interlocked.Exchange(ref _audioActivitySource, null);
        if (audioActivitySource is not null)
        {
            audioActivitySource.AudibleSignalDetected -= OnAudibleSignalDetected;
        }

        var controller = Interlocked.Exchange(ref _controller, null);
        if (controller is not null)
        {
            controller.StateChanged -= OnControllerStateChanged;
            await controller.DisposeAsync().ConfigureAwait(false);
        }

        var audioCapture = _audioCaptureService;
        _audioCaptureService = null;
        if (audioCapture is IAsyncDisposable asyncDisposableCapture)
        {
            await asyncDisposableCapture.DisposeAsync().ConfigureAwait(false);
        }
        else if (audioCapture is IDisposable disposableCapture)
        {
            disposableCapture.Dispose();
        }
    }
}
