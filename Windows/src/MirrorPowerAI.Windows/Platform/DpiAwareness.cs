using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Configures the process for per-monitor V2 DPI awareness before any window handle is created.
/// </summary>
public static class DpiAwareness
{
    /// <summary>
    /// Enables per-monitor V2 awareness. A process that was already configured is considered valid.
    /// </summary>
    /// <returns>A result describing whether DPI awareness is available.</returns>
    public static DpiAwarenessResult TryEnablePerMonitorV2()
    {
        if (NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2))
        {
            return DpiAwarenessResult.Success;
        }

        var error = Marshal.GetLastWin32Error();
        return error == NativeMethods.ErrorAccessDenied
            ? DpiAwarenessResult.AlreadyConfigured
            : new DpiAwarenessResult(false, error, new Win32Exception(error).Message);
    }
}

/// <summary>
/// Describes the outcome of configuring process DPI awareness.
/// </summary>
/// <param name="IsUsable">Whether the process can continue with a valid awareness context.</param>
/// <param name="Win32Error">The Win32 error code, or zero when none occurred.</param>
/// <param name="Message">A diagnostic message that does not contain user data.</param>
public sealed record DpiAwarenessResult(bool IsUsable, int Win32Error, string Message)
{
    /// <summary>Successful per-monitor V2 configuration.</summary>
    public static DpiAwarenessResult Success { get; } = new(true, 0, string.Empty);

    /// <summary>The runtime or manifest configured DPI awareness before application startup.</summary>
    public static DpiAwarenessResult AlreadyConfigured { get; } = new(true, NativeMethods.ErrorAccessDenied, string.Empty);
}
