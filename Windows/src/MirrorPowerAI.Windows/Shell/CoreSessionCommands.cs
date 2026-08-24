using System.IO;
using System.Security.Cryptography;
using MirrorPowerAI.Core.Answers;
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
    private readonly bool _ownsGeminiAudioConsentGate;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private SessionSnapshot _snapshot = new(ShellActivityState.Idle);
    private SessionController? _controller;
    private WasapiLoopbackAudioCaptureService? _audioCaptureService;
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
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _localTranscriptionService = localTranscriptionService ??
            throw new ArgumentNullException(nameof(localTranscriptionService));
        _geminiClient = geminiClient ?? throw new ArgumentNullException(nameof(geminiClient));
        _geminiAudioConsentGate = geminiAudioConsentGate ?? new GeminiAudioConsentGate();
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
        await Task.WhenAll(settingsTask, contextTask).ConfigureAwait(false);
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
        var audioCapture = CreateAudioCapture(options.OutputDeviceId);
        var controller = new SessionController(
            audioCapture,
            [_localTranscriptionService, geminiTranscription],
            _answerService,
            options);
        controller.StateChanged += OnControllerStateChanged;
        _audioCaptureService = audioCapture;
        Volatile.Write(ref _controller, controller);
        Publish(new SessionSnapshot(ShellActivityState.Idle));
        return controller;
    }

    private static GeminiAudioConsent? CreateConsent(AppSettings settings) =>
        settings.GeminiAudioConsentVersion == GeminiAudioConsentPolicy.CurrentVersion &&
        settings.GeminiAudioConsentGrantedAtUtc is DateTimeOffset grantedAtUtc
            ? new GeminiAudioConsent(settings.GeminiAudioConsentVersion, grantedAtUtc)
            : null;

    private static WasapiLoopbackAudioCaptureService CreateAudioCapture(string? outputDeviceId) =>
        new(
            new NAudioEndpointProvider(),
            new NAudioLoopbackCaptureSessionFactory(),
            new Pcm16WaveConverter(),
            new SystemCaptureTimer(),
            outputDeviceId,
            MirrorPowerAI.Core.Configuration.MirrorPowerAIOptions.CaptureDurationLimit);

    private void OnControllerStateChanged(object? sender, MirrorPowerAI.Core.Sessions.SessionStateChangedEventArgs eventArgs)
    {
        if (sender is not SessionController controller || !ReferenceEquals(controller, Volatile.Read(ref _controller)))
        {
            return;
        }

        Publish(CreateSnapshot(controller));
    }

    private static SessionSnapshot CreateSnapshot(SessionController controller)
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
            MapFailureResourceKey(controller.LastFailure));
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
        SessionErrorKind.Unexpected => "SessionUnexpectedError",
        _ => null,
    };

    private void Publish(SessionSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(snapshot));
    }

    private async Task DisposeCurrentControllerAsync()
    {
        var controller = Interlocked.Exchange(ref _controller, null);
        if (controller is not null)
        {
            controller.StateChanged -= OnControllerStateChanged;
            await controller.DisposeAsync().ConfigureAwait(false);
        }

        var audioCapture = _audioCaptureService;
        _audioCaptureService = null;
        if (audioCapture is not null)
        {
            await audioCapture.DisposeAsync().ConfigureAwait(false);
        }
    }
}
