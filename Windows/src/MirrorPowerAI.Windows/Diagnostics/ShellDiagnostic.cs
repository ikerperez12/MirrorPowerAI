using MirrorPowerAI.Windows.Platform;
using MirrorPowerAI.Windows.Resources;
using MirrorPowerAI.Windows.Shell;

namespace MirrorPowerAI.Windows.Diagnostics;

/// <summary>
/// Performs a bounded local diagnostic of the shell resources without starting a user session.
/// </summary>
/// <remarks>
/// The diagnostic creates only a named mutex, a notification-area icon, and a message-only hotkey
/// window. It deliberately does not construct settings, DPAPI, audio, model, network, or session services.
/// </remarks>
internal sealed class ShellDiagnostic
{
    private static readonly TimeSpan MutexProbeTimeout = TimeSpan.FromSeconds(2);
    private readonly Func<IShellDiagnosticResource?> _acquireMutex;
    private readonly Func<bool> _verifyMutexContention;
    private readonly Func<IShellDiagnosticTrayResource> _createTray;
    private readonly Func<IShellDiagnosticHotKeyResource> _createHotKey;

    /// <summary>
    /// Initializes the production diagnostic using real Windows shell resources.
    /// </summary>
    internal ShellDiagnostic()
        : this(
            AcquireMutex,
            VerifyMutexContention,
            static () => new TrayIconService(LocalizationService.Current),
            static () => new GlobalHotKeyService())
    {
    }

    /// <summary>
    /// Initializes a diagnostic with explicit resource factories for deterministic tests.
    /// </summary>
    internal ShellDiagnostic(
        Func<IShellDiagnosticResource?> acquireMutex,
        Func<bool> verifyMutexContention,
        Func<IShellDiagnosticTrayResource> createTray,
        Func<IShellDiagnosticHotKeyResource> createHotKey)
    {
        ArgumentNullException.ThrowIfNull(acquireMutex);
        ArgumentNullException.ThrowIfNull(verifyMutexContention);
        ArgumentNullException.ThrowIfNull(createTray);
        ArgumentNullException.ThrowIfNull(createHotKey);
        _acquireMutex = acquireMutex;
        _verifyMutexContention = verifyMutexContention;
        _createTray = createTray;
        _createHotKey = createHotKey;
    }

    /// <summary>
    /// Verifies mutex exclusion, tray initialization, and native hotkey registration, then disposes every resource.
    /// </summary>
    /// <returns>A non-sensitive result suitable for mapping to a process exit code.</returns>
    internal ShellDiagnosticResult Verify()
    {
        IShellDiagnosticResource? mutex = null;
        IShellDiagnosticTrayResource? tray = null;
        IShellDiagnosticHotKeyResource? hotKey = null;
        var failure = ShellDiagnosticFailure.None;

        try
        {
            mutex = _acquireMutex();
            if (mutex is null)
            {
                failure = ShellDiagnosticFailure.InstanceAlreadyRunning;
            }
            else if (!_verifyMutexContention())
            {
                failure = ShellDiagnosticFailure.MutexExclusivityFailed;
            }
            else
            {
                tray = _createTray();
                if (!tray.IsVisible)
                {
                    failure = ShellDiagnosticFailure.TrayUnavailable;
                }
                else
                {
                    hotKey = _createHotKey();
                    if (!hotKey.IsRegistered || hotKey.WindowHandle == nint.Zero)
                    {
                        failure = ShellDiagnosticFailure.HotKeyUnavailable;
                    }
                }
            }
        }
        catch (Exception)
        {
            failure = ShellDiagnosticFailure.UnexpectedFailure;
        }
        finally
        {
            var hotKeyDisposed = DisposeAndVerify(hotKey);
            var trayDisposed = DisposeAndVerify(tray);
            var mutexDisposed = DisposeAndVerify(mutex);
            if (!hotKeyDisposed
                || !trayDisposed
                || !mutexDisposed
                || !IsHotKeyCleanupVerified(hotKey))
            {
                failure = ShellDiagnosticFailure.CleanupFailed;
            }
        }

        if (failure == ShellDiagnosticFailure.None)
        {
            failure = VerifyMutexCanBeReacquired();
        }

        return new ShellDiagnosticResult(failure);
    }

    private static IShellDiagnosticResource? AcquireMutex() =>
        SingleInstanceGuard.TryAcquire(out var guard) ? guard : null;

