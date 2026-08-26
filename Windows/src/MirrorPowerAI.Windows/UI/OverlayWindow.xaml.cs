using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using MirrorPowerAI.Windows.Platform;
using Forms = System.Windows.Forms;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace MirrorPowerAI.Windows.UI;

/// <summary>
/// Non-layered, selectable-text overlay that can be protected with display affinity.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double ResultWidth = 720;
    private const double ResultHeight = 480;
    private const double ResultMinimumWidth = 420;
    private const double ResultMinimumHeight = 280;
    private const double StatusWidth = 420;
    private const double StatusHeight = 220;
    private const double StatusMinimumWidth = 380;
    private const double StatusMinimumHeight = 180;
    private readonly IOverlayDisplaySettingsChangeSource _displaySettingsChangeSource;
    private readonly IOverlayMonitorPlacementService _monitorPlacementService;
    private bool _contentAnnouncementPending;
    private bool _contentAnnouncementScheduled;
    private bool _statusAnnouncementPending;
    private bool _statusAnnouncementScheduled;
    private int _displaySettingsSubscribed;
    private int _displaySettingsRepositionQueued;

    /// <summary>Initializes an empty overlay. Sensitive text must be assigned only after protection succeeds.</summary>
    public OverlayWindow()
        : this(new SystemOverlayDisplaySettingsChangeSource(), new SystemOverlayMonitorPlacementService())
    {
    }

    /// <summary>Initializes an overlay with testable display-topology dependencies.</summary>
    /// <param name="displaySettingsChangeSource">Notifies when the desktop display topology changes.</param>
    /// <param name="monitorPlacementService">Places the overlay inside the active monitor's working area.</param>
    internal OverlayWindow(
        IOverlayDisplaySettingsChangeSource displaySettingsChangeSource,
        IOverlayMonitorPlacementService monitorPlacementService)
    {
        _displaySettingsChangeSource = displaySettingsChangeSource ??
            throw new ArgumentNullException(nameof(displaySettingsChangeSource));
        _monitorPlacementService = monitorPlacementService ??
            throw new ArgumentNullException(nameof(monitorPlacementService));
        InitializeComponent();
        ContentRendered += OnContentRendered;
    }

    /// <summary>
    /// Raised in question-then-answer order after visible protected text is submitted to UI
    /// Automation. The event deliberately carries no user text and exists for local UI verification.
    /// </summary>
    internal event EventHandler<OverlayContentAnnouncementEventArgs>? ContentAnnouncementRaised;

    /// <summary>
    /// Raised after a generic visible session status is submitted to UI Automation. The event does
    /// not expose the localized status text.
    /// </summary>
    internal event EventHandler? StatusAnnouncementRaised;

    /// <summary>Inserts plain text after the owning presenter verifies capture exclusion.</summary>
    /// <param name="question">Transcribed question.</param>
    /// <param name="answer">Generated answer.</param>
    public void SetProtectedContent(string? question, string answer)
    {
        Dispatcher.VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);
        ConfigureResultLayout();
        _statusAnnouncementPending = false;
        StatusTextBlock.ClearValue(AutomationProperties.NameProperty);
        StatusTextBlock.Text = string.Empty;
        QuestionTextBox.Text = question ?? string.Empty;
        AnswerTextBox.Text = answer;
        _contentAnnouncementPending = true;
        ScheduleContentAnnouncement();
    }

    /// <summary>Shows a generic capture or processing state after display-affinity verification.</summary>
    /// <param name="status">Localized status containing no user content.</param>
    /// <param name="isBusy">Whether to show indeterminate progress.</param>
    public void SetProtectedStatus(string status, bool isBusy)
    {
        Dispatcher.VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        _contentAnnouncementPending = false;
        QuestionTextBox.Clear();
        AnswerTextBox.Clear();
        ConfigureStatusLayout();
        StatusProgressBar.Visibility = isBusy && SystemParameters.ClientAreaAnimation
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusTextBlock.Text = status;
        AutomationProperties.SetName(StatusTextBlock, status);
        _statusAnnouncementPending = true;
        ScheduleStatusAnnouncement();
    }

    /// <summary>Clears all potentially sensitive text before closing or after a protection failure.</summary>
    public void ClearSensitiveContent()
    {
        Dispatcher.VerifyAccess();
        _contentAnnouncementPending = false;
        _statusAnnouncementPending = false;
        QuestionTextBox.Clear();
        AnswerTextBox.Clear();
        StatusTextBlock.Text = string.Empty;
        StatusTextBlock.ClearValue(AutomationProperties.NameProperty);
    }

    /// <summary>Centers and bounds the overlay inside the working area of the monitor under the pointer.</summary>
    public void PositionOnActiveMonitor()
    {
        _monitorPlacementService.Position(this);
    }

    internal void PositionOnActiveMonitorCore()
    {
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var windowHandle = new WindowInteropHelper(this).EnsureHandle();
        if (MonitorPlacementService.TryCenter(
                windowHandle,
                Forms.Cursor.Position.X,
                Forms.Cursor.Position.Y,
                screen.WorkingArea.Left,
                screen.WorkingArea.Top,
                screen.WorkingArea.Right,
                screen.WorkingArea.Bottom,
                Width,
                Height,
                MinWidth,
                MinHeight))
        {
            return;
        }

        var source = HwndSource.FromHwnd(windowHandle);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        var workingWidth = Math.Max(1, bottomRight.X - topLeft.X);
        var workingHeight = Math.Max(1, bottomRight.Y - topLeft.Y);

        var preferredWidth = double.IsFinite(Width) && Width > 0 ? Width : ResultWidth;
        var preferredHeight = double.IsFinite(Height) && Height > 0 ? Height : ResultHeight;
        Width = Math.Max(MinWidth, Math.Min(preferredWidth, workingWidth * 0.9));
        Height = Math.Max(MinHeight, Math.Min(preferredHeight, workingHeight * 0.8));
        Left = topLeft.X + Math.Max(0, (workingWidth - Width) / 2);
        Top = topLeft.Y + Math.Max(0, (workingHeight - Height) / 2);
    }

    private void OnContentRendered(object? sender, EventArgs eventArgs)
    {
        SubscribeToDisplaySettingsChanges();
        ScheduleContentAnnouncement();
        ScheduleStatusAnnouncement();
    }

    private void ConfigureResultLayout()
    {
        StatusPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        MinWidth = ResultMinimumWidth;
        MinHeight = ResultMinimumHeight;
        Width = ResultWidth;
        Height = ResultHeight;
        ResizeMode = ResizeMode.CanResizeWithGrip;
    }

    private void ConfigureStatusLayout()
    {
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
        MinWidth = StatusMinimumWidth;
        MinHeight = StatusMinimumHeight;
        Width = StatusWidth;
        Height = StatusHeight;
        ResizeMode = ResizeMode.NoResize;
    }

    private void SubscribeToDisplaySettingsChanges()
    {
        Dispatcher.VerifyAccess();
        if (!IsVisible || Interlocked.CompareExchange(ref _displaySettingsSubscribed, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _displaySettingsChangeSource.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }
        catch
        {
            Interlocked.Exchange(ref _displaySettingsSubscribed, 0);
            throw;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs eventArgs)
    {
        // SystemEvents can raise on a non-WPF thread. Never inspect WPF state there; coalesce the
        // notification and marshal the final placement decision to the owning dispatcher instead.
        if (Volatile.Read(ref _displaySettingsSubscribed) == 0 ||
            Dispatcher.HasShutdownStarted ||
            Interlocked.Exchange(ref _displaySettingsRepositionQueued, 1) != 0)
        {
            return;
        }

        try
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(RepositionAfterDisplaySettingsChanged));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _displaySettingsRepositionQueued, 0);
        }
    }

    private void RepositionAfterDisplaySettingsChanged()
    {
        Dispatcher.VerifyAccess();
        Interlocked.Exchange(ref _displaySettingsRepositionQueued, 0);
        if (Volatile.Read(ref _displaySettingsSubscribed) == 0 || !IsVisible)
        {
            return;
        }

        try
        {
            PositionOnActiveMonitor();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // The existing protected window remains available if Windows is between topology updates.
            // A later display-settings event can safely retry placement; no sensitive data is emitted.
        }
    }

    private void UnsubscribeFromDisplaySettingsChanges()
    {
        if (Interlocked.Exchange(ref _displaySettingsSubscribed, 0) == 0)
        {
            return;
        }

        _displaySettingsChangeSource.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void ScheduleContentAnnouncement()
    {
        if (!_contentAnnouncementPending ||
            _contentAnnouncementScheduled ||
            !IsVisible ||
            !AnswerTextBox.IsVisible ||
            Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _contentAnnouncementScheduled = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(AnnounceContentWhenVisible));
    }

    private void AnnounceContentWhenVisible()
    {
        _contentAnnouncementScheduled = false;
        if (!_contentAnnouncementPending ||
            !IsVisible ||
            !AnswerTextBox.IsVisible ||
            string.IsNullOrWhiteSpace(AnswerTextBox.Text))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(QuestionTextBox.Text))
        {
            RaiseLiveRegionChanged(QuestionTextBox, OverlayContentRegion.Question);
        }

        RaiseLiveRegionChanged(AnswerTextBox, OverlayContentRegion.Answer);
        _contentAnnouncementPending = false;
        AnswerTextBox.Focus();
    }

    private void ScheduleStatusAnnouncement()
    {
        if (!_statusAnnouncementPending ||
            _statusAnnouncementScheduled ||
            !IsVisible ||
            !StatusTextBlock.IsVisible ||
            Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _statusAnnouncementScheduled = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(AnnounceStatusWhenVisible));
    }

    private void AnnounceStatusWhenVisible()
    {
        _statusAnnouncementScheduled = false;
        if (!_statusAnnouncementPending ||
            !IsVisible ||
            !StatusTextBlock.IsVisible ||
            string.IsNullOrWhiteSpace(StatusTextBlock.Text))
        {
            return;
        }

        var peer = UIElementAutomationPeer.CreatePeerForElement(StatusTextBlock)
            ?? new TextBlockAutomationPeer(StatusTextBlock);
        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _statusAnnouncementPending = false;
        StatusAnnouncementRaised?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseLiveRegionChanged(WpfTextBox textBox, OverlayContentRegion region)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(textBox)
            ?? new TextBoxAutomationPeer(textBox);
        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        ContentAnnouncementRaised?.Invoke(this, new OverlayContentAnnouncementEventArgs(region));
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs) => Close();

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape)
        {
            return;
        }

        eventArgs.Handled = true;
        Close();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        UnsubscribeFromDisplaySettingsChanges();
        Interlocked.Exchange(ref _displaySettingsRepositionQueued, 0);
        ClearSensitiveContent();
        ContentRendered -= OnContentRendered;
        base.OnClosed(e);
    }
}

