using System.Windows.Threading;
using MirrorPowerAI.Windows.Tests.Platform;
using MirrorPowerAI.Windows.UI;

namespace MirrorPowerAI.Windows.Tests.Accessibility;

[Collection(nameof(WpfSettingsWindowSerialTestSuite))]
public sealed class OverlayWindowDisplayTopologyTests
{
    [Fact]
    public async Task DisplaySettingsChanged_VisibleOverlay_RepositionsOnceOnItsOwningDispatcher()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var displaySettings = new RecordingDisplaySettingsChangeSource();
            var placement = new RecordingMonitorPlacementService();
            var window = new OverlayWindow(displaySettings, placement)
            {
                Left = -10000,
                Top = -10000,
            };

            try
            {
                window.Show();
                await WaitUntilAsync(
                    () => displaySettings.SubscriptionCount == 1,
                    window.Dispatcher);

                await Task.Run(displaySettings.Raise);
                await placement.Repositioned.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(1, placement.CallCount);
                Assert.Equal(window.Dispatcher.Thread.ManagedThreadId, placement.ThreadId);
                Assert.True(window.IsVisible);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
            }
        });
    }

    [Fact]
    public async Task ClosedOverlay_UnsubscribesAndIgnoresQueuedDisplayTopologyChanges()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var displaySettings = new RecordingDisplaySettingsChangeSource();
            var placement = new RecordingMonitorPlacementService();
            var window = new OverlayWindow(displaySettings, placement)
            {
                Left = -10000,
                Top = -10000,
            };

            window.Show();
            await WaitUntilAsync(
                () => displaySettings.SubscriptionCount == 1,
                window.Dispatcher);

            // Block this dispatcher only until the background event has queued its callback. Closing
            // before returning to the dispatcher makes the intended queued-after-close ordering
            // deterministic instead of racing ApplicationIdle against the awaited continuation.
            Task.Run(displaySettings.Raise).GetAwaiter().GetResult();
            window.Close();
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal(0, displaySettings.SubscriptionCount);
            Assert.Equal(0, placement.CallCount);

            displaySettings.Raise();
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(0, placement.CallCount);
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(dispatcher);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle, timeout.Token);
        }
    }

    private sealed class RecordingDisplaySettingsChangeSource : IOverlayDisplaySettingsChangeSource
    {
        private EventHandler? _displaySettingsChanged;

        public int SubscriptionCount { get; private set; }

        public event EventHandler? DisplaySettingsChanged
        {
            add
            {
                _displaySettingsChanged += value;
                SubscriptionCount++;
            }

            remove
            {
                _displaySettingsChanged -= value;
                SubscriptionCount--;
            }
        }

        public void Raise() => _displaySettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class RecordingMonitorPlacementService : IOverlayMonitorPlacementService
    {
        public TaskCompletionSource Repositioned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public int ThreadId { get; private set; }

        public void Position(OverlayWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            CallCount++;
            ThreadId = Environment.CurrentManagedThreadId;
            Repositioned.TrySetResult();
        }
    }
}
