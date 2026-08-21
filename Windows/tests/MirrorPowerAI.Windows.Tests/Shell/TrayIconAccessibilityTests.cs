using Forms = System.Windows.Forms;
using MirrorPowerAI.Windows.Shell;

namespace MirrorPowerAI.Windows.Tests.Shell;

public sealed class TrayIconAccessibilityTests
{
    [Theory]
    [MemberData(nameof(ChangedStates))]
    public void TryCreate_ChangedState_UsesOneGenericLocalizedResourceAndExpectedIcon(
        ShellActivityState state,
        string resourceKey,
        Forms.ToolTipIcon icon)
    {
        // Arrange
        var policy = new TrayStateAnnouncementPolicy();

        // Act
        var wasCreated = policy.TryCreate(state, out var announcement);

        // Assert
        Assert.True(wasCreated);
        Assert.Equal(state, announcement.Activity);
        Assert.Equal(resourceKey, announcement.ResourceKey);
        Assert.Equal(icon, announcement.Icon);
    }

    [Fact]
    public void TryCreate_InitialIdleAndRepeatedStates_DoesNotProduceNoisyDuplicateAnnouncements()
    {
        // Arrange
        var policy = new TrayStateAnnouncementPolicy();

        // Act and assert
        Assert.False(policy.TryCreate(ShellActivityState.Idle, out _));
        Assert.True(policy.TryCreate(ShellActivityState.Capturing, out _));
        Assert.False(policy.TryCreate(ShellActivityState.Capturing, out _));
        Assert.True(policy.TryCreate(ShellActivityState.Processing, out _));
        Assert.False(policy.TryCreate(ShellActivityState.Processing, out _));
        Assert.True(policy.TryCreate(ShellActivityState.Error, out _));
        Assert.False(policy.TryCreate(ShellActivityState.Error, out _));
        Assert.True(policy.TryCreate(ShellActivityState.Idle, out _));
        Assert.False(policy.TryCreate(ShellActivityState.Idle, out _));
    }

    [Fact]
    public void TryCreate_ReturningToAnEarlierState_AnnouncesTheNewTransition()
    {
        // Arrange
        var policy = new TrayStateAnnouncementPolicy();

        // Act
        Assert.True(policy.TryCreate(ShellActivityState.Capturing, out _));
        Assert.True(policy.TryCreate(ShellActivityState.Processing, out _));
        var wasCreated = policy.TryCreate(ShellActivityState.Capturing, out var announcement);

        // Assert
        Assert.True(wasCreated);
        Assert.Equal(ShellActivityState.Capturing, announcement.Activity);
        Assert.Equal("TrayAnnouncementCapturing", announcement.ResourceKey);
    }

    [Fact]
    public void TryCreate_ReturningToIdle_AnnouncesTheIdleTransition()
    {
        // Arrange
        var policy = new TrayStateAnnouncementPolicy();
        Assert.True(policy.TryCreate(ShellActivityState.Processing, out _));

        // Act
        var wasCreated = policy.TryCreate(ShellActivityState.Idle, out var announcement);

        // Assert
        Assert.True(wasCreated);
        Assert.Equal(ShellActivityState.Idle, announcement.Activity);
        Assert.Equal("TrayAnnouncementIdle", announcement.ResourceKey);
        Assert.Equal(Forms.ToolTipIcon.Info, announcement.Icon);
    }

    [Fact]
    public void CreateSafeTooltip_ControlCharactersAndLongAppName_UsesBoundedSingleLineAppOwnedText()
    {
        // Act
        var tooltip = TrayIconService.CreateSafeTooltip(
            new string('A', 80),
            "Status\r\nwithout\tthe response text");

        // Assert
        Assert.Equal(63, tooltip.Length);
        Assert.DoesNotContain('\r', tooltip);
        Assert.DoesNotContain('\n', tooltip);
        Assert.DoesNotContain('\t', tooltip);
        Assert.Equal(new string('A', 63), tooltip);
    }

    [Theory]
    [InlineData(null, "Status: ready")]
    [InlineData("MirrorPowerAI", null)]
    [InlineData(" ", "Status: ready")]
    [InlineData("MirrorPowerAI", " ")]
    public void CreateSafeTooltip_MissingLocalizedLabel_RejectsIt(string? appName, string? statusText)
    {
        // Act and assert
        Assert.ThrowsAny<ArgumentException>(() => TrayIconService.CreateSafeTooltip(appName!, statusText!));
    }

    public static TheoryData<ShellActivityState, string, Forms.ToolTipIcon> ChangedStates => new()
    {
        { ShellActivityState.Capturing, "TrayAnnouncementCapturing", Forms.ToolTipIcon.Info },
        { ShellActivityState.Processing, "TrayAnnouncementProcessing", Forms.ToolTipIcon.Info },
        { ShellActivityState.Error, "TrayAnnouncementError", Forms.ToolTipIcon.Error },
    };
}
