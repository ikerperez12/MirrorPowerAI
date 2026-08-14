using MirrorPowerAI.Windows.Platform;

namespace MirrorPowerAI.Windows.Tests.Platform;

public sealed class Win32AdapterTests
{
    [Fact]
    public void OverlayProtection_SetFailure_FailsClosedWithoutReadingOrRollingBack()
    {
        var native = new FakeDisplayAffinityApi { SetResult = false, LastError = 5 };
        var service = new OverlayProtectionService(native);

        var protectedOverlay = service.TryApplyAndVerify((nint)42);

        Assert.False(protectedOverlay);
        Assert.Equal([NativeMethods.WdaExcludeFromCapture], native.SetAffinities);
        Assert.Equal(0, native.GetCallCount);
    }

    [Fact]
    public void OverlayProtection_ReadbackMismatch_ClearsAffinityAndFailsClosed()
    {
        var native = new FakeDisplayAffinityApi
        {
            SetResult = true,
            GetResult = true,
            ReadAffinity = NativeMethods.WdaNone,
        };
        var service = new OverlayProtectionService(native);

        var protectedOverlay = service.TryApplyAndVerify((nint)42);

        Assert.False(protectedOverlay);
        Assert.Equal(
            [NativeMethods.WdaExcludeFromCapture, NativeMethods.WdaNone],
            native.SetAffinities);
        Assert.Equal(1, native.GetCallCount);
    }

    [Fact]
    public void OverlayProtection_VerifiedReadback_AllowsProtectedContent()
    {
        var native = new FakeDisplayAffinityApi
        {
            SetResult = true,
            GetResult = true,
            ReadAffinity = NativeMethods.WdaExcludeFromCapture,
        };
        var service = new OverlayProtectionService(native);

        var protectedOverlay = service.TryApplyAndVerify((nint)42);

        Assert.True(protectedOverlay);
        Assert.Equal([NativeMethods.WdaExcludeFromCapture], native.SetAffinities);
        Assert.True(service.IsProtected((nint)42));
    }

    [Fact]
    public void OverlayProtection_ZeroHandle_FailsBeforeNativeCalls()
    {
        var native = new FakeDisplayAffinityApi();
        var service = new OverlayProtectionService(native);

        Assert.False(service.TryApplyAndVerify(nint.Zero));
        Assert.False(service.IsProtected(nint.Zero));
        Assert.Empty(native.SetAffinities);
        Assert.Equal(0, native.GetCallCount);
    }

