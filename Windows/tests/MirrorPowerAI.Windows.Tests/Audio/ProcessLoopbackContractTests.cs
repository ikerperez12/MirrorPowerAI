using System.Diagnostics;
using System.Runtime.InteropServices;
using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Windows.Audio;

namespace MirrorPowerAI.Windows.Tests.Audio;

public sealed class ProcessLoopbackContractTests
{
    [Fact]
    public void CreateActivationParameters_UsesDocumentedTwelveByteProcessTreeLayout()
    {
        var parameters = ProcessLoopbackNative.CreateActivationParameters(1234);

        Assert.Equal(12, Marshal.SizeOf<AudioClientActivationParameters>());
        Assert.Equal(8, Marshal.SizeOf<AudioClientProcessLoopbackParameters>());
        Assert.Equal(0, Marshal.OffsetOf<AudioClientActivationParameters>(
            nameof(AudioClientActivationParameters.ActivationType)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<AudioClientActivationParameters>(
            nameof(AudioClientActivationParameters.ProcessLoopbackParameters)).ToInt32());
        Assert.Equal(AudioClientActivationType.ProcessLoopback, parameters.ActivationType);
        Assert.Equal(1234u, parameters.ProcessLoopbackParameters.TargetProcessId);
        Assert.Equal(
            ProcessLoopbackMode.IncludeTargetProcessTree,
            parameters.ProcessLoopbackParameters.Mode);
        Assert.Equal("VAD\\Process_Loopback", ProcessLoopbackNative.VirtualProcessLoopbackDevice);
    }

    [Fact]
    public void ProcessEndpointProvider_CurrentProcess_ResolvesWithoutOpeningAudio()
    {
        using var current = Process.GetCurrentProcess();
        var provider = new ProcessAudioEndpointProvider(current.ProcessName, current.Id);

        var endpoint = provider.GetRenderEndpoint(deviceId: null);

        Assert.Equal(current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), endpoint.Id);
        Assert.False(endpoint.WasSelectedAsDefault);
        Assert.True(provider.IsEndpointActive(endpoint.Id));
        Assert.False(provider.IsDefaultRenderEndpoint(endpoint.Id));
    }

    [Fact]
    public void ProcessEndpointProvider_MissingApplication_FailsClosed()
    {
        var processName = $"mirrorpowerai-missing-{Guid.NewGuid():N}";
        var provider = new ProcessAudioEndpointProvider(processName, preferredProcessId: null);

        var exception = Assert.Throws<AudioCaptureException>(
            () => provider.GetRenderEndpoint(deviceId: null));

        Assert.Equal(AudioCaptureFailure.SourceUnavailable, exception.Failure);
        Assert.Empty(provider.GetActiveRenderEndpoints());
    }

    [Fact]
    public void ProcessLoopbackFactory_InvalidEndpoint_FailsBeforeNativeActivation()
    {
        var factory = new ProcessLoopbackCaptureSessionFactory();
        var endpoint = new AudioEndpoint("not-a-process", "Invalid", WasSelectedAsDefault: false);

        var exception = Assert.Throws<AudioCaptureException>(() => factory.Create(endpoint));

        Assert.Equal(AudioCaptureFailure.SourceUnavailable, exception.Failure);
    }

    [Fact]
    public void ProcessLoopbackSession_DisposeBeforeStart_IsBoundedAndIdempotent()
    {
        using var current = Process.GetCurrentProcess();
        var session = new ProcessLoopbackCaptureSession(current.Id);

        Assert.Equal(ProcessLoopbackCaptureSession.CaptureSampleRate, session.SourceFormat.SampleRate);
        Assert.Equal(ProcessLoopbackCaptureSession.CaptureChannels, session.SourceFormat.Channels);
        Assert.Equal(ProcessLoopbackCaptureSession.CaptureBitsPerSample, session.SourceFormat.BitsPerSample);

        session.Dispose();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Start());
    }
}
