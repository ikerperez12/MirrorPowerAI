using MirrorPowerAI.Windows.Diagnostics;

namespace MirrorPowerAI.Windows.Tests.Diagnostics;

public sealed class BestEffortCleanupTests
{
    [Fact]
    public void Run_CancellationAndResourceFailures_StillInvokesEveryRemainingCleanup()
    {
        // Arrange
        var invocations = new List<string>();

        // Act
        BestEffortCleanup.Run(
            () =>
            {
                invocations.Add("cancel");
                throw new OperationCanceledException();
            },
            () =>
            {
                invocations.Add("window");
                throw new InvalidOperationException();
            },
            () => invocations.Add("session"),
            () =>
            {
                invocations.Add("hotkey");
                throw new InvalidOperationException();
            },
            () => invocations.Add("tray"),
            () => invocations.Add("mutex"));

        // Assert
        Assert.Equal(["cancel", "window", "session", "hotkey", "tray", "mutex"], invocations);
    }
}
