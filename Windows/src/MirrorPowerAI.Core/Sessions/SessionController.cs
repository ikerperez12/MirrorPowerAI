using MirrorPowerAI.Core.Answers;
using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Core.Configuration;
using MirrorPowerAI.Core.Gemini;
using MirrorPowerAI.Core.Privacy;
using MirrorPowerAI.Core.Transcription;

namespace MirrorPowerAI.Core.Sessions;

/// <summary>
/// Owns every capture-to-answer state transition and prevents overlapping sessions.
/// </summary>
public sealed class SessionController : IAsyncDisposable
{
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly Dictionary<TranscriptionProvider, ITranscriptionService> _transcriptionServices;
    private readonly IAnswerService _answerService;
    private readonly MirrorPowerAIOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenSource? _deadlineCancellation;
    private Task? _deadlineTask;
    private Task? _activeOperation;
    private SessionState _state;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionController"/> class.
    /// </summary>
    /// <param name="audioCaptureService">The system-output capture service.</param>
    /// <param name="transcriptionServices">The explicitly available transcription providers.</param>
    /// <param name="answerService">The textual answer service.</param>
    /// <param name="options">The current non-secret options.</param>
    /// <param name="timeProvider">An optional time provider used for the capture deadline.</param>
    public SessionController(
        IAudioCaptureService audioCaptureService,
        IEnumerable<ITranscriptionService> transcriptionServices,
        IAnswerService answerService,
        MirrorPowerAIOptions options,
        TimeProvider? timeProvider = null)
    {
        _audioCaptureService = audioCaptureService ??
            throw new ArgumentNullException(nameof(audioCaptureService));
        ArgumentNullException.ThrowIfNull(transcriptionServices);
        _answerService = answerService ?? throw new ArgumentNullException(nameof(answerService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var services = new Dictionary<TranscriptionProvider, ITranscriptionService>();
        foreach (var service in transcriptionServices)
        {
            ArgumentNullException.ThrowIfNull(service);
            if (!services.TryAdd(service.Provider, service))
            {
                throw new ArgumentException(
                    $"Sólo puede registrarse un servicio para {service.Provider}.",
                    nameof(transcriptionServices));
            }
        }

        _transcriptionServices = services;
    }

    /// <summary>
    /// Raised synchronously after each externally observable state transition.
    /// </summary>
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Gets the current session state.
    /// </summary>
    public SessionState State => _state;

    /// <summary>
    /// Gets the latest successful in-memory result.
    /// </summary>
    public SessionResult? LastResult { get; private set; }

    /// <summary>
    /// Gets the latest safe failure description.
    /// </summary>
    public SessionFailure? LastFailure { get; private set; }

    /// <summary>
    /// Starts capture from an inactive state or stops and processes the active capture.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel this command.</param>
    /// <returns>A task that completes when the selected action has completed.</returns>
    /// <exception cref="SessionBusyException">
    /// Thrown when another toggle or processing operation is already active.
    /// </exception>
    public async Task ToggleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!await _commandGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            throw new SessionBusyException();
        }

        Task? operationToAwait = null;
        try
        {
            switch (_state)
            {
                case SessionState.Idle:
                case SessionState.ShowingResult:
                case SessionState.Error:
                    await StartCaptureAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case SessionState.Capturing:
                    TransitionTo(SessionState.Transcribing);
                    CancelCaptureDeadline();
                    operationToAwait = StopAndProcessAsync(
                        _sessionCancellation ?? throw new InvalidOperationException("No hay una sesión activa."),
                        cancellationToken);
                    _activeOperation = operationToAwait;
                    break;

                case SessionState.Transcribing:
                case SessionState.RequestingAnswer:
                    throw new SessionBusyException();

                default:
                    throw new InvalidOperationException("Estado de sesión desconocido.");
            }
        }
        finally
        {
            _commandGate.Release();
        }

        if (operationToAwait is not null)
        {
            await operationToAwait.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels capture or processing and returns the controller to <see cref="SessionState.Idle"/>.
    /// </summary>
    /// <param name="cancellationToken">A token used only while waiting to issue cancellation.</param>
    /// <returns>A task that completes after active work has stopped.</returns>
    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await CancelCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels active work and releases controller-owned synchronization resources.
    /// </summary>
    /// <returns>A task that completes after capture and processing have stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CancelCoreAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _sessionCancellation?.Dispose();
        _deadlineCancellation?.Dispose();
        _commandGate.Dispose();
    }

    private async Task StartCaptureAsync(CancellationToken cancellationToken)
    {
        LastResult = null;
        LastFailure = null;

        try
        {
            _options.EnsureValid();
            _ = GetSelectedTranscriptionService();

            _sessionCancellation?.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            await _audioCaptureService.StartAsync(cancellationToken).ConfigureAwait(false);
            TransitionTo(SessionState.Capturing);
            ScheduleCaptureDeadline(_options.MaxCaptureDuration);
        }
        catch (OperationCanceledException)
        {
            await StopCaptureAfterFailedStartAsync().ConfigureAwait(false);
            CleanupSession();
            TransitionTo(SessionState.Idle);
            throw;
        }
        catch (Exception exception) when (exception is not SessionBusyException)
        {
            await StopCaptureAfterFailedStartAsync().ConfigureAwait(false);
            CleanupSession();
            SetFailure(exception);
        }
    }

    private async Task StopAndProcessAsync(
        CancellationTokenSource sessionCancellation,
        CancellationToken commandCancellation)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            sessionCancellation.Token,
            commandCancellation);

