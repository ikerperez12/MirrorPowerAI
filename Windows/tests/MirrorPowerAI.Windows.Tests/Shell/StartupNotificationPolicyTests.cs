using MirrorPowerAI.Windows.Shell;

namespace MirrorPowerAI.Windows.Tests.Shell;

public sealed class StartupNotificationPolicyTests
{
    [Fact]
    public void Publish_ReadyStartup_DoesNotEmitAnInterruptingNotification()
    {
        // Arrange
        var tray = new RecordingTrayErrorNotificationSink();
        var sink = CreateLocalizedSink(tray);

        // Act
        StartupNotificationPolicy.Publish(
            isGlobalHotKeyRegistered: true,
            isDpiAwarenessUsable: true,
            sink);

        // Assert
        Assert.Empty(tray.Messages);
    }

    [Fact]
    public void Publish_GlobalHotKeyRegistrationFailure_EmitsTheLocalizedHotKeyError()
    {
        // Arrange
        var tray = new RecordingTrayErrorNotificationSink();
        var sink = CreateLocalizedSink(tray);

        // Act
        StartupNotificationPolicy.Publish(
            isGlobalHotKeyRegistered: false,
            isDpiAwarenessUsable: true,
            sink);

        // Assert
        Assert.Equal(["localized:HotKeyUnavailable"], tray.Messages);
    }

    [Fact]
    public void Publish_DpiAwarenessFailure_EmitsTheDedicatedLocalizedDpiError()
    {
        // Arrange
        var tray = new RecordingTrayErrorNotificationSink();
        var sink = CreateLocalizedSink(tray);

        // Act
        StartupNotificationPolicy.Publish(
            isGlobalHotKeyRegistered: true,
            isDpiAwarenessUsable: false,
            sink);

        // Assert
        Assert.Equal(["localized:DpiAwarenessFailed"], tray.Messages);
    }

    [Fact]
    public void Publish_MultipleStartupFailures_EmitsEachActionableErrorInDeterministicOrder()
    {
        // Arrange
        var tray = new RecordingTrayErrorNotificationSink();
        var sink = CreateLocalizedSink(tray);

        // Act
        StartupNotificationPolicy.Publish(
            isGlobalHotKeyRegistered: false,
            isDpiAwarenessUsable: false,
            sink);

        // Assert
        Assert.Equal(
            ["localized:HotKeyUnavailable", "localized:DpiAwarenessFailed"],
            tray.Messages);
    }

    [Fact]
    public void Publish_MissingNotificationSink_RejectsTheInvalidComposition()
    {
        // Act and assert
        Assert.Throws<ArgumentNullException>(() => StartupNotificationPolicy.Publish(
            isGlobalHotKeyRegistered: true,
            isDpiAwarenessUsable: true,
            notificationSink: null!));
    }

    private static LocalizedTrayStartupNotificationSink CreateLocalizedSink(
        RecordingTrayErrorNotificationSink tray) =>
        new(tray, static resourceKey => $"localized:{resourceKey}");

    private sealed class RecordingTrayErrorNotificationSink : ITrayErrorNotificationSink
    {
        public List<string> Messages { get; } = [];

        public void ShowError(string message) => Messages.Add(message);
    }
}
