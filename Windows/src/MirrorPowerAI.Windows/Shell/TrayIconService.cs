using System.ComponentModel;
using System.Drawing;
using Forms = System.Windows.Forms;
using MirrorPowerAI.Windows.Diagnostics;
using MirrorPowerAI.Windows.Resources;

namespace MirrorPowerAI.Windows.Shell;

/// <summary>
/// Owns the notification-area icon and its keyboard-accessible native context menu.
/// </summary>
public sealed class TrayIconService : IDisposable, IShellDiagnosticTrayResource, ITrayErrorNotificationSink
{
    private const int NotifyIconTextLimit = 63;
    private readonly LocalizationService _localization;
    private readonly Icon _icon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private readonly Forms.ToolStripMenuItem _showResponseItem;
    private readonly Forms.ToolStripMenuItem _settingsItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly TrayStateAnnouncementPolicy _stateAnnouncementPolicy = new();
    private ShellActivityState _activity;
    private bool _hasResponse;
    private bool _disposed;

    bool IShellDiagnosticResource.IsDisposed => _disposed;

    bool IShellDiagnosticTrayResource.IsVisible => !_disposed && _notifyIcon.Visible;

    /// <summary>Initializes and displays the notification-area icon.</summary>
    /// <param name="localization">Live localized string source.</param>
    public TrayIconService(LocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _icon = TrayIconFactory.Create();
        _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
        _toggleItem = new Forms.ToolStripMenuItem();
        _showResponseItem = new Forms.ToolStripMenuItem();
        _settingsItem = new Forms.ToolStripMenuItem();
        _exitItem = new Forms.ToolStripMenuItem();
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.AddRange(
        [
            _statusItem,
            new Forms.ToolStripSeparator(),
            _toggleItem,
            _showResponseItem,
            _settingsItem,
            new Forms.ToolStripSeparator(),
            _exitItem,
        ]);

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _icon,
            Text = _localization["AppName"],
            Visible = true,
        };

        _toggleItem.Click += OnToggleClick;
        _showResponseItem.Click += OnShowResponseClick;
        _settingsItem.Click += OnSettingsClick;
        _exitItem.Click += OnExitClick;
        _notifyIcon.DoubleClick += OnShowResponseClick;
        _localization.PropertyChanged += OnLocalizationChanged;
        UpdateLabels();
    }

    /// <summary>Raised when the user requests start or stop from the tray.</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>Raised when the user requests the latest protected response.</summary>
    public event EventHandler? ShowResponseRequested;

    /// <summary>Raised when the user opens settings.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the user explicitly exits the application.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Raised after a non-sensitive state announcement is sent to the Windows notification area.
    /// The event is intended for local UI observation only; consumers must not log its message.
    /// </summary>
    public event EventHandler<TrayStateAnnouncementEventArgs>? StateAnnouncementRaised;

    /// <summary>Updates state-dependent labels and menu availability.</summary>
    /// <param name="activity">Current shell activity.</param>
    /// <param name="hasResponse">Whether a protected result is available.</param>
    public void SetState(ShellActivityState activity, bool hasResponse)
    {
        _activity = activity;
        _hasResponse = hasResponse;
        if (!_disposed)
        {
            UpdateLabels();
        }
    }

    /// <summary>
    /// Updates tray labels and announces a changed session activity once through the native
    /// Windows notification area. The announcement contains only a localized generic state,
    /// never a question, answer, project context, API key, or provider error detail.
    /// </summary>
    /// <param name="activity">Current shell activity.</param>
    /// <param name="hasResponse">Whether a protected result is available.</param>
    /// <returns><see langword="true"/> when a notification was emitted; otherwise <see langword="false"/> when the state is unchanged or the tray is disposed.</returns>
    /// <remarks>
    /// Call this from the UI thread in response to <c>ISessionCommands.StateChanged</c>.
    /// The initial <see cref="ShellActivityState.Idle"/> state is deliberately silent so startup
    /// does not generate a redundant notification.
    /// </remarks>
    public bool SetStateAndNotify(ShellActivityState activity, bool hasResponse)
    {
        SetState(activity, hasResponse);
        return TryAnnounceStateChange(activity);
    }

    private bool TryAnnounceStateChange(ShellActivityState activity)
    {
        if (_disposed || !_stateAnnouncementPolicy.TryCreate(activity, out var announcement))
        {
            return false;
        }

        var message = _localization[announcement.ResourceKey];
        ShowBalloon(message, announcement.Icon);
        StateAnnouncementRaised?.Invoke(
            this,
            new TrayStateAnnouncementEventArgs(announcement.Activity, message));
        return true;
    }

    /// <summary>Shows a non-sensitive informational notification.</summary>
    /// <param name="message">Localized, non-sensitive message.</param>
    public void ShowInformation(string message) => ShowBalloon(message, Forms.ToolTipIcon.Info);

    /// <summary>Shows a non-sensitive error notification.</summary>
    /// <param name="message">Localized, non-sensitive message.</param>
    public void ShowError(string message) => ShowBalloon(message, Forms.ToolTipIcon.Error);

    private void ShowBalloon(string message, Forms.ToolTipIcon icon)
    {
        if (_disposed)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.BalloonTipTitle = TruncateTooltipText(_localization["AppName"]);
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(5000);
    }

    private void UpdateLabels()
    {
        var statusText = _localization[_activity switch
        {
            ShellActivityState.Capturing => "TrayStatusCapturing",
            ShellActivityState.Processing => "TrayStatusBusy",
            ShellActivityState.Error => "TrayStatusError",
            _ => "TrayStatusIdle",
        }];
        _notifyIcon.Text = CreateSafeTooltip(_localization["AppName"], statusText);
        _statusItem.Text = statusText;
        _statusItem.AccessibleName = statusText;
        _statusItem.AccessibleDescription = statusText;
        _toggleItem.Text = _localization[_activity switch
        {
            ShellActivityState.Capturing => "TrayToggleStop",
            ShellActivityState.Processing => "TrayToggleCancel",
            _ => "TrayToggleStart",
        }];
        _toggleItem.Enabled = true;
        _showResponseItem.Text = _localization["TrayShowResponse"];
        _showResponseItem.Enabled = _hasResponse;
        _settingsItem.Text = _localization["TraySettings"];
        _exitItem.Text = _localization["TrayExit"];
        SetAccessibleText(_toggleItem);
        SetAccessibleText(_showResponseItem);
        SetAccessibleText(_settingsItem);
        SetAccessibleText(_exitItem);
    }

    /// <summary>
    /// Creates a bounded notification-area tooltip from app-owned localized labels only.
    /// Callers must never pass user content, protected settings, audio, or provider diagnostics.
    /// </summary>
    internal static string CreateSafeTooltip(string appName, string statusText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);

        var normalizedAppName = NormalizeTooltipText(appName);
        var normalizedStatusText = NormalizeTooltipText(statusText);
        var tooltip = $"{normalizedAppName}: {normalizedStatusText}";
        return tooltip.Length <= NotifyIconTextLimit
            ? tooltip
            : TruncateTooltipText(normalizedAppName);
    }

    private static string NormalizeTooltipText(string text) => text
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Replace('\t', ' ')
        .Trim();

    private static string TruncateTooltipText(string text) => text.Length <= NotifyIconTextLimit
        ? text
        : text[..NotifyIconTextLimit];

    private static void SetAccessibleText(Forms.ToolStripMenuItem item)
    {
        item.AccessibleName = item.Text;
        item.AccessibleDescription = item.Text;
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs eventArgs) => UpdateLabels();
    private void OnToggleClick(object? sender, EventArgs eventArgs) => ToggleRequested?.Invoke(this, EventArgs.Empty);
    private void OnShowResponseClick(object? sender, EventArgs eventArgs) => ShowResponseRequested?.Invoke(this, EventArgs.Empty);
    private void OnSettingsClick(object? sender, EventArgs eventArgs) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OnExitClick(object? sender, EventArgs eventArgs) => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _localization.PropertyChanged -= OnLocalizationChanged;
        _notifyIcon.DoubleClick -= OnShowResponseClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Represents a generic, localized state change announced through the Windows notification area.
