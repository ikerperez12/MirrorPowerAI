namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Places a native WPF window in physical pixels using the target monitor's effective DPI.
/// </summary>
public static class MonitorPlacementService
{
    private const double DefaultDpi = 96;

    /// <summary>Centers a 720 by 480 DIP window within a physical monitor working area.</summary>
    /// <param name="windowHandle">Top-level window handle.</param>
    /// <param name="monitorPointX">Physical X coordinate used to select the monitor.</param>
    /// <param name="monitorPointY">Physical Y coordinate used to select the monitor.</param>
    /// <param name="workingLeft">Working-area left in physical pixels.</param>
    /// <param name="workingTop">Working-area top in physical pixels.</param>
    /// <param name="workingRight">Working-area right in physical pixels.</param>
    /// <param name="workingBottom">Working-area bottom in physical pixels.</param>
    /// <returns>Whether native DPI-aware placement succeeded.</returns>
    public static bool TryCenter(
        nint windowHandle,
        int monitorPointX,
        int monitorPointY,
        int workingLeft,
        int workingTop,
        int workingRight,
        int workingBottom)
    {
        if (windowHandle == nint.Zero || workingRight <= workingLeft || workingBottom <= workingTop)
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
        var minimumWidth = Math.Min(maximumWidth, (int)Math.Ceiling(420 * scale));
        var minimumHeight = Math.Min(maximumHeight, (int)Math.Ceiling(280 * scale));
        var width = Math.Clamp((int)Math.Round(720 * scale), minimumWidth, maximumWidth);
        var height = Math.Clamp((int)Math.Round(480 * scale), minimumHeight, maximumHeight);
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