        try
        {
            using var audio = await _audioCaptureService
                .StopAsync(operationCancellation.Token)
                .ConfigureAwait(false);
            EnsureUsableAudio(audio);

            var service = GetSelectedTranscriptionService();
            var language = _options.AutomaticLanguageDetection ? "auto" : _options.Language;
            var transcript = (await service
                    .TranscribeAsync(audio, language, operationCancellation.Token)
                    .ConfigureAwait(false))
                .Trim();

            if (string.IsNullOrWhiteSpace(transcript))
            {
                throw new SessionOperationException(SessionErrorKind.EmptyTranscript);
            }

            TransitionTo(SessionState.RequestingAnswer);
            var answer = (await _answerService
                    .AskAsync(transcript, _options.Context, operationCancellation.Token)
                    .ConfigureAwait(false))
                .Trim();

            if (string.IsNullOrWhiteSpace(answer))
            {
                throw new SessionOperationException(SessionErrorKind.EmptyAnswer);
            }

            LastResult = new SessionResult(
                transcript,
                answer,
                service.Provider,
                _timeProvider.GetUtcNow());
            LastFailure = null;
            TransitionTo(SessionState.ShowingResult);
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
            LastResult = null;
            LastFailure = null;
            TransitionTo(SessionState.Idle);
        }
        catch (OperationCanceledException)
        {
            sessionCancellation.Cancel();
            LastResult = null;
            LastFailure = null;
            TransitionTo(SessionState.Idle);
            throw;
        }
        catch (Exception exception)
        {
            SetFailure(exception);
        }
        finally
        {
            CleanupSession();
            _activeOperation = null;
        }
    }

    private async Task CancelCoreAsync(CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task? operationToAwait = null;
        try
        {
            CancelCaptureDeadline();
            _sessionCancellation?.Cancel();

            if (_state == SessionState.Capturing)
            {
                if (_audioCaptureService.IsCapturing)
                {
                    try
                    {
                        using var discardedAudio = await _audioCaptureService
                            .StopAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Cancellation is best-effort cleanup; no adapter details are retained.
                    }
                }

                CleanupSession();
                LastResult = null;
                LastFailure = null;
                TransitionTo(SessionState.Idle);
            }
            else if (_state is SessionState.Transcribing or SessionState.RequestingAnswer)
            {
                operationToAwait = _activeOperation;
            }
            else if (_state is SessionState.ShowingResult or SessionState.Error)
            {
                LastResult = null;
                LastFailure = null;
                TransitionTo(SessionState.Idle);
            }
        }
        finally
        {
            _commandGate.Release();
        }

        if (operationToAwait is not null)
        {
            await operationToAwait.ConfigureAwait(false);
        }
    }

    private void ScheduleCaptureDeadline(TimeSpan duration)
    {
        CancelCaptureDeadline();
        _deadlineCancellation = new CancellationTokenSource();
        _deadlineTask = EnforceCaptureDeadlineAsync(duration, _deadlineCancellation.Token);
    }

    private async Task EnforceCaptureDeadlineAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, _timeProvider, cancellationToken).ConfigureAwait(false);
            await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            Task? operationToAwait = null;
            try
            {
                if (_state == SessionState.Capturing && _sessionCancellation is not null)
                {
                    TransitionTo(SessionState.Transcribing);
                    operationToAwait = StopAndProcessAsync(_sessionCancellation, CancellationToken.None);
                    _activeOperation = operationToAwait;
                }
            }
            finally
            {
                _commandGate.Release();
            }

            if (operationToAwait is not null)
            {
                await operationToAwait.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // An explicit stop or cancellation always wins over the capture deadline.
        }
    }

    private ITranscriptionService GetSelectedTranscriptionService()
    {
        if (_transcriptionServices.TryGetValue(_options.Provider, out var service))
        {
            return service;
        }

        throw new SessionOperationException(SessionErrorKind.ProviderUnavailable);
    }

    private void EnsureUsableAudio(CapturedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);

        if (audio.WavData.IsEmpty || !audio.ContainsAudibleSignal || audio.Duration <= TimeSpan.Zero)
        {
            throw new SessionOperationException(SessionErrorKind.EmptyAudio);
        }

        if (audio.Duration > _options.MaxCaptureDuration)
        {
            throw new SessionOperationException(SessionErrorKind.EmptyAudio);
        }
    }

    private async Task StopCaptureAfterFailedStartAsync()
    {
        if (!_audioCaptureService.IsCapturing)
        {
            return;
        }

        try
        {
            using var discardedAudio = await _audioCaptureService
                .StopAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Preserve the original startup failure while making cleanup best-effort.
        }
    }

    private void SetFailure(Exception exception)
    {
        LastResult = null;
        LastFailure = exception switch
        {
            ConfigurationValidationException => new(
                SessionErrorKind.InvalidConfiguration,
                "Revisa la configuración antes de iniciar la captura."),
            GeminiAudioConsentRequiredException => new(
                SessionErrorKind.ConsentRequired,
                "Autoriza explícitamente el envío de audio a Gemini."),
            GeminiApiException geminiException => new(
                SessionErrorKind.Gemini,
                GetGeminiFailureMessage(geminiException.Kind)),
            AudioCaptureException audioException => new(
                MapAudioFailure(audioException.Failure),
                GetAudioFailureMessage(audioException.Failure)),
            SessionOperationException sessionException => new(
                sessionException.Kind,
                GetSessionFailureMessage(sessionException.Kind)),
            _ => new(
                SessionErrorKind.Unexpected,
                "No se pudo completar la sesión. Revisa el dispositivo y vuelve a intentarlo."),
        };
        TransitionTo(SessionState.Error);
    }

    private static string GetGeminiFailureMessage(GeminiErrorKind kind) => kind switch
    {
        GeminiErrorKind.MissingApiKey => "Configura una API key de Gemini válida.",
        GeminiErrorKind.Unauthorized => "Gemini rechazó la API key o sus permisos.",
        GeminiErrorKind.RateLimited => "Gemini ha alcanzado el límite de solicitudes.",
        GeminiErrorKind.ServiceUnavailable => "Gemini no está disponible temporalmente.",
        GeminiErrorKind.Timeout => "La petición a Gemini agotó el tiempo de espera.",
        GeminiErrorKind.Blocked => "Gemini bloqueó la solicitud o la respuesta.",
        GeminiErrorKind.EmptyResponse => "Gemini no devolvió texto utilizable.",
        GeminiErrorKind.InputTooLarge => "La entrada supera el límite seguro configurado.",
        _ => "Gemini devolvió una respuesta no válida.",
    };

    private static string GetSessionFailureMessage(SessionErrorKind kind) => kind switch
    {
        SessionErrorKind.ProviderUnavailable => "El proveedor de transcripción seleccionado no está disponible.",
        SessionErrorKind.EmptyAudio => "No se detectó audio de salida utilizable.",
        SessionErrorKind.EmptyTranscript => "El proveedor no pudo obtener una transcripción.",
        SessionErrorKind.EmptyAnswer => "El servicio no devolvió una respuesta.",
        _ => "No se pudo completar la sesión.",
    };

    private static SessionErrorKind MapAudioFailure(AudioCaptureFailure failure) => failure switch
    {
        AudioCaptureFailure.SourceUnavailable => SessionErrorKind.AudioSourceUnavailable,
        AudioCaptureFailure.SourceDisconnected => SessionErrorKind.AudioSourceDisconnected,
        AudioCaptureFailure.DefaultDeviceChanged => SessionErrorKind.AudioDeviceChanged,
        AudioCaptureFailure.BufferLimitReached => SessionErrorKind.AudioCaptureLimit,
        _ => SessionErrorKind.AudioBackend,
    };

    private static string GetAudioFailureMessage(AudioCaptureFailure failure) => failure switch
    {
        AudioCaptureFailure.SourceUnavailable => "La fuente de audio seleccionada no está disponible.",
        AudioCaptureFailure.SourceDisconnected => "La fuente de audio terminó durante la captura.",
        AudioCaptureFailure.DefaultDeviceChanged => "El dispositivo de salida predeterminado cambió durante la captura.",
        AudioCaptureFailure.BufferLimitReached => "La captura alcanzó su límite seguro de memoria.",
        _ => "Windows interrumpió la captura de audio inesperadamente.",
    };

    private void TransitionTo(SessionState nextState)
    {
        if (_state == nextState)
        {
            return;
        }

        var previousState = _state;
        _state = nextState;
        var eventArgs = new SessionStateChangedEventArgs(previousState, nextState);
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<SessionStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception)
            {
                // A presentation subscriber cannot be allowed to corrupt the session state machine.
            }
        }
    }

    private void CancelCaptureDeadline()
    {
        _deadlineCancellation?.Cancel();
        _deadlineCancellation?.Dispose();
        _deadlineCancellation = null;
        _deadlineTask = null;
    }

    private void CleanupSession()
    {
        CancelCaptureDeadline();
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
    }

    private sealed class SessionOperationException : Exception
    {
        public SessionOperationException(SessionErrorKind kind)
            : base("La operación de sesión no pudo completarse.")
        {
            Kind = kind;
        }

        public SessionErrorKind Kind { get; }
    }
}
