using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using MirrorPowerAI.Core.Overlay;

namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Applies and verifies Windows display-affinity protection to top-level WPF windows.
/// </summary>
public sealed class OverlayProtectionService : IOverlayProtectionService
{
    private readonly IWindowDisplayAffinityApi _displayAffinityApi;

    /// <summary>
    /// Initializes the production service backed by the public Windows display-affinity APIs.
    /// </summary>
    public OverlayProtectionService()
        : this(new NativeWindowDisplayAffinityApi())
    {
    }

    internal OverlayProtectionService(IWindowDisplayAffinityApi displayAffinityApi)
    {
        _displayAffinityApi = displayAffinityApi ?? throw new ArgumentNullException(nameof(displayAffinityApi));
    }

    /// <inheritdoc />
    public bool TryApplyAndVerify(nint windowHandle)
    {
        if (windowHandle == nint.Zero ||
            !_displayAffinityApi.SetWindowDisplayAffinity(
                windowHandle,
                NativeMethods.WdaExcludeFromCapture))
        {
            return false;
        }

        if (IsProtected(windowHandle))
        {
            return true;
        }

        _ = _displayAffinityApi.SetWindowDisplayAffinity(windowHandle, NativeMethods.WdaNone);
        return false;
    }

    /// <inheritdoc />
    public bool IsProtected(nint windowHandle) =>
        windowHandle != nint.Zero &&
        _displayAffinityApi.GetWindowDisplayAffinity(windowHandle, out var affinity) &&
        affinity == NativeMethods.WdaExcludeFromCapture;

    /// <inheritdoc />
    public CaptureProtectionResult ProtectAndVerify(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.AllowsTransparency)
        {
            return new CaptureProtectionResult(false, 0, "Capture protection requires a non-layered WPF window.");
        }

        var windowHandle = new WindowInteropHelper(window).EnsureHandle();
        if (windowHandle == nint.Zero)
        {
            return new CaptureProtectionResult(false, 0, "The overlay window handle could not be created.");
        }

        if (!TryApplyAndVerify(windowHandle))
        {
            return FromError(
                _displayAffinityApi.GetLastError(),
                "Windows rejected capture exclusion for the overlay.");
        }

        return CaptureProtectionResult.Success;
    }

    private static CaptureProtectionResult FromError(int error, string prefix) =>
        new(false, error, $"{prefix} {new Win32Exception(error).Message}");
}

/// <summary>
/// Describes whether an overlay is protected from public Windows capture APIs.
/// </summary>
/// <param name="IsProtected">Whether the requested affinity was applied and read back.</param>
/// <param name="Win32Error">The Win32 error code, or zero when unavailable.</param>
/// <param name="Message">A diagnostic message containing no sensitive content.</param>
public sealed record CaptureProtectionResult(bool IsProtected, int Win32Error, string Message)
{
    /// <summary>Represents verified capture exclusion.</summary>
    public static CaptureProtectionResult Success { get; } = new(true, 0, string.Empty);
}
