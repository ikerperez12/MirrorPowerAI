using System.Runtime.InteropServices;
using MirrorPowerAI.Core.Configuration;
using MirrorPowerAI.Core.Sessions;
using MirrorPowerAI.Core.Transcription;

namespace MirrorPowerAI.Core.Tests;

public sealed class SessionControllerTests
{
    [Fact]
    public async Task ToggleAsync_NormalFlow_OwnsExpectedTransitionsAndResult()
    {
        var audio = new FakeAudioCaptureService();
        var transcription = new FakeTranscriptionService(TranscriptionProvider.LocalWhisper);
        var answers = new FakeAnswerService();
        var options = new MirrorPowerAIOptions { Context = "Contexto protegido" };
        await using var controller = CreateController(audio, transcription, answers, options);
        var transitions = new List<SessionState>();
        controller.StateChanged += (_, args) => transitions.Add(args.CurrentState);

        await controller.ToggleAsync();
        await controller.ToggleAsync();

        Assert.Equal(SessionState.ShowingResult, controller.State);
        Assert.NotNull(controller.LastResult);
        Assert.Equal("¿Qué hace el sistema?", controller.LastResult.Transcript);
        Assert.Equal(TranscriptionProvider.LocalWhisper, controller.LastResult.Provider);
        Assert.Equal("¿Qué hace el sistema?", answers.LastQuestion);
        Assert.Equal("Contexto protegido", answers.LastContext);
        Assert.Equal(
            [
                SessionState.Capturing,
                SessionState.Transcribing,
                SessionState.RequestingAnswer,
                SessionState.ShowingResult,
            ],
            transitions);
    }

    [Fact]
    public async Task ToggleAsync_DoubleToggle_StopsExactlyOneCapture()
    {
        var audio = new FakeAudioCaptureService();
        await using var controller = CreateController(audio);

        await controller.ToggleAsync();
        await controller.ToggleAsync();

        Assert.Equal(1, audio.StartCount);
        Assert.Equal(1, audio.StopCount);
    }

    [Fact]
    public async Task ToggleAsync_ConcurrentStartup_ThrowsBusy()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var audio = new FakeAudioCaptureService
        {
            StartHandler = async cancellationToken =>
            {
                entered.SetResult(true);
                await release.Task.WaitAsync(cancellationToken);
            },
        };
        await using var controller = CreateController(audio);

        var firstToggle = controller.ToggleAsync();
        await entered.Task;

