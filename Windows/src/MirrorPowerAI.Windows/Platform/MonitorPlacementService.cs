namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Places a native WPF window in physical pixels using the target monitor's effective DPI.
/// </summary>
public static class MonitorPlacementService
{
    private const double DefaultDpi = 96;

    /// <summary>Centers a bounded DIP-sized window within a physical monitor working area.</summary>
    /// <param name="windowHandle">Top-level window handle.</param>
    /// <param name="monitorPointX">Physical X coordinate used to select the monitor.</param>
    /// <param name="monitorPointY">Physical Y coordinate used to select the monitor.</param>
    /// <param name="workingLeft">Working-area left in physical pixels.</param>
    /// <param name="workingTop">Working-area top in physical pixels.</param>
    /// <param name="workingRight">Working-area right in physical pixels.</param>
    /// <param name="workingBottom">Working-area bottom in physical pixels.</param>
    /// <param name="preferredWidthDip">Preferred width in device-independent pixels.</param>
    /// <param name="preferredHeightDip">Preferred height in device-independent pixels.</param>
    /// <param name="minimumWidthDip">Minimum width in device-independent pixels.</param>
    /// <param name="minimumHeightDip">Minimum height in device-independent pixels.</param>
    /// <returns>Whether native DPI-aware placement succeeded.</returns>
    public static bool TryCenter(
        nint windowHandle,
        int monitorPointX,
        int monitorPointY,
        int workingLeft,
        int workingTop,
        int workingRight,
        int workingBottom,
        double preferredWidthDip = 720,
        double preferredHeightDip = 480,
        double minimumWidthDip = 420,
        double minimumHeightDip = 280)
    {
        if (windowHandle == nint.Zero ||
            workingRight <= workingLeft ||
            workingBottom <= workingTop ||
            !double.IsFinite(preferredWidthDip) ||
            !double.IsFinite(preferredHeightDip) ||
            !double.IsFinite(minimumWidthDip) ||
            !double.IsFinite(minimumHeightDip) ||
            preferredWidthDip <= 0 ||
            preferredHeightDip <= 0 ||
            minimumWidthDip <= 0 ||
            minimumHeightDip <= 0)
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.NativePoint(monitorPointX, monitorPointY),
            NativeMethods.MonitorDefaultToNearest);
        var scale = 1.0;
        if (monitor != nint.Zero &&
            NativeMethods.GetDpiForMonitor(
                monitor,
                NativeMethods.MonitorDpiTypeEffective,
                out var dpiX,
                out _) >= 0 &&
            dpiX > 0)
        {
            scale = dpiX / DefaultDpi;
        }

        var workingWidth = workingRight - workingLeft;
        var workingHeight = workingBottom - workingTop;
        var maximumWidth = Math.Max(1, (int)Math.Floor(workingWidth * 0.9));
        var maximumHeight = Math.Max(1, (int)Math.Floor(workingHeight * 0.8));
        var minimumWidth = Math.Min(maximumWidth, (int)Math.Ceiling(minimumWidthDip * scale));
        var minimumHeight = Math.Min(maximumHeight, (int)Math.Ceiling(minimumHeightDip * scale));
        var width = Math.Clamp((int)Math.Round(preferredWidthDip * scale), minimumWidth, maximumWidth);
        var height = Math.Clamp((int)Math.Round(preferredHeightDip * scale), minimumHeight, maximumHeight);
        var x = workingLeft + ((workingWidth - width) / 2);
        var y = workingTop + ((workingHeight - height) / 2);

        return NativeMethods.SetWindowPos(
            windowHandle,
            nint.Zero,
            x,
            y,
            width,
            height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }
}
