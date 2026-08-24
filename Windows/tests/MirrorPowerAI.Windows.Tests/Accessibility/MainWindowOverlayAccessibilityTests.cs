using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Threading;
using MirrorPowerAI.Core.Security;
using MirrorPowerAI.Windows.Audio;
using MirrorPowerAI.Windows.Platform;
using MirrorPowerAI.Windows.Resources;
using MirrorPowerAI.Windows.Tests.Platform;
using MirrorPowerAI.Windows.UI;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;

namespace MirrorPowerAI.Windows.Tests.Accessibility;

[Collection(nameof(WpfSettingsWindowSerialTestSuite))]
public sealed class MainWindowOverlayAccessibilityTests
{
    [Fact]
    public async Task ReloadAsync_ProtectedSettingsLoadFailureBeforeShow_AnnouncesOnceAfterWindowIsVisible()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var settingsStore = new JsonSettingsStore(Path.Combine(temporaryDirectory.Path, "settings.json"));

        await StaDispatcher.RunAsync(async () =>
        {
            var window = new MainWindow(
                settingsStore,
                new FailingSecretStore(),
                new TestAudioDeviceCatalog(),
                LocalizationService.Current)
            {
                Left = -10000,
                Top = -10000,
            };
            var announcements = 0;
            var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.StatusAnnouncementRaised += (_, _) =>
            {
                announcements++;
                announced.TrySetResult();
            };

            try
            {
                // The failed DPAPI reads occur while the settings window is still hidden.
                await window.ReloadAsync();

                var status = GetTextBlock(window, "StatusText");
                Assert.False(window.IsVisible);
                Assert.Equal(0, announcements);
                Assert.Equal(Visibility.Visible, status.Visibility);
                Assert.Contains(LocalizationService.Current["SettingsLoadError"], status.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("secret-that-must-not-appear", status.Text, StringComparison.Ordinal);
                Assert.Equal(status.Text, AutomationProperties.GetName(status));

                window.Show();
                await announced.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                Assert.True(window.IsVisible);
                Assert.Equal(1, announcements);
                Assert.False(status.IsKeyboardFocused);
                var apiKeyBox = GetPasswordBox(window, "ApiKeyBox");
                Assert.True(
                    apiKeyBox.IsKeyboardFocused ||
                    ReferenceEquals(
                        FocusManager.GetFocusedElement(FocusManager.GetFocusScope(apiKeyBox)),
                        apiKeyBox));

                var contextBox = GetTextBox(window, "ContextBox");
                FocusManager.SetFocusedElement(FocusManager.GetFocusScope(contextBox), contextBox);
                _ = contextBox.Focus();
                window.Hide();
                window.Show();
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                Assert.True(
                    apiKeyBox.IsKeyboardFocused ||
                    ReferenceEquals(
                        FocusManager.GetFocusedElement(FocusManager.GetFocusScope(apiKeyBox)),
                        apiKeyBox));

                window.Activate();
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                Assert.Equal(1, announcements);
            }
            finally
            {
                window.CloseForApplicationExit();
            }
        });
    }

    [Fact]
    public async Task ProtectedOverlay_VisibleContent_AnnouncesQuestionThenAnswerFocusesAnswerAndClearsOnClose()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var window = new OverlayWindow
            {
                Left = -10000,
                Top = -10000,
            };
            var announcedRegions = new List<OverlayContentRegion>();
            var bothRegionsAnnounced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ContentAnnouncementRaised += (_, eventArgs) =>
            {
                announcedRegions.Add(eventArgs.Region);
                if (announcedRegions.Count == 2)
                {
                    bothRegionsAnnounced.TrySetResult();
                }
            };

            try
            {
                window.SetProtectedContent("Question for the protected overlay.", "Answer for the protected overlay.");
                var question = GetTextBox(window, "QuestionTextBox");
                var answer = GetTextBox(window, "AnswerTextBox");

                window.Show();
                await bothRegionsAnnounced.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal([OverlayContentRegion.Question, OverlayContentRegion.Answer], announcedRegions);
                Assert.True(answer.IsKeyboardFocused);
                Assert.Equal(LocalizationService.Current["QuestionHeading"], GetAutomationName(question));
                Assert.Equal(LocalizationService.Current["AnswerHeading"], GetAutomationName(answer));
                Assert.Equal("Question for the protected overlay.", GetAutomationValue(question));
                Assert.Equal("Answer for the protected overlay.", GetAutomationValue(answer));

                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(string.Empty, question.Text);
                Assert.Equal(string.Empty, answer.Text);
                Assert.Equal(LocalizationService.Current["QuestionHeading"], GetAutomationName(question));
                Assert.Equal(LocalizationService.Current["AnswerHeading"], GetAutomationName(answer));
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

    private static WpfTextBlock GetTextBlock(MainWindow window, string name) =>
        Assert.IsType<WpfTextBlock>(window.FindName(name));

    private static WpfTextBox GetTextBox(OverlayWindow window, string name) =>
        Assert.IsType<WpfTextBox>(window.FindName(name));

    private static WpfTextBox GetTextBox(MainWindow window, string name) =>
        Assert.IsType<WpfTextBox>(window.FindName(name));

    private static WpfPasswordBox GetPasswordBox(MainWindow window, string name) =>
        Assert.IsType<WpfPasswordBox>(window.FindName(name));

    private static string GetAutomationName(WpfTextBox textBox)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(textBox)
            ?? new TextBoxAutomationPeer(textBox);
        return peer.GetName();
    }

    private static string GetAutomationValue(WpfTextBox textBox)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(textBox)
            ?? new TextBoxAutomationPeer(textBox);
        return Assert.IsAssignableFrom<IValueProvider>(peer.GetPattern(PatternInterface.Value)).Value;
    }

    private sealed class TestAudioDeviceCatalog : IAudioDeviceCatalog
    {
        public Task<IReadOnlyList<AudioDeviceOption>> GetOutputDevicesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AudioDeviceOption> devices =
            [
                new AudioDeviceOption(AudioDeviceOption.DefaultDeviceId, "Default"),
            ];
            return Task.FromResult(devices);
        }
    }

    private sealed class FailingSecretStore : ISecretStore
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return name == MainWindow.GeminiApiKeySecretName
                ? Task.FromException<string?>(new CryptographicException("secret-that-must-not-appear"))
                : Task.FromException<string?>(new IOException("secret-that-must-not-appear"));
        }

        public Task SetSecretAsync(string name, string value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MirrorPowerAI.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
