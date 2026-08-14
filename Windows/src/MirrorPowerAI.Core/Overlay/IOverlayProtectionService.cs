namespace MirrorPowerAI.Core.Overlay;

/// <summary>
/// Applies and verifies capture exclusion for a native overlay window.
/// </summary>
public interface IOverlayProtectionService
{
    /// <summary>
    /// Applies capture exclusion to the supplied top-level window and verifies the result.
    /// </summary>
    /// <param name="windowHandle">The native top-level window handle owned by this process.</param>
    /// <returns><see langword="true"/> only when exclusion was applied and verified.</returns>
    bool TryApplyAndVerify(nint windowHandle);

    /// <summary>
    /// Verifies that capture exclusion remains active for a window.
    /// </summary>
    /// <param name="windowHandle">The native top-level window handle owned by this process.</param>
    /// <returns><see langword="true"/> when the expected exclusion is active.</returns>
    bool IsProtected(nint windowHandle);
}
