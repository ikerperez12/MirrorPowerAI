using System.ComponentModel;
using System.Drawing;
using Forms = System.Windows.Forms;
using MirrorPowerAI.Windows.Diagnostics;
using MirrorPowerAI.Windows.Resources;

namespace MirrorPowerAI.Windows.Shell;

/// <summary>
/// Owns the notification-area icon and its keyboard-accessible native context menu.
/// </summary>
public sealed class TrayIconService : IDisposable, IShellDiagnosticTrayResource
{
    private readonly LocalizationService _localization;
    private readonly Icon _icon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private readonly Forms.ToolStripMenuItem _showResponseItem;
    private readonly Forms.ToolStripMenuItem _settingsItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
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
        _icon = (Icon)SystemIcons.Application.Clone();
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

    /// <summary>Updates state-dependent labels and menu availability.</summary>
    /// <param name="activity">Current shell activity.</param>
    /// <param name="hasResponse">Whether a protected result is available.</param>
    public void SetState(ShellActivityState activity, bool hasResponse)
    {
        _activity = activity;
        _hasResponse = hasResponse;
        UpdateLabels();
    }

    /// <summary>Shows a non-sensitive informational notification.</summary>
    /// <param name="message">Localized, non-sensitive message.</param>
    public void ShowInformation(string message) => ShowBalloon(message, Forms.ToolTipIcon.Info);

    /// <summary>Shows a non-sensitive error notification.</summary>
    /// <param name="message">Localized, non-sensitive message.</param>
    public void ShowError(string message) => ShowBalloon(message, Forms.ToolTipIcon.Error);

    private void ShowBalloon(string message, Forms.ToolTipIcon icon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.BalloonTipTitle = _localization["AppName"];
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(5000);
    }

    private void UpdateLabels()
    {
        _notifyIcon.Text = _localization["AppName"];
        _statusItem.Text = _localization[_activity switch
        {
            ShellActivityState.Capturing => "TrayStatusCapturing",
            ShellActivityState.Processing => "TrayStatusBusy",
            ShellActivityState.Error => "TrayStatusError",
            _ => "TrayStatusIdle",
        }];
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