/// Its message is intentionally free of user content and diagnostic details.
/// </summary>
/// <param name="activity">The announced shell activity.</param>
/// <param name="message">Localized generic state text.</param>
public sealed class TrayStateAnnouncementEventArgs(ShellActivityState activity, string message) : EventArgs
{
    /// <summary>Gets the announced shell activity.</summary>
    public ShellActivityState Activity { get; } = activity;

    /// <summary>Gets the generic localized announcement text.</summary>
    public string Message { get; } = string.IsNullOrWhiteSpace(message)
        ? throw new ArgumentException("A state announcement requires text.", nameof(message))
        : message;
}

/// <summary>
/// Tracks the last announced state so notification-area feedback remains useful rather than noisy.
/// </summary>
internal sealed class TrayStateAnnouncementPolicy
{
    private ShellActivityState _lastAnnouncedActivity = ShellActivityState.Idle;

    /// <summary>Creates the next non-sensitive announcement if the activity changed.</summary>
    internal bool TryCreate(ShellActivityState activity, out TrayStateAnnouncement announcement)
    {
        if (_lastAnnouncedActivity == activity)
        {
            announcement = default!;
            return false;
        }

        announcement = activity switch
        {
            ShellActivityState.Capturing => new TrayStateAnnouncement(
                activity,
                "TrayAnnouncementCapturing",
                Forms.ToolTipIcon.Info),
            ShellActivityState.Processing => new TrayStateAnnouncement(
                activity,
                "TrayAnnouncementProcessing",
                Forms.ToolTipIcon.Info),
            ShellActivityState.Idle => new TrayStateAnnouncement(
                activity,
                "TrayAnnouncementIdle",
                Forms.ToolTipIcon.Info),
            ShellActivityState.Error => new TrayStateAnnouncement(
                activity,
                "TrayAnnouncementError",
                Forms.ToolTipIcon.Error),
            _ => throw new ArgumentOutOfRangeException(nameof(activity), activity, "Unknown shell activity."),
        };
        _lastAnnouncedActivity = activity;
        return true;
    }
}

/// <summary>Contains the resource mapping for a deduplicated state announcement.</summary>
/// <param name="Activity">The changed shell activity.</param>
/// <param name="ResourceKey">The localized generic announcement resource.</param>
/// <param name="Icon">The native non-sensitive notification icon.</param>
internal sealed record TrayStateAnnouncement(
    ShellActivityState Activity,
    string ResourceKey,
    Forms.ToolTipIcon Icon);
