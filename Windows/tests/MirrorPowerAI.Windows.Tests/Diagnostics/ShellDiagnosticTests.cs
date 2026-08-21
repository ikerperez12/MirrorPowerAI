using MirrorPowerAI.Windows.Diagnostics;

namespace MirrorPowerAI.Windows.Tests.Diagnostics;

public sealed class ShellDiagnosticTests
{
    [Fact]
    public void Verify_AllShellResourcesAvailable_SucceedsAndReleasesEveryResource()
    {
        // Arrange
        var mutex = new FakeResource();
        var reacquiredMutex = new FakeResource();
        var tray = new FakeTrayResource(isVisible: true);
        var hotKey = new FakeHotKeyResource(isRegistered: true, windowHandle: (nint)1);
        var acquireCount = 0;
        var diagnostic = new ShellDiagnostic(
            () => ++acquireCount switch
            {
                1 => mutex,
                2 => reacquiredMutex,
                _ => null,
            },
            static () => true,
            () => tray,
            () => hotKey);

        // Act
        var result = diagnostic.Verify();

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.Equal(ShellDiagnosticFailure.None, result.Failure);
        Assert.Equal(2, acquireCount);
        Assert.Equal(1, mutex.DisposeCount);
        Assert.Equal(1, reacquiredMutex.DisposeCount);
        Assert.Equal(1, tray.DisposeCount);
        Assert.Equal(1, hotKey.DisposeCount);
        Assert.True(hotKey.UnregistrationSucceeded);
    }

    [Fact]
    public void Verify_ExistingInstance_FailsWithoutCreatingTrayOrHotKey()
    {
        // Arrange
        var trayCreated = false;
        var hotKeyCreated = false;
        var diagnostic = new ShellDiagnostic(
            static () => null,
            static () => true,
            () =>
            {
                trayCreated = true;
                return new FakeTrayResource(isVisible: true);
            },
            () =>
            {
                hotKeyCreated = true;
                return new FakeHotKeyResource(isRegistered: true, windowHandle: (nint)1);
            });

        // Act
        var result = diagnostic.Verify();

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Equal(ShellDiagnosticFailure.InstanceAlreadyRunning, result.Failure);
        Assert.False(trayCreated);
        Assert.False(hotKeyCreated);
    }

    [Fact]
    public void Verify_MutexExclusivityFailure_ReleasesThePrimaryMutexWithoutCreatingShellResources()
    {
        // Arrange
        var mutex = new FakeResource();
        var trayCreated = false;
        var hotKeyCreated = false;
        var diagnostic = new ShellDiagnostic(
            () => mutex,
            static () => false,
            () =>
            {
                trayCreated = true;
                return new FakeTrayResource(isVisible: true);
            },
            () =>
            {
                hotKeyCreated = true;
                return new FakeHotKeyResource(isRegistered: true, windowHandle: (nint)1);
            });

        // Act
        var result = diagnostic.Verify();

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Equal(ShellDiagnosticFailure.MutexExclusivityFailed, result.Failure);
        Assert.Equal(1, mutex.DisposeCount);
        Assert.False(trayCreated);
        Assert.False(hotKeyCreated);
    }

    [Fact]
    public void Verify_HiddenTray_FailsAndReleasesTrayAndMutexWithoutRegisteringHotKey()
    {
        // Arrange
        var mutex = new FakeResource();
        var tray = new FakeTrayResource(isVisible: false);
        var hotKeyCreated = false;
        var diagnostic = new ShellDiagnostic(
            () => mutex,
            static () => true,
            () => tray,
            () =>
            {
                hotKeyCreated = true;
                return new FakeHotKeyResource(isRegistered: true, windowHandle: (nint)1);
            });

        // Act
        var result = diagnostic.Verify();

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Equal(ShellDiagnosticFailure.TrayUnavailable, result.Failure);
        Assert.Equal(1, mutex.DisposeCount);
        Assert.Equal(1, tray.DisposeCount);
        Assert.False(hotKeyCreated);
    }

    [Fact]
    public void Verify_HotKeyRegistrationFailure_ReleasesEveryCreatedResource()
    {
        // Arrange
        var mutex = new FakeResource();
        var tray = new FakeTrayResource(isVisible: true);
        var hotKey = new FakeHotKeyResource(isRegistered: false, windowHandle: nint.Zero);
        var diagnostic = CreateDiagnostic(mutex, tray, hotKey, mutexContentionIsRejected: true);

        // Act
        var result = diagnostic.Verify();

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Equal(ShellDiagnosticFailure.HotKeyUnavailable, result.Failure);
        Assert.Equal(1, mutex.DisposeCount);
        Assert.Equal(1, tray.DisposeCount);
        Assert.Equal(1, hotKey.DisposeCount);
    }

    [Fact]
    public void Verify_HotKeyWithoutARealMessageWindow_FailsClosed()
    {
        // Arrange
        var mutex = new FakeResource();
        var tray = new FakeTrayResource(isVisible: true);
        var hotKey = new FakeHotKeyResource(isRegistered: true, windowHandle: nint.Zero);
        var diagnostic = CreateDiagnostic(mutex, tray, hotKey, mutexContentionIsRejected: true);

        // Act
        var result = diagnostic.Verify();

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Equal(ShellDiagnosticFailure.HotKeyUnavailable, result.Failure);
        Assert.Equal(1, mutex.DisposeCount);
        Assert.Equal(1, tray.DisposeCount);
        Assert.Equal(1, hotKey.DisposeCount);
    }