    [Fact]
    public void GlobalHotKey_AlreadyRegistered_ReportsConflictAndPreservesExactShortcut()
    {
        var native = new FakeGlobalHotKeyApi
        {
            RegisterResult = false,
            LastError = NativeMethods.ErrorHotKeyAlreadyRegistered,
        };

        var registration = GlobalHotKeyService.Register((nint)99, native);

        Assert.False(registration.IsRegistered);
        Assert.Equal(NativeMethods.ErrorHotKeyAlreadyRegistered, registration.Win32Error);
        Assert.Contains("already registered", registration.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((nint)99, native.LastWindowHandle);
        Assert.Equal(GlobalHotKeyService.HotKeyIdentifier, native.LastIdentifier);
        Assert.Equal(
            NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModNoRepeat,
            native.LastModifiers);
        Assert.Equal(NativeMethods.VirtualKeyL, native.LastVirtualKey);
    }

    [Fact]
    public void GlobalHotKey_RegistrationSuccess_ReturnsSuccessWithoutErrorDetails()
    {
        var native = new FakeGlobalHotKeyApi { RegisterResult = true };

        var registration = GlobalHotKeyService.Register((nint)99, native);

        Assert.True(registration.IsRegistered);
        Assert.Equal(0, registration.Win32Error);
        Assert.Empty(registration.Message);
    }

    [Fact]
    public void GlobalHotKey_Unregister_UsesTheSameStableIdentifier()
    {
        var native = new FakeGlobalHotKeyApi { UnregisterResult = true };

        var unregistered = GlobalHotKeyService.Unregister((nint)99, native);

        Assert.True(unregistered);
        Assert.Equal((nint)99, native.LastUnregisterWindowHandle);
        Assert.Equal(GlobalHotKeyService.HotKeyIdentifier, native.LastUnregisterIdentifier);
    }

    [Fact]
    public void SingleInstance_AcquiredMutex_ReleasesAndDisposesOnce()
    {
        var mutex = new FakeInstanceMutex { AcquireResult = true };
        var factory = new FakeInstanceMutexFactory(mutex);

        var acquired = SingleInstanceGuard.TryAcquire(factory, "S-1-5-21-test", out var guard);

        Assert.True(acquired);
        Assert.NotNull(guard);
        Assert.Equal("Local\\MirrorPowerAI.Windows.S-1-5-21-test", factory.CreatedName);
        guard.Dispose();
        guard.Dispose();
        Assert.Equal(1, mutex.ReleaseCount);
        Assert.Equal(1, mutex.DisposeCount);
    }

    [Fact]
    public void SingleInstance_ContendedMutex_DisposesTemporaryHandleAndReturnsNull()
    {
        var mutex = new FakeInstanceMutex { AcquireResult = false };
        var factory = new FakeInstanceMutexFactory(mutex);

        var acquired = SingleInstanceGuard.TryAcquire(factory, "scope", out var guard);

        Assert.False(acquired);
        Assert.Null(guard);
        Assert.Equal(0, mutex.ReleaseCount);
        Assert.Equal(1, mutex.DisposeCount);
    }

    [Fact]
    public void SingleInstance_AcquireFailure_DisposesHandleBeforePropagating()
    {
        var mutex = new FakeInstanceMutex { AcquireException = new InvalidOperationException("simulated") };
        var factory = new FakeInstanceMutexFactory(mutex);

        _ = Assert.Throws<InvalidOperationException>(() =>
            SingleInstanceGuard.TryAcquire(factory, "scope", out _));

        Assert.Equal(1, mutex.DisposeCount);
        Assert.Equal(0, mutex.ReleaseCount);
    }

    private sealed class FakeDisplayAffinityApi : IWindowDisplayAffinityApi
    {
        public bool SetResult { get; init; } = true;

        public bool GetResult { get; init; } = true;

        public uint ReadAffinity { get; init; } = NativeMethods.WdaExcludeFromCapture;

        public int LastError { get; init; }

        public List<uint> SetAffinities { get; } = [];

        public int GetCallCount { get; private set; }

        public bool SetWindowDisplayAffinity(nint windowHandle, uint affinity)
        {
            SetAffinities.Add(affinity);
            return SetResult;
        }

        public bool GetWindowDisplayAffinity(nint windowHandle, out uint affinity)
        {
            GetCallCount++;
            affinity = ReadAffinity;
            return GetResult;
        }

        public int GetLastError() => LastError;
    }

    private sealed class FakeGlobalHotKeyApi : IGlobalHotKeyApi
    {
        public bool RegisterResult { get; init; }

        public int LastError { get; init; }

        public nint LastWindowHandle { get; private set; }

        public int LastIdentifier { get; private set; }

        public uint LastModifiers { get; private set; }

        public uint LastVirtualKey { get; private set; }

        public bool UnregisterResult { get; init; }

        public nint LastUnregisterWindowHandle { get; private set; }

        public int LastUnregisterIdentifier { get; private set; }

        public bool RegisterHotKey(nint windowHandle, int identifier, uint modifiers, uint virtualKey)
        {
            LastWindowHandle = windowHandle;
            LastIdentifier = identifier;
            LastModifiers = modifiers;
            LastVirtualKey = virtualKey;
            return RegisterResult;
        }

        public bool UnregisterHotKey(nint windowHandle, int identifier)
        {
            LastUnregisterWindowHandle = windowHandle;
            LastUnregisterIdentifier = identifier;
            return UnregisterResult;
        }

        public int GetLastError() => LastError;
    }

    private sealed class FakeInstanceMutexFactory(FakeInstanceMutex mutex) : IInstanceMutexFactory
    {
        public string? CreatedName { get; private set; }

        public IInstanceMutex Create(string name)
        {
            CreatedName = name;
            return mutex;
        }
    }

    private sealed class FakeInstanceMutex : IInstanceMutex
    {
        public bool AcquireResult { get; init; }

        public Exception? AcquireException { get; init; }

        public int ReleaseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool TryAcquire()
        {
            if (AcquireException is not null)
            {
                throw AcquireException;
            }

            return AcquireResult;
        }

        public void Release() => ReleaseCount++;

        public void Dispose() => DisposeCount++;
    }
}
