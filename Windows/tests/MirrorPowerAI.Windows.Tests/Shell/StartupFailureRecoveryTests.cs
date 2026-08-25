using MirrorPowerAI.Windows.Shell;

namespace MirrorPowerAI.Windows.Tests.Shell;

public sealed class StartupFailureRecoveryTests
{
    [Fact]
    public void Handle_CleanupAndNotificationSucceed_ShutsDownLast()
    {
        var calls = new List<string>();

        StartupFailureRecovery.Handle(
            () => calls.Add("cleanup"),
            () => calls.Add("notify"),
            () => calls.Add("shutdown"));

        Assert.Equal(["cleanup", "notify", "shutdown"], calls);
    }

    [Fact]
    public void Handle_CleanupFails_StillNotifiesAndShutsDown()
    {
        var calls = new List<string>();

        StartupFailureRecovery.Handle(
            () =>
            {
                calls.Add("cleanup");
                throw new InvalidOperationException("diagnostic details must not escape startup");
            },
            () => calls.Add("notify"),
            () => calls.Add("shutdown"));

        Assert.Equal(["cleanup", "notify", "shutdown"], calls);
    }

    [Fact]
    public void Handle_NotificationFails_StillShutsDown()
    {
        var calls = new List<string>();

        StartupFailureRecovery.Handle(
            () => calls.Add("cleanup"),
            () =>
            {
                calls.Add("notify");
                throw new InvalidOperationException("native UI unavailable");
            },
            () => calls.Add("shutdown"));

        Assert.Equal(["cleanup", "notify", "shutdown"], calls);
    }

    [Fact]
    public void Handle_NullDependency_FailsBeforePerformingRecovery()
    {
        Assert.Throws<ArgumentNullException>(() => StartupFailureRecovery.Handle(
            null!,
            static () => { },
            static () => { }));
        Assert.Throws<ArgumentNullException>(() => StartupFailureRecovery.Handle(
            static () => { },
            null!,
            static () => { }));
        Assert.Throws<ArgumentNullException>(() => StartupFailureRecovery.Handle(
            static () => { },
            static () => { },
            null!));
    }
}
