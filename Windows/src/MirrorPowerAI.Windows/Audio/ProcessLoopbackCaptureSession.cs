using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MirrorPowerAI.Windows.Platform;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace MirrorPowerAI.Windows.Audio;

/// <summary>Resolves one persisted application selection as a monitorable loopback target.</summary>
internal sealed class ProcessAudioEndpointProvider : IAudioEndpointProvider
{
    private readonly string _processName;
    private readonly int? _preferredProcessId;

    /// <summary>Initializes a process target without opening any audio stream.</summary>
    /// <param name="processName">Executable name without a path.</param>
    /// <param name="preferredProcessId">Last selected process identifier.</param>
    internal ProcessAudioEndpointProvider(string processName, int? preferredProcessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        _processName = processName;
        _preferredProcessId = preferredProcessId is > 0 ? preferredProcessId : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<AudioEndpoint> GetActiveRenderEndpoints()
    {
        try
        {
            return [GetRenderEndpoint(deviceId: null)];
        }
        catch (AudioCaptureException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public AudioEndpoint GetRenderEndpoint(string? deviceId)
    {
        _ = deviceId;
        if (!AudioApplicationProcessResolver.TryResolve(
                _processName,
                _preferredProcessId,
                out var process))
        {
            throw new AudioCaptureException(
                AudioCaptureFailure.DeviceUnavailable,
                "The selected audio application is not running.");
        }

        return new AudioEndpoint(
            process.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            process.DisplayName,
            WasSelectedAsDefault: false);
    }

    /// <inheritdoc />
    public bool IsEndpointActive(string deviceId)
    {
        if (!int.TryParse(
                deviceId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var processId))
        {
            return false;
        }

        return AudioApplicationProcessResolver.TryResolve(_processName, processId, out var process)
            && process.ProcessId == processId;
    }

    /// <inheritdoc />
    public bool IsDefaultRenderEndpoint(string deviceId)
    {
        _ = deviceId;
        return false;
    }
}

/// <summary>Creates process-tree loopback sessions for a resolved process endpoint.</summary>
internal sealed class ProcessLoopbackCaptureSessionFactory : ILoopbackCaptureSessionFactory
{
    /// <inheritdoc />
    public ILoopbackCaptureSession Create(AudioEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!int.TryParse(
                endpoint.Id,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var processId) || processId <= 0)
        {
            throw new AudioCaptureException(
                AudioCaptureFailure.DeviceUnavailable,
                "The selected audio application process is invalid.");
        }

        return new ProcessLoopbackCaptureSession(processId);
    }
}

/// <summary>
/// Captures only the render streams owned by one process and its child processes through the
/// Windows application-loopback API.
/// </summary>
internal sealed class ProcessLoopbackCaptureSession : ILoopbackCaptureSession
{
    internal const int RequiredWindowsBuild = 20348;
    internal const int CaptureSampleRate = 48_000;
    internal const int CaptureChannels = 2;
    internal const int CaptureBitsPerSample = 16;

    private static readonly TimeSpan CaptureThreadJoinTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(10);
    private readonly int _processId;
    private readonly AutoResetEvent _sampleReady = new(initialState: false);
    private readonly ManualResetEvent _stopRequested = new(initialState: false);
    private readonly object _stopSignalSync = new();
    private readonly LoopbackCaptureSessionOwnership _ownership = new();
    private Thread? _captureThread;
    private int _disposed;

    /// <summary>Initializes a stopped process-tree capture session.</summary>
    /// <param name="processId">Root process identifier to include.</param>
    internal ProcessLoopbackCaptureSession(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(processId, 1);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, RequiredWindowsBuild))
        {
            throw new PlatformNotSupportedException(
                $"Application audio capture requires Windows build {RequiredWindowsBuild} or later.");
        }

        _processId = processId;
        SourceFormat = new AudioSampleFormat(
            CaptureSampleRate,
            CaptureChannels,
            CaptureBitsPerSample,
            AudioSampleEncoding.PcmInteger);
    }

    /// <inheritdoc />
    public event EventHandler<LoopbackAudioDataEventArgs>? DataAvailable;