    [Fact]
    public void Verify_HotKeyUnregistrationFailure_FailsClosedAfterReleasingOtherResources()
    {
        // Arrange
        var mutex = new FakeResource();
        var tray = new FakeTrayResource(isVisible: true);
        var hotKey = new FakeHotKeyResource(
            isRegistered: true,
            windowHandle: (nint)1,
            unregistrationSucceededAfterDispose: false);
        var diagnostic = CreateDiagnostic(mutex, tray, hotKey, mutexContentionIsRejected: true);

        // Act
        var result = diagnostic.Verify();

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Equal(ShellDiagnosticFailure.CleanupFailed, result.Failure);
        Assert.Equal(1, mutex.DisposeCount);
        Assert.Equal(1, tray.DisposeCount);
        Assert.Equal(1, hotKey.DisposeCount);
    }

    [Fact]
    public void Verify_ReleasedMutexCannotBeReacquired_FailsClosedWithoutCreatingASecondShell()
    {
        // Arrange
        var mutex = new FakeResource();
        var tray = new FakeTrayResource(isVisible: true);
        var hotKey = new FakeHotKeyResource(isRegistered: true, windowHandle: (nint)1);
        var acquireCount = 0;
        var diagnostic = new ShellDiagnostic(
            () => ++acquireCount == 1 ? mutex : null,
            static () => true,
            () => tray,
            () => hotKey);

        // Act
        var result = diagnostic.Verify();

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Equal(ShellDiagnosticFailure.MutexReacquireFailed, result.Failure);
        Assert.Equal(2, acquireCount);
        Assert.Equal(1, mutex.DisposeCount);
        Assert.Equal(1, tray.DisposeCount);
        Assert.Equal(1, hotKey.DisposeCount);
    }

    [Fact]
    public void Verify_ReacquiredMutexDisposeThrows_FailsClosed()
    {
        // Arrange
        var mutex = new FakeResource();
        var reacquiredMutex = new FakeResource(new InvalidOperationException("simulated"));
        var tray = new FakeTrayResource(isVisible: true);
        var hotKey = new FakeHotKeyResource(isRegistered: true, windowHandle: (nint)1);
        var acquireCount = 0;
        var diagnostic = new ShellDiagnostic(
            () => ++acquireCount switch
            {
                1 => mutex,
                2 => reacquiredMutex,
                _ => null,
            },
            static () => true,
            () => tray,
            () => hotKey);

        // Act
        var result = diagnostic.Verify();

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Equal(ShellDiagnosticFailure.CleanupFailed, result.Failure);
        Assert.Equal(1, mutex.DisposeCount);
        Assert.Equal(1, reacquiredMutex.DisposeCount);
        Assert.Equal(1, tray.DisposeCount);
        Assert.Equal(1, hotKey.DisposeCount);
    }

    [Fact]
    public void Verify_TrayDisposeThrows_StillReleasesMutexAndHotKeyThenFailsClosed()
    {
        // Arrange
        var mutex = new FakeResource();
        var tray = new FakeTrayResource(isVisible: true, disposeException: new InvalidOperationException("simulated"));
        var hotKey = new FakeHotKeyResource(isRegistered: true, windowHandle: (nint)1);
        var diagnostic = CreateDiagnostic(mutex, tray, hotKey, mutexContentionIsRejected: true);

        // Act
        var result = diagnostic.Verify();

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Equal(ShellDiagnosticFailure.CleanupFailed, result.Failure);
        Assert.Equal(1, mutex.DisposeCount);
        Assert.Equal(1, tray.DisposeCount);
        Assert.Equal(1, hotKey.DisposeCount);
        Assert.True(hotKey.UnregistrationSucceeded);
    }

    [Fact]
    public void Verify_TrayFactoryThrows_ReleasesThePrimaryMutexAndFailsClosed()
    {
        // Arrange
        var mutex = new FakeResource();
        var hotKeyCreated = false;
        var diagnostic = new ShellDiagnostic(
            () => mutex,
            static () => true,
            static () => throw new InvalidOperationException("simulated"),
            () =>
            {
                hotKeyCreated = true;
                return new FakeHotKeyResource(isRegistered: true, windowHandle: (nint)1);
            });

        // Act
        var result = diagnostic.Verify();

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Equal(ShellDiagnosticFailure.UnexpectedFailure, result.Failure);
        Assert.Equal(1, mutex.DisposeCount);
        Assert.False(hotKeyCreated);
    }

    private static ShellDiagnostic CreateDiagnostic(
        FakeResource mutex,
        FakeTrayResource tray,
        FakeHotKeyResource hotKey,
        bool mutexContentionIsRejected) =>
        new(
            () => mutex,
            () => mutexContentionIsRejected,
            () => tray,
            () => hotKey);

    private sealed class FakeResource(Exception? disposeException = null) : IShellDiagnosticResource
    {
        public int DisposeCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (disposeException is not null)
            {
                throw disposeException;
            }

            IsDisposed = true;
        }
    }

    private sealed class FakeTrayResource(bool isVisible, Exception? disposeException = null) : IShellDiagnosticTrayResource
    {
        public int DisposeCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public bool IsVisible => isVisible;

        public void Dispose()
        {
            DisposeCount++;
            if (disposeException is not null)
            {
                throw disposeException;
            }

            IsDisposed = true;
        }
    }

    private sealed class FakeHotKeyResource(
        bool isRegistered,
        nint windowHandle,
        bool unregistrationSucceededAfterDispose = true) : IShellDiagnosticHotKeyResource
    {
        public int DisposeCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public bool IsRegistered => isRegistered;

        public nint WindowHandle => windowHandle;

        public bool? UnregistrationSucceeded { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            IsDisposed = true;
            UnregistrationSucceeded = isRegistered ? unregistrationSucceededAfterDispose : null;
        }
    }
}
