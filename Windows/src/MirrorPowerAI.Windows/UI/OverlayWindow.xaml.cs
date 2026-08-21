using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MirrorPowerAI.Windows.Platform;
using Forms = System.Windows.Forms;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace MirrorPowerAI.Windows.UI;

/// <summary>
/// Non-layered, selectable-text overlay that can be protected with display affinity.
/// </summary>
public partial class OverlayWindow : Window
{
    private bool _contentAnnouncementPending;
    private bool _contentAnnouncementScheduled;

    /// <summary>Initializes an empty overlay. Sensitive text must be assigned only after protection succeeds.</summary>
    public OverlayWindow()
    {
        InitializeComponent();
        ContentRendered += OnContentRendered;
    }

    /// <summary>
    /// Raised in question-then-answer order after visible protected text is submitted to UI
    /// Automation. The event deliberately carries no user text and exists for local UI verification.
    /// </summary>
    internal event EventHandler<OverlayContentAnnouncementEventArgs>? ContentAnnouncementRaised;

    /// <summary>Inserts plain text after the owning presenter verifies capture exclusion.</summary>
    /// <param name="question">Transcribed question.</param>
    /// <param name="answer">Generated answer.</param>
    public void SetProtectedContent(string? question, string answer)
    {
        Dispatcher.VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);
        QuestionTextBox.Text = question ?? string.Empty;
        AnswerTextBox.Text = answer;
        _contentAnnouncementPending = true;
        ScheduleContentAnnouncement();
    }

    /// <summary>Clears all potentially sensitive text before closing or after a protection failure.</summary>
    public void ClearSensitiveContent()
    {
        Dispatcher.VerifyAccess();
        _contentAnnouncementPending = false;
        QuestionTextBox.Clear();
        AnswerTextBox.Clear();
    }

    /// <summary>Centers and bounds the overlay inside the working area of the monitor under the pointer.</summary>
    public void PositionOnActiveMonitor()
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
                screen.WorkingArea.Bottom))
        {
            return;
        }

        var source = HwndSource.FromHwnd(windowHandle);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        var workingWidth = Math.Max(1, bottomRight.X - topLeft.X);
        var workingHeight = Math.Max(1, bottomRight.Y - topLeft.Y);

        Width = Math.Max(MinWidth, Math.Min(720, workingWidth * 0.9));
        Height = Math.Max(MinHeight, Math.Min(480, workingHeight * 0.8));
        Left = topLeft.X + Math.Max(0, (workingWidth - Width) / 2);
        Top = topLeft.Y + Math.Max(0, (workingHeight - Height) / 2);
    }

    private void OnContentRendered(object? sender, EventArgs eventArgs)
    {
        ScheduleContentAnnouncement();
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
        ClearSensitiveContent();
        ContentRendered -= OnContentRendered;
        base.OnClosed(e);
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
