using MirrorPowerAI.Windows.Diagnostics;

namespace MirrorPowerAI.Windows.Shell;

/// <summary>
/// Completes a failed normal startup without exposing exception details or abandoning partially
/// constructed native resources.
/// </summary>
internal static class StartupFailureRecovery
{
    /// <summary>
    /// Runs best-effort cleanup and notification before requesting an explicit failed shutdown.
    /// </summary>
    /// <param name="cleanup">Releases every resource assigned before startup failed.</param>
    /// <param name="notify">Shows a fixed, localized message containing no exception or user data.</param>
    /// <param name="shutdown">Terminates the WPF application with a failed exit code.</param>
    internal static void Handle(Action cleanup, Action notify, Action shutdown)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(notify);
        ArgumentNullException.ThrowIfNull(shutdown);

        BestEffortCleanup.Run(cleanup, notify);
        shutdown();
    }
}