    /// <inheritdoc />
    public event EventHandler<LoopbackCaptureStoppedEventArgs>? Stopped;

    /// <inheritdoc />
    public AudioSampleFormat SourceFormat { get; }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_ownership.TryStart())
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            throw new InvalidOperationException("The process loopback session can only be started once.");
        }

        try
        {
            var captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "MirrorPowerAI process loopback",
            };
            captureThread.SetApartmentState(ApartmentState.MTA);
            Volatile.Write(ref _captureThread, captureThread);
            captureThread.Start();
        }
        catch
        {
            Volatile.Write(ref _captureThread, null);
            ReleaseAfterFailedStart();
            throw;
        }
    }

    /// <inheritdoc />
    public void RequestStop()
    {
        lock (_stopSignalSync)
        {
            if (_ownership.CanRequestStop())
            {
                _stopRequested.Set();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var teardownOwner = RequestDisposeAndStop();
        if (teardownOwner == LoopbackCaptureTeardownOwner.CallerBeforeStart)
        {
            ReleasePreStartResources();
            return;
        }

        if (teardownOwner == LoopbackCaptureTeardownOwner.CaptureThread)
        {
            JoinCaptureThreadIfForeign();
        }
    }

    private void CaptureLoop()
    {
        var captureThreadId = Environment.CurrentManagedThreadId;
        if (!_ownership.TryClaimCaptureThread(captureThreadId))
        {
            return;
        }

        Exception? failure = null;
        IAudioClient? audioClient = null;
        IProcessAudioCaptureClient? captureClient = null;
        var clientStarted = false;

        try
        {
            audioClient = ProcessLoopbackNative.ActivateAudioClient(_processId, ActivationTimeout);
            var waveFormat = new WaveFormat(CaptureSampleRate, CaptureBitsPerSample, CaptureChannels);
            var sessionGuid = Guid.Empty;
            ThrowIfFailed(audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback |
                AudioClientStreamFlags.EventCallback |
                AudioClientStreamFlags.AutoConvertPcm |
                AudioClientStreamFlags.SrcDefaultQuality,
                0,
                0,
                waveFormat,
                ref sessionGuid));

            var captureServiceId = typeof(IProcessAudioCaptureClient).GUID;
            ThrowIfFailed(audioClient.GetService(captureServiceId, out var captureService));
            captureClient = captureService as IProcessAudioCaptureClient
                ?? throw new InvalidCastException("Windows returned an incompatible audio capture service.");
            ThrowIfFailed(audioClient.SetEventHandle(_sampleReady.SafeWaitHandle.DangerousGetHandle()));

            ThrowIfFailed(audioClient.Start());
            clientStarted = true;
            var waitHandles = new WaitHandle[] { _stopRequested, _sampleReady };
            while (WaitHandle.WaitAny(waitHandles) == 1)
            {
                ReadAvailablePackets(captureClient);
            }

            ReadAvailablePackets(captureClient);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (_ownership.TryBeginCaptureThreadTeardown(captureThreadId))
            {
                failure ??= ReleaseOwnedResources(
                    captureClient,
                    audioClient,
                    clientStarted,
                    captureThreadId);
            }

            Volatile.Write(ref _captureThread, null);
            Stopped?.Invoke(this, new LoopbackCaptureStoppedEventArgs(failure));
        }
    }

    private void ReadAvailablePackets(IProcessAudioCaptureClient captureClient)
    {
        while (true)
        {
            ThrowIfFailed(captureClient.GetNextPacketSize(out var framesAvailable));
            if (framesAvailable <= 0)
            {
                return;
            }

            ThrowIfFailed(captureClient.GetBuffer(
                out var buffer,
                out framesAvailable,
                out var flags,
                out _,
                out _));
            try
            {
                var byteCount = checked(framesAvailable * SourceFormat.BlockAlign);
                var copied = new byte[byteCount];
                if ((flags & AudioClientBufferFlags.Silent) != AudioClientBufferFlags.Silent &&
                    buffer != nint.Zero &&
                    byteCount > 0)
                {
                    Marshal.Copy(buffer, copied, 0, byteCount);
                }

                try
                {
                    DataAvailable?.Invoke(this, new LoopbackAudioDataEventArgs(copied));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(copied);
                }
            }
            finally
            {
                ThrowIfFailed(captureClient.ReleaseBuffer(framesAvailable));
            }
        }
    }

    private LoopbackCaptureTeardownOwner RequestDisposeAndStop()
    {
        lock (_stopSignalSync)
        {
            var owner = _ownership.RequestDispose(Environment.CurrentManagedThreadId);
            if (owner == LoopbackCaptureTeardownOwner.CaptureThread && _ownership.CanRequestStop())
            {
                _stopRequested.Set();
            }

            return owner;
        }
    }

    private void ReleaseAfterFailedStart()
    {
        var threadId = Environment.CurrentManagedThreadId;
        if (_ownership.TryBeginFailedStartTeardown(threadId))
        {
            _ = ReleaseOwnedResources(null, null, clientStarted: false, threadId);
        }
    }

    private void ReleasePreStartResources()
    {
        var threadId = Environment.CurrentManagedThreadId;
        _ = ReleaseOwnedResources(null, null, clientStarted: false, threadId);
    }

    private Exception? ReleaseOwnedResources(
        IProcessAudioCaptureClient? captureClient,
        IAudioClient? audioClient,
        bool clientStarted,
        int threadId)
    {
        Exception? failure = null;

        void Execute(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        try
        {
            if (clientStarted && audioClient is not null)
            {
                Execute(() => ThrowIfFailed(audioClient.Stop()));
            }

            Execute(() => ReleaseComObject(captureClient));
            Execute(() => ReleaseComObject(audioClient));
            Execute(_sampleReady.Dispose);
            lock (_stopSignalSync)
            {
                Execute(_stopRequested.Dispose);
            }
        }
        finally
        {
            _ownership.CompleteTeardown(threadId);
        }

        return failure;
    }

    private void JoinCaptureThreadIfForeign()
    {
        var captureThread = Volatile.Read(ref _captureThread);
        if (captureThread is null || captureThread == Thread.CurrentThread)
        {
            return;
        }

        try
        {
            if (!captureThread.Join(CaptureThreadJoinTimeout))
            {
                _ownership.RecordJoinTimeout();
            }
        }
        catch (ThreadStateException)
        {
            _ownership.RecordJoinTimeout();
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }
}

/// <summary>Minimal public projection of the native WASAPI capture service.</summary>
[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IProcessAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(
        out nint data,
        out int framesAvailable,
        out AudioClientBufferFlags flags,
        out long devicePosition,
        out long performanceCounterPosition);

    [PreserveSig]
    int ReleaseBuffer(int framesRead);

    [PreserveSig]
    int GetNextPacketSize(out int framesAvailable);
}

/// <summary>Creates the process-loopback activation payload and completes its asynchronous COM activation.</summary>
internal static class ProcessLoopbackNative
{
    internal const string VirtualProcessLoopbackDevice = "VAD\\Process_Loopback";
    internal const ushort VariantTypeBlob = 65;

    /// <summary>Activates an <see cref="IAudioClient"/> restricted to one process tree.</summary>
    /// <param name="processId">Root process identifier to include.</param>
    /// <param name="timeout">Maximum time allowed for the Windows activation callback.</param>
    /// <returns>The activated COM audio client.</returns>
    internal static IAudioClient ActivateAudioClient(int processId, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(processId, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var activation = CreateActivationParameters(processId);
        var activationPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParameters>());
        Marshal.StructureToPtr(activation, activationPointer, fDeleteOld: false);
        var completion = new ProcessLoopbackActivationCompletion(activationPointer);
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            var variant = new ProcessLoopbackPropVariant
            {
                VariantType = VariantTypeBlob,
                Blob = new ProcessLoopbackBlob
                {
                    Size = checked((uint)Marshal.SizeOf<AudioClientActivationParameters>()),
                    Data = activationPointer,
                },
            };
            var interfaceId = typeof(IAudioClient).GUID;
            var result = ActivateAudioInterfaceAsync(
                VirtualProcessLoopbackDevice,
                ref interfaceId,
                ref variant,
                completion,
                out operation);
            if (result < 0)
            {
                completion.Abort();
                Marshal.ThrowExceptionForHR(result);
            }

            if (!completion.Task.Wait(timeout))
            {
                throw new TimeoutException("Windows did not activate process audio capture in time.");
            }

            return completion.Task.GetAwaiter().GetResult();
        }
        finally
        {
            if (operation is not null && Marshal.IsComObject(operation))
            {
                _ = Marshal.FinalReleaseComObject(operation);
            }
        }
    }

    /// <summary>Creates the exact 12-byte activation layout defined by audioclientactivationparams.h.</summary>
    /// <param name="processId">Target process identifier.</param>
    /// <returns>Native-compatible activation parameters.</returns>
    internal static AudioClientActivationParameters CreateActivationParameters(int processId) =>
        new()
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParameters = new AudioClientProcessLoopbackParameters
            {
                TargetProcessId = checked((uint)processId),
                Mode = ProcessLoopbackMode.IncludeTargetProcessTree,
            },
        };

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid interfaceId,
        ref ProcessLoopbackPropVariant activationParameters,
        [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceCompletionHandler completionHandler,
        [MarshalAs(UnmanagedType.Interface)] out IActivateAudioInterfaceAsyncOperation activationOperation);
}