        await Assert.ThrowsAsync<SessionBusyException>(() => controller.ToggleAsync());
        release.SetResult(true);
        await firstToggle;
    }

    [Fact]
    public async Task ToggleAsync_WhileTranscribing_ThrowsBusyWithoutFallback()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transcription = new FakeTranscriptionService(TranscriptionProvider.LocalWhisper)
        {
            Handler = async (_, _, cancellationToken) =>
            {
                entered.SetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                return "pregunta";
            },
        };
        await using var controller = CreateController(transcription: transcription);
        await controller.ToggleAsync();

        var processing = controller.ToggleAsync();
        await entered.Task;

        await Assert.ThrowsAsync<SessionBusyException>(() => controller.ToggleAsync());
        release.SetResult(true);
        await processing;
        Assert.Equal(1, transcription.CallCount);
    }

    [Fact]
    public async Task CancelAsync_DuringCapture_DiscardsAndZeroesAudio()
    {
        var raw = new byte[] { 1, 2, 3, 4 };
        var captured = TestAudio.Create(raw);
        Assert.True(MemoryMarshal.TryGetArray(captured.WavData, out var ownedBuffer));
        var audio = new FakeAudioCaptureService { Audio = captured };
        await using var controller = CreateController(audio);
        await controller.ToggleAsync();

        await controller.CancelAsync();

        Assert.Equal(SessionState.Idle, controller.State);
        Assert.Equal(1, audio.StopCount);
        Assert.All(ownedBuffer.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CancelAsync_DuringTranscription_CancelsProviderAndReturnsIdle()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transcription = new FakeTranscriptionService(TranscriptionProvider.LocalWhisper)
        {
            Handler = async (_, _, cancellationToken) =>
            {
                entered.SetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "unreachable";
            },
        };
        await using var controller = CreateController(transcription: transcription);
        await controller.ToggleAsync();
        var processing = controller.ToggleAsync();
        await entered.Task;

        await controller.CancelAsync();
        await processing;

        Assert.Equal(SessionState.Idle, controller.State);
        Assert.Null(controller.LastFailure);
        Assert.Null(controller.LastResult);
    }

    [Fact]
    public async Task ToggleAsync_ProviderFailure_StoresOnlySafeError()
    {
        const string sensitiveTranscript = "contenido que no puede aparecer";
        var transcription = new FakeTranscriptionService(TranscriptionProvider.LocalWhisper)
        {
            Handler = (_, _, _) => throw new InvalidOperationException(sensitiveTranscript),
        };
        await using var controller = CreateController(transcription: transcription);

        await controller.ToggleAsync();
        await controller.ToggleAsync();

        Assert.Equal(SessionState.Error, controller.State);
        Assert.Equal(SessionErrorKind.Unexpected, controller.LastFailure?.Kind);
        Assert.DoesNotContain(
            sensitiveTranscript,
            controller.LastFailure?.Message ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleAsync_SelectedProviderUnavailable_DoesNotStartOrFallback()
    {
        var audio = new FakeAudioCaptureService();
        var cloudOnly = new FakeTranscriptionService(TranscriptionProvider.GeminiAudio);
        var options = new MirrorPowerAIOptions { Provider = TranscriptionProvider.LocalWhisper };
        await using var controller = CreateController(audio, cloudOnly, options: options);

        await controller.ToggleAsync();

        Assert.Equal(SessionState.Error, controller.State);
        Assert.Equal(SessionErrorKind.ProviderUnavailable, controller.LastFailure?.Kind);
        Assert.Equal(0, audio.StartCount);
        Assert.Equal(0, cloudOnly.CallCount);
    }

    [Fact]
    public async Task ToggleAsync_StartFailsAfterCaptureBegan_StopsCaptureAndKeepsSafeFailure()
    {
        var audio = new FakeAudioCaptureService
        {
            CaptureBeforeStartHandler = true,
            StartHandler = _ => throw new InvalidOperationException("sensitive device detail"),
        };
        await using var controller = CreateController(audio);

        await controller.ToggleAsync();

        Assert.False(audio.IsCapturing);
        Assert.Equal(1, audio.StopCount);
        Assert.Equal(SessionState.Error, controller.State);
        Assert.DoesNotContain(
            "sensitive device detail",
            controller.LastFailure?.Message ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureDeadline_WhenReached_StopsAndProcessesAutomatically()
    {
        var options = new MirrorPowerAIOptions
        {
            MaxCaptureDuration = TimeSpan.FromMilliseconds(30),
        };
        var audio = new FakeAudioCaptureService
        {
            Audio = new(new byte[] { 1, 2, 3, 4 }, TimeSpan.FromMilliseconds(20)),
        };
        await using var controller = CreateController(audio, options: options);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StateChanged += (_, args) =>
        {
            if (args.CurrentState == SessionState.ShowingResult)
            {
                completed.TrySetResult(true);
            }
        };

        await controller.ToggleAsync();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SessionState.ShowingResult, controller.State);
        Assert.Equal(1, audio.StopCount);
    }

    [Fact]
    public async Task ToggleAsync_EmptyAudio_ProducesTypedFailure()
    {
        var audio = new FakeAudioCaptureService
        {
            Audio = new(Array.Empty<byte>(), TimeSpan.Zero, containsAudibleSignal: false),
        };
        await using var controller = CreateController(audio);

        await controller.ToggleAsync();
        await controller.ToggleAsync();

        Assert.Equal(SessionErrorKind.EmptyAudio, controller.LastFailure?.Kind);
        Assert.Equal(SessionState.Error, controller.State);
    }

    [Fact]
    public async Task ToggleAsync_AudioBeyondConfiguredDuration_IsRejected()
    {
        var options = new MirrorPowerAIOptions { MaxCaptureDuration = TimeSpan.FromSeconds(10) };
        var audio = new FakeAudioCaptureService
        {
            Audio = new(new byte[] { 1, 2, 3, 4 }, TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(1)),
        };
        await using var controller = CreateController(audio, options: options);

        await controller.ToggleAsync();
        await controller.ToggleAsync();

        Assert.Equal(SessionErrorKind.EmptyAudio, controller.LastFailure?.Kind);
        Assert.Equal(SessionState.Error, controller.State);
    }

    [Fact]
    public async Task StateChanged_ThrowingSubscriber_DoesNotCorruptSession()
    {
        await using var controller = CreateController();
        controller.StateChanged += static (_, _) => throw new InvalidOperationException("UI failure");

        await controller.ToggleAsync();
        await controller.ToggleAsync();

        Assert.Equal(SessionState.ShowingResult, controller.State);
        Assert.NotNull(controller.LastResult);
    }

    private static SessionController CreateController(
        FakeAudioCaptureService? audio = null,
        FakeTranscriptionService? transcription = null,
        FakeAnswerService? answers = null,
        MirrorPowerAIOptions? options = null) =>
        new(
            audio ?? new FakeAudioCaptureService(),
            [transcription ?? new FakeTranscriptionService(TranscriptionProvider.LocalWhisper)],
            answers ?? new FakeAnswerService(),
            options ?? new MirrorPowerAIOptions());
}
