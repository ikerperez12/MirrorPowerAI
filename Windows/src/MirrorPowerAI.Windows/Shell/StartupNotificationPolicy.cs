namespace MirrorPowerAI.Windows.Shell;

/// <summary>
/// Selects only actionable startup failures for notification-area feedback.
/// A fully ready startup deliberately emits nothing because the tray tooltip already exposes
/// the ready state without interrupting the user.
/// </summary>
internal static class StartupNotificationPolicy
{
    /// <summary>
    /// Publishes non-sensitive startup failures in a deterministic order. A ready application
    /// is silent; later session state changes are announced by <see cref="TrayStateAnnouncementPolicy"/>.
    /// </summary>
    /// <param name="isGlobalHotKeyRegistered">Whether the configured global hotkey registered successfully.</param>
    /// <param name="isDpiAwarenessUsable">Whether the process has usable per-monitor DPI awareness.</param>
    /// <param name="notificationSink">The localized tray notification boundary.</param>
    internal static void Publish(
        bool isGlobalHotKeyRegistered,
        bool isDpiAwarenessUsable,
        IStartupNotificationSink notificationSink)
    {
        ArgumentNullException.ThrowIfNull(notificationSink);

        if (!isGlobalHotKeyRegistered)
        {
            notificationSink.ShowError("HotKeyUnavailable");
        }

        if (!isDpiAwarenessUsable)
        {
            notificationSink.ShowError("DpiAwarenessFailed");
        }
    }
}

/// <summary>
/// Receives startup error resource identifiers before they are localized and rendered by the tray.
/// </summary>
internal interface IStartupNotificationSink
{
    /// <summary>Shows an error represented by an application-owned localization resource key.</summary>
    /// <param name="resourceKey">The non-sensitive resource key for the failure.</param>
    void ShowError(string resourceKey);
}

/// <summary>
/// Resolves app-owned startup resource keys before delegating to the native tray error surface.
/// </summary>
internal sealed class LocalizedTrayStartupNotificationSink : IStartupNotificationSink
{
    private readonly ITrayErrorNotificationSink _trayErrorNotificationSink;
    private readonly Func<string, string> _localize;

    /// <summary>Initializes the localized tray adapter.</summary>
    /// <param name="trayErrorNotificationSink">The native tray error surface.</param>
    /// <param name="localize">Resolves an app-owned resource key to localized non-sensitive text.</param>
    internal LocalizedTrayStartupNotificationSink(
        ITrayErrorNotificationSink trayErrorNotificationSink,
        Func<string, string> localize)
    {
        _trayErrorNotificationSink = trayErrorNotificationSink ??
            throw new ArgumentNullException(nameof(trayErrorNotificationSink));
        _localize = localize ?? throw new ArgumentNullException(nameof(localize));
    }

    /// <inheritdoc />
    public void ShowError(string resourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        _trayErrorNotificationSink.ShowError(_localize(resourceKey));
    }
}

/// <summary>
/// Narrows the tray dependency required to surface a localized startup failure.
/// </summary>
internal interface ITrayErrorNotificationSink
{
    /// <summary>Shows a localized, non-sensitive error in the native notification area.</summary>
    /// <param name="message">The localized, non-sensitive message.</param>
    void ShowError(string message);
}