/// <summary>Exposes desktop display-topology changes without coupling tests to <see cref="SystemEvents"/>.</summary>
internal interface IOverlayDisplaySettingsChangeSource
{
    event EventHandler? DisplaySettingsChanged;
}

/// <summary>Bridges the system display-settings event to the overlay's lifetime.</summary>
internal sealed class SystemOverlayDisplaySettingsChangeSource : IOverlayDisplaySettingsChangeSource
{
    public event EventHandler? DisplaySettingsChanged
    {
        add => SystemEvents.DisplaySettingsChanged += value;
        remove => SystemEvents.DisplaySettingsChanged -= value;
    }
}

/// <summary>Places an overlay in response to its initial display or a topology change.</summary>
internal interface IOverlayMonitorPlacementService
{
    void Position(OverlayWindow window);
}

/// <summary>Performs the production pointer-monitor placement for an overlay.</summary>
internal sealed class SystemOverlayMonitorPlacementService : IOverlayMonitorPlacementService
{
    public void Position(OverlayWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.PositionOnActiveMonitorCore();
    }
}

/// <summary>Identifies the protected text region announced by the overlay.</summary>
internal enum OverlayContentRegion
{
    /// <summary>The transcribed question.</summary>
    Question,

    /// <summary>The generated answer.</summary>
    Answer,
}

/// <summary>Contains only the kind of protected region announced to UI Automation.</summary>
/// <param name="region">The announced region, never its text.</param>
internal sealed class OverlayContentAnnouncementEventArgs(OverlayContentRegion region) : EventArgs
{
    /// <summary>Gets the announced region.</summary>
    public OverlayContentRegion Region { get; } = region;
}
