using System.Diagnostics;
using System.Runtime.InteropServices;
using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Windows.Audio;

namespace MirrorPowerAI.Windows.Tests.Audio;

public sealed class ProcessLoopbackContractTests
{
    [Fact]
    public void ProcessResolver_ChildAudioSession_UsesSameExecutableRoot()
    {
        var processTree = new Dictionary<int, ProcessTreeEntry>
        {
            [100] = new ProcessTreeEntry(100, 1, "chrome"),
            [200] = new ProcessTreeEntry(200, 100, "chrome"),
            [300] = new ProcessTreeEntry(300, 200, "chrome"),
        };

        var root = AudioApplicationProcessResolver.ResolveSameExecutableRoot(
            processId: 300,
            processName: "chrome.exe",
            processTree);

        Assert.Equal(100, root);
    }

    [Fact]
    public void ProcessResolver_DoesNotCrossExecutableBoundary()
    {
        var processTree = new Dictionary<int, ProcessTreeEntry>
        {
            [100] = new ProcessTreeEntry(100, 1, "generic-host"),
            [200] = new ProcessTreeEntry(200, 100, "msedgewebview2"),
            [300] = new ProcessTreeEntry(300, 200, "msedgewebview2"),
        };

        var root = AudioApplicationProcessResolver.ResolveSameExecutableRoot(
            processId: 300,
            processName: "msedgewebview2",
            processTree);

        Assert.Equal(200, root);
    }

    [Fact]
    public void ProcessResolver_NewTeamsWebViewChild_UsesTeamsRootOnly()
    {
        var processTree = new Dictionary<int, ProcessTreeEntry>
        {
            [100] = new ProcessTreeEntry(100, 1, "explorer"),
            [200] = new ProcessTreeEntry(200, 100, "ms-teams"),
            [300] = new ProcessTreeEntry(300, 200, "msedgewebview2"),
            [400] = new ProcessTreeEntry(400, 300, "msedgewebview2"),
        };

        var root = AudioApplicationProcessResolver.ResolveSameExecutableRoot(
            processId: 400,
            processName: "msedgewebview2.exe",
            processTree);

        Assert.Equal(200, root);
    }

    [Fact]
    public void ProcessResolver_StopsOnCycles()
    {
        var processTree = new Dictionary<int, ProcessTreeEntry>
        {
            [100] = new ProcessTreeEntry(100, 200, "discord"),
            [200] = new ProcessTreeEntry(200, 100, "discord"),
        };

        var root = AudioApplicationProcessResolver.ResolveSameExecutableRoot(
            processId: 100,
            processName: "discord",
            processTree);

        Assert.Equal(100, root);
    }

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
