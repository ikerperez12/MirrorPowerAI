using System.Windows.Interop;
using System.Windows.Threading;
using MirrorPowerAI.Core.Overlay;
using MirrorPowerAI.Windows.Platform;

namespace MirrorPowerAI.Windows.UI;

/// <summary>
/// Creates protected overlays and fails closed before assigning sensitive text.
/// </summary>
public sealed class OverlayPresenter
{
    private readonly IOverlayProtectionService _protectionService;
    private OverlayWindow? _window;

    /// <summary>Initializes the presenter.</summary>
    /// <param name="protectionService">Capture-exclusion implementation.</param>
    public OverlayPresenter(IOverlayProtectionService protectionService)
    {
        _protectionService = protectionService ?? throw new ArgumentNullException(nameof(protectionService));
    }

    /// <summary>Raised when the protected status panel requests that listening be paused.</summary>
    public event EventHandler? StopRequested;

    /// <summary>Gets whether the protected overlay currently has a visible window.</summary>
    public bool IsVisible => _window?.IsVisible == true;

    /// <summary>
    /// Shows plain text only after Windows confirms <c>WDA_EXCLUDEFROMCAPTURE</c>.
    /// </summary>
    /// <param name="question">Transcribed question.</param>
    /// <param name="answer">Generated answer.</param>
    /// <returns>A result safe to report without revealing content.</returns>
    public OverlayShowResult TryShow(string? question, string answer) =>
        TryShow(question, answer, activate: true);

    /// <summary>
    /// Shows a protected answer and optionally leaves the meeting window focused.
    /// </summary>
    /// <param name="question">Transcribed question.</param>
    /// <param name="answer">Generated answer.</param>
    /// <param name="activate">
    /// <see langword="true"/> only for an explicit user request such as the tray's
    /// “show response” command. Automatic meeting answers remain non-activating.
    /// </param>
    /// <returns>A result safe to report without revealing content.</returns>
    public OverlayShowResult TryShow(string? question, string answer, bool activate)
    {
        Dispatcher.CurrentDispatcher.VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);

        Close();
        var candidate = new OverlayWindow
        {
            ShowActivated = activate,
        };
        var protection = _protectionService is OverlayProtectionService windowsProtection
            ? windowsProtection.ProtectAndVerify(candidate)
            : ProtectThroughContract(_protectionService, candidate);
        if (!protection.IsProtected)
        {
            candidate.ClearSensitiveContent();
            candidate.Close();
            return new OverlayShowResult(false, protection.Win32Error, protection.Message);
        }

        candidate.SetProtectedContent(question, answer, focusAnswer: activate);
        candidate.PositionOnActiveMonitor();
        candidate.StopRequested += OnStopRequested;
        candidate.Closed += OnWindowClosed;
        _window = candidate;
        candidate.Show();
        if (activate)
        {
            candidate.Activate();
        }
        return OverlayShowResult.Success;
    }

    /// <summary>
    /// Shows a generic protected session state without activating the window or stealing focus from
    /// the meeting, browser, or presentation application.
    /// </summary>
    /// <param name="status">Localized status containing no user content or raw diagnostics.</param>
    /// <param name="isBusy">Whether indeterminate progress should be visible.</param>
    /// <param name="showStopAction">Whether the protected pause-listening action should be visible.</param>
    /// <returns>A result safe to report without exposing the status text.</returns>
    public OverlayShowResult TryShowStatus(
        string status,
        bool isBusy = true,
        bool showStopAction = false)
    {
        Dispatcher.CurrentDispatcher.VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        if (_window is { IsVisible: true } existingWindow)
        {
            existingWindow.SetProtectedStatus(status, isBusy, showStopAction);
            existingWindow.PositionOnActiveMonitor();
            return OverlayShowResult.Success;
        }

        Close();
        var candidate = new OverlayWindow
        {
            ShowActivated = false,
        };
        var protection = _protectionService is OverlayProtectionService windowsProtection
            ? windowsProtection.ProtectAndVerify(candidate)
            : ProtectThroughContract(_protectionService, candidate);
        if (!protection.IsProtected)
        {
            candidate.ClearSensitiveContent();
            candidate.Close();
            return new OverlayShowResult(false, protection.Win32Error, protection.Message);
        }

        candidate.SetProtectedStatus(status, isBusy, showStopAction);
        candidate.PositionOnActiveMonitor();
        candidate.StopRequested += OnStopRequested;
        candidate.Closed += OnWindowClosed;
        _window = candidate;
        candidate.Show();
        return OverlayShowResult.Success;
    }

    private static CaptureProtectionResult ProtectThroughContract(
        IOverlayProtectionService protectionService,
        OverlayWindow window)
    {
        if (window.AllowsTransparency)
        {
            return new CaptureProtectionResult(false, 0, "Capture protection requires a non-layered WPF window.");
        }

        var windowHandle = new WindowInteropHelper(window).EnsureHandle();
        return protectionService.TryApplyAndVerify(windowHandle) && protectionService.IsProtected(windowHandle)
            ? CaptureProtectionResult.Success
            : new CaptureProtectionResult(false, 0, "Capture exclusion could not be verified.");
    }

    /// <summary>Closes the current overlay and clears its text.</summary>
    public void Close()
    {
        if (_window is null)
        {
            return;
        }

        var window = _window;
        _window = null;
        window.StopRequested -= OnStopRequested;
        window.Closed -= OnWindowClosed;
        window.ClearSensitiveContent();
        window.Close();
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is OverlayWindow window)
        {
            window.StopRequested -= OnStopRequested;
            window.Closed -= OnWindowClosed;
        }

        _window = null;
    }

    private void OnStopRequested(object? sender, EventArgs eventArgs) =>
        StopRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Describes a protected overlay display attempt.
/// </summary>
/// <param name="WasShown">Whether content was shown under verified protection.</param>
/// <param name="Win32Error">The platform error, or zero when unavailable.</param>
/// <param name="DiagnosticMessage">A non-sensitive diagnostic.</param>
public sealed record OverlayShowResult(bool WasShown, int Win32Error, string DiagnosticMessage)
{
    /// <summary>Represents a successfully displayed protected overlay.</summary>
    public static OverlayShowResult Success { get; } = new(true, 0, string.Empty);
}