/// <summary>Managed COM callback that retains the native activation payload until Windows is finished.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ProcessLoopbackActivationCompletion : IActivateAudioInterfaceCompletionHandler
{
    private readonly TaskCompletionSource<IAudioClient> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _activationPointer;

    /// <summary>Initializes a callback that owns an unmanaged activation payload.</summary>
    /// <param name="activationPointer">Allocated activation structure retained until completion.</param>
    internal ProcessLoopbackActivationCompletion(nint activationPointer)
    {
        if (activationPointer == nint.Zero)
        {
            throw new ArgumentException("An activation payload is required.", nameof(activationPointer));
        }

        _activationPointer = activationPointer;
    }

    /// <summary>Gets the asynchronous activated client.</summary>
    internal Task<IAudioClient> Task => _completion.Task;

    /// <summary>Handles the native completion callback without letting managed exceptions cross COM.</summary>
    /// <param name="activateOperation">Completed Windows activation operation.</param>
    public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        object? activatedInterface = null;
        try
        {
            activateOperation.GetActivateResult(out var activationResult, out activatedInterface);
            if (activationResult < 0)
            {
                Marshal.ThrowExceptionForHR(activationResult);
            }

            if (activatedInterface is not IAudioClient audioClient)
            {
                throw new InvalidCastException("Windows returned an incompatible process audio client.");
            }

            _completion.TrySetResult(audioClient);
            activatedInterface = null;
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
        finally
        {
            if (activatedInterface is not null && Marshal.IsComObject(activatedInterface))
            {
                _ = Marshal.FinalReleaseComObject(activatedInterface);
            }

            ReleaseActivationPointer();
        }
    }

    /// <summary>Releases the payload when activation fails before Windows accepts the callback.</summary>
    internal void Abort()
    {
        ReleaseActivationPointer();
        _completion.TrySetException(
            new AudioCaptureException(
                AudioCaptureFailure.BackendFailure,
                "Process audio activation failed before completion."));
    }

    private void ReleaseActivationPointer()
    {
        var pointer = Interlocked.Exchange(ref _activationPointer, nint.Zero);
        if (pointer != nint.Zero)
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}

internal enum AudioClientActivationType
{
    Default,
    ProcessLoopback,
}

internal enum ProcessLoopbackMode
{
    IncludeTargetProcessTree,
    ExcludeTargetProcessTree,
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParameters
{
    internal uint TargetProcessId;
    internal ProcessLoopbackMode Mode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParameters
{
    internal AudioClientActivationType ActivationType;
    internal AudioClientProcessLoopbackParameters ProcessLoopbackParameters;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessLoopbackBlob
{
    internal uint Size;
    internal nint Data;
}

[StructLayout(LayoutKind.Explicit)]
internal struct ProcessLoopbackPropVariant
{
    [FieldOffset(0)]
    internal ushort VariantType;

    [FieldOffset(8)]
    internal ProcessLoopbackBlob Blob;
}
