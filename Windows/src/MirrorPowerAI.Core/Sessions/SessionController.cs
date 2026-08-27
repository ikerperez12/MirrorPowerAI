using System.Threading.Channels;
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
    private const int SegmentQueueCapacity = 4;
    private const int MaximumConversationCharacters = 8_000;
    private const int MaximumPendingFragmentCharacters = 2_048;

    private readonly IAudioCaptureService _audioCaptureService;
    private readonly Dictionary<TranscriptionProvider, ITranscriptionService> _transcriptionServices;
    private readonly IAnswerService _answerService;
    private readonly MirrorPowerAIOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly IAudioSegmentSource? _segmentSource;

    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenSource? _deadlineCancellation;
    private Task? _deadlineTask;
    private Task? _activeOperation;
    private Channel<SegmentWorkItem>? _segmentChannel;
    private Task? _segmentWorker;
    private readonly Queue<string> _recentTranscripts = new();
    private int _recentTranscriptCharacters;
    private string? _pendingQuestionFragment;
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
        _segmentSource = audioCaptureService as IAudioSegmentSource;

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
    /// Starts listening from an inactive state or pauses the active continuous listener.
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
            if (_activeOperation is { IsCompleted: false })
            {
                throw new SessionBusyException();
            }

            switch (_state)
            {
                case SessionState.Idle:
                case SessionState.ShowingResult:
                case SessionState.Error:
                    await StartCaptureAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case SessionState.Paused:
                    await StartCaptureAsync(cancellationToken, preserveConversation: true).ConfigureAwait(false);
                    break;

                case SessionState.Capturing:
                    operationToAwait = _segmentSource is null
                        ? StopAndProcessCommandAsync(cancellationToken)
                        : PauseContinuousCaptureAsync(cancellationToken);
                    _activeOperation = operationToAwait;
                    break;

                case SessionState.Transcribing:
                case SessionState.RequestingAnswer:
                    if (_segmentSource is null)
                    {
                        throw new SessionBusyException();
                    }

                    operationToAwait = PauseContinuousCaptureAsync(cancellationToken);
                    _activeOperation = operationToAwait;
                    break;

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
            try
            {
                await operationToAwait.ConfigureAwait(false);
            }
            finally
            {
                if (ReferenceEquals(_activeOperation, operationToAwait))
                {
                    _activeOperation = null;
                }
            }
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

    private async Task StartCaptureAsync(
        CancellationToken cancellationToken,
        bool preserveConversation = false)
    {
        if (!preserveConversation)
        {
            LastResult = null;
            ClearRecentTranscripts();
        }

        LastFailure = null;

        try
        {
            _options.EnsureValid();
            _ = GetSelectedTranscriptionService();

            _sessionCancellation?.Dispose();
            _sessionCancellation = new CancellationTokenSource();

            if (_segmentSource is not null)
            {
                _segmentChannel = Channel.CreateBounded<SegmentWorkItem>(new BoundedChannelOptions(SegmentQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });
                _segmentSource.SegmentAvailable += OnSegmentAvailable;
                _segmentWorker = ProcessSegmentsAsync(
                    _segmentChannel.Reader,
                    _sessionCancellation.Token);
            }

            await _audioCaptureService.StartAsync(cancellationToken).ConfigureAwait(false);
            TransitionTo(SessionState.Capturing);
            if (_segmentSource is null)
            {
                ScheduleCaptureDeadline(_options.MaxCaptureDuration);
            }
        }
        catch (OperationCanceledException)
        {
            await AbortContinuousStartAsync().ConfigureAwait(false);
            CleanupSession();
            TransitionTo(SessionState.Idle);
            throw;
        }
        catch (Exception exception) when (exception is not SessionBusyException)
        {
            await AbortContinuousStartAsync().ConfigureAwait(false);
            CleanupSession();
            SetFailure(exception);
        }
    }

    /// <summary>
    /// Cancels the pipeline created before the native capture start completed.  Startup can fail
    /// after the segment worker has already subscribed, so the worker must be awaited before its
    /// channel and session token are released.
    /// </summary>
    private async Task AbortContinuousStartAsync()
    {
        _sessionCancellation?.Cancel();
        _segmentChannel?.Writer.TryComplete();
        await AwaitSegmentWorkerAsync().ConfigureAwait(false);
        await StopCaptureAfterFailedStartAsync().ConfigureAwait(false);
        StopSegmentWorker();
    }

    private async Task StopAndProcessCommandAsync(CancellationToken cancellationToken)
    {
        TransitionTo(SessionState.Transcribing);
        CancelCaptureDeadline();
        await StopAndProcessAsync(
                _sessionCancellation ?? throw new InvalidOperationException("No hay una sesión activa."),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PauseContinuousCaptureAsync(CancellationToken cancellationToken)
    {
        var sessionCancellation = _sessionCancellation
            ?? throw new InvalidOperationException("No hay una sesión activa.");

        sessionCancellation.Cancel();
        _segmentChannel?.Writer.TryComplete();

        Exception? stopFailure = null;
        try
        {
            if (_audioCaptureService.IsCapturing)
            {
                using var finalAudio = await _audioCaptureService
                    .StopAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }

        try
        {
            await AwaitSegmentWorkerAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Pausing deliberately cancels any in-flight transcription or answer request.
        }
        finally
        {
            StopSegmentWorker();
            CleanupSession();
        }

        if (stopFailure is not null)
        {
            SetFailure(stopFailure);
            return;
        }

        LastFailure = null;
        // Pausing is an explicit conversational boundary. Keep completed rolling context, but do
        // not join an unfinished pre-pause fragment to unrelated speech after a later resume.
        _pendingQuestionFragment = null;
        TransitionTo(SessionState.Paused);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void OnSegmentAvailable(object? sender, AudioSegmentAvailableEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        var audio = eventArgs.Audio;
        var channel = Volatile.Read(ref _segmentChannel);
        if (channel is null || _sessionCancellation?.IsCancellationRequested != false ||
            !channel.Writer.TryWrite(new SegmentWorkItem(audio, eventArgs.ForcedBoundary)))
        {
            audio.Dispose();
        }
    }

    private async Task ProcessSegmentsAsync(
        ChannelReader<SegmentWorkItem> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var workItem in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using (workItem.Audio)
                {
                    try
                    {
                        await ProcessSegmentAsync(
                                workItem.Audio,
                                workItem.ForcedBoundary,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // A failed turn must not terminate the meeting listener.  Keep the last
                        // successful answer visible while surfacing a safe transient failure.
                        SetFailure(exception, preserveLastResult: true);
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            TransitionTo(SessionState.Capturing);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            while (reader.TryRead(out var discarded))
            {
                discarded.Audio.Dispose();
            }
        }
        catch (Exception exception)
        {
            SetFailure(exception);
            if (!cancellationToken.IsCancellationRequested)
            {
                TransitionTo(SessionState.Capturing);
            }
        }
    }

    private async Task ProcessSegmentAsync(
        CapturedAudio audio,
        bool forcedBoundary,
        CancellationToken cancellationToken)
    {
        if (!audio.ContainsAudibleSignal || audio.Duration <= TimeSpan.Zero)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var service = GetSelectedTranscriptionService();
        TransitionTo(SessionState.Transcribing);
        var language = _options.AutomaticLanguageDetection ? "auto" : _options.Language;
        var transcript = (await service
                .TranscribeAsync(audio, language, cancellationToken)
                .ConfigureAwait(false))
            .Trim();
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(transcript))
        {
            LastFailure = null;
            TransitionTo(SessionState.Capturing);
            return;
        }

        if (ConversationQuestionDetector.IsLikelyIncomplete(transcript, forcedBoundary))
        {
            _pendingQuestionFragment = AppendQuestionFragment(_pendingQuestionFragment, transcript);
            LastFailure = null;
            TransitionTo(SessionState.Capturing);
            return;
        }

        if (_pendingQuestionFragment is not null)
        {
            transcript = $"{_pendingQuestionFragment} {transcript}".Trim();
            _pendingQuestionFragment = null;
        }

        RememberTranscript(transcript);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConversationQuestionDetector.IsLikelyQuestion(transcript))
        {
            LastFailure = null;
            TransitionTo(SessionState.Capturing);
            return;
        }

        TransitionTo(SessionState.RequestingAnswer);
        var answerContext = BuildConversationContext();
        var answer = (await _answerService
                .AskAsync(transcript, answerContext, cancellationToken)
                .ConfigureAwait(false))
            .Trim();
        cancellationToken.ThrowIfCancellationRequested();

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
        TransitionTo(SessionState.Capturing);
    }

    private string BuildConversationContext()
    {
        var recent = string.Join(Environment.NewLine, _recentTranscripts);
        if (string.IsNullOrWhiteSpace(recent))
        {
            return _options.Context;
        }

        return string.IsNullOrWhiteSpace(_options.Context)
            ? $"Conversación reciente de la reunión (audio de salida):\n{recent}"
            : $"{_options.Context}\n\nConversación reciente de la reunión (audio de salida):\n{recent}";
    }

    private void RememberTranscript(string transcript)
    {
        _recentTranscripts.Enqueue(transcript);
        _recentTranscriptCharacters += transcript.Length;
        while (_recentTranscriptCharacters > MaximumConversationCharacters && _recentTranscripts.Count > 1)
        {
            _recentTranscriptCharacters -= _recentTranscripts.Dequeue().Length;
        }
    }

    private void ClearRecentTranscripts()
    {
        _recentTranscripts.Clear();
        _recentTranscriptCharacters = 0;
        _pendingQuestionFragment = null;
    }

    private static string AppendQuestionFragment(string? existing, string next)
    {
        var combined = string.IsNullOrWhiteSpace(existing)
            ? next.Trim()
            : $"{existing} {next.Trim()}";
        if (combined.Length <= MaximumPendingFragmentCharacters)
        {
            return combined;
        }

        // Keep both the original question cue and the newest words while bounding the in-memory
        // fragment.  Retaining only the suffix could remove "qué/how" and suppress detection.
        var half = MaximumPendingFragmentCharacters / 2;
        return $"{combined[..half]} … {combined[^half..]}";
    }

    private void StopSegmentWorker()
    {
        if (_segmentSource is not null)
        {
            _segmentSource.SegmentAvailable -= OnSegmentAvailable;
        }

        _segmentChannel?.Writer.TryComplete();
        _segmentChannel = null;
        _segmentWorker = null;
    }

    /// <summary>Waits for the current segment worker while keeping cleanup centralized.</summary>
    private async Task AwaitSegmentWorkerAsync()
    {
        var worker = _segmentWorker;
        if (worker is null)
        {
            return;
        }

        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session cancellation is the normal way a worker leaves the read loop.
        }
        catch (Exception)
        {
            // Segment failures are converted to LastFailure by the worker.  Teardown must still
            // finish even if a future adapter violates that contract.
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

            if (_activeOperation is { IsCompleted: false } activeOperation)
            {
                // A concurrent pause/stop is already tearing down the session. Reuse that task
                // instead of issuing a second native StopAsync call.
                operationToAwait = activeOperation;
            }
            else if (_state == SessionState.Capturing && _segmentSource is not null)
            {
                operationToAwait = CancelContinuousCaptureAsync();
                _activeOperation = operationToAwait;
            }
            else if (_state == SessionState.Capturing)
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
                operationToAwait = _segmentSource is not null
                    ? CancelContinuousCaptureAsync()
                    : _activeOperation;
                _activeOperation = operationToAwait;
            }
            else if (_state is SessionState.Paused or SessionState.ShowingResult or SessionState.Error)
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
            try
            {
                await operationToAwait.ConfigureAwait(false);
            }
            finally
            {
                if (ReferenceEquals(_activeOperation, operationToAwait))
                {
                    _activeOperation = null;
                }
            }
        }
    }

    private async Task CancelContinuousCaptureAsync()
    {
        _sessionCancellation?.Cancel();
        _segmentChannel?.Writer.TryComplete();

        try
        {
            if (_audioCaptureService.IsCapturing)
            {
                using var discardedAudio = await _audioCaptureService
                    .StopAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Explicit cancellation is best-effort and intentionally hides native audio details.
        }

        try
        {
            await AwaitSegmentWorkerAsync().ConfigureAwait(false);
        }
        finally
        {
            StopSegmentWorker();
            CleanupSession();
            LastResult = null;
            LastFailure = null;
            TransitionTo(SessionState.Idle);
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

    private void SetFailure(Exception exception, bool preserveLastResult = false)
    {
        if (!preserveLastResult)
        {
            LastResult = null;
        }
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

    private sealed record SegmentWorkItem(CapturedAudio Audio, bool ForcedBoundary);
}