    private static bool VerifyMutexContention()
    {
        var secondAcquireSucceeded = false;
        Exception? workerException = null;
        var worker = new Thread(() =>
        {
            IShellDiagnosticResource? secondaryMutex = null;
            try
            {
                secondaryMutex = AcquireMutex();
                secondAcquireSucceeded = secondaryMutex is not null;
            }
            catch (Exception exception)
            {
                workerException = exception;
            }
            finally
            {
                _ = DisposeAndVerify(secondaryMutex);
            }
        })
        {
            IsBackground = true,
            Name = "MirrorPowerAI.MutexDiagnostic",
        };

        worker.Start();
        return worker.Join(MutexProbeTimeout) && !secondAcquireSucceeded && workerException is null;
    }

    private static bool DisposeAndVerify(IShellDiagnosticResource? resource)
    {
        if (resource is null)
        {
            return true;
        }

        try
        {
            resource.Dispose();
            return resource.IsDisposed;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private ShellDiagnosticFailure VerifyMutexCanBeReacquired()
    {
        IShellDiagnosticResource? reacquiredMutex = null;
        var failure = ShellDiagnosticFailure.None;
        try
        {
            reacquiredMutex = _acquireMutex();
            if (reacquiredMutex is null)
            {
                failure = ShellDiagnosticFailure.MutexReacquireFailed;
            }
        }
        catch (Exception)
        {
            failure = ShellDiagnosticFailure.UnexpectedFailure;
        }
        finally
        {
            if (!DisposeAndVerify(reacquiredMutex))
            {
                failure = ShellDiagnosticFailure.CleanupFailed;
            }
        }

        return failure;
    }

    private static bool IsHotKeyCleanupVerified(IShellDiagnosticHotKeyResource? hotKey)
    {
        if (hotKey is null)
        {
            return true;
        }

        try
        {
            return !hotKey.IsRegistered || hotKey.UnregistrationSucceeded == true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Categorizes the safe, non-sensitive outcomes of a shell diagnostic.
/// </summary>
internal enum ShellDiagnosticFailure
{
    /// <summary>Every required shell resource was initialized and cleaned up.</summary>
    None,

    /// <summary>An existing application instance already owns the mutex.</summary>
    InstanceAlreadyRunning,

    /// <summary>A second thread unexpectedly acquired the per-user mutex.</summary>
    MutexExclusivityFailed,

    /// <summary>The primary mutex was released but could not be acquired again.</summary>
    MutexReacquireFailed,

    /// <summary>The notification-area icon could not become visible.</summary>
    TrayUnavailable,

    /// <summary>The stable message-only HWND could not register the configured global hotkey.</summary>
    HotKeyUnavailable,

    /// <summary>A shell resource factory threw an unexpected exception.</summary>
    UnexpectedFailure,

    /// <summary>At least one diagnostic resource could not be verified as released.</summary>
    CleanupFailed,
}

/// <summary>
/// Contains only the categorical outcome of a shell diagnostic.
/// </summary>
/// <param name="Failure">The first failure category, or <see cref="ShellDiagnosticFailure.None"/> on success.</param>
internal sealed record ShellDiagnosticResult(ShellDiagnosticFailure Failure)
{
    /// <summary>Gets whether all shell resources were verified successfully.</summary>
    internal bool IsSuccessful => Failure == ShellDiagnosticFailure.None;
}

/// <summary>
/// Represents a disposable shell resource whose cleanup can be verified by the diagnostic.
/// </summary>
internal interface IShellDiagnosticResource : IDisposable
{
    /// <summary>Gets whether the resource released its managed and native handles.</summary>
    bool IsDisposed { get; }
}

/// <summary>
/// Represents the notification-area resource used by the shell diagnostic.
/// </summary>
internal interface IShellDiagnosticTrayResource : IShellDiagnosticResource
{
    /// <summary>Gets whether the tray resource accepted the request to become visible.</summary>
    bool IsVisible { get; }
}

/// <summary>
/// Represents the message-only HWND and global-hotkey resource used by the diagnostic.
/// </summary>
internal interface IShellDiagnosticHotKeyResource : IShellDiagnosticResource
{
    /// <summary>Gets whether the configured hotkey registered with Windows.</summary>
    bool IsRegistered { get; }

    /// <summary>Gets the real message-only window handle used for registration.</summary>
    nint WindowHandle { get; }

    /// <summary>Gets whether Windows confirmed unregistration after disposal, when registration succeeded.</summary>
    bool? UnregistrationSucceeded { get; }
}
