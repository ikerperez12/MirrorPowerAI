using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MirrorPowerAI.Core.Security;
using MirrorPowerAI.Windows.Platform;
using MirrorPowerAI.Windows.Resources;
using MirrorPowerAI.Windows.UI;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace MirrorPowerAI.Windows.Diagnostics;

/// <summary>
/// Performs one bounded, local rendering lifecycle for the settings and protected-overlay windows.
/// </summary>
/// <remarks>
/// This diagnostic deliberately constructs no DPAPI implementation, real audio catalog, session,
/// HTTP client, or model manager. It loads defaults from an isolated missing settings path and uses
/// non-sensitive in-memory dependencies plus fixed overlay text.
/// </remarks>
internal sealed class UiDiagnostic
{
    private static readonly TimeSpan DiagnosticTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);
    private const string DiagnosticQuestion = "Pregunta de diagnóstico de interfaz.";
    private const string DiagnosticAnswer = "Respuesta de diagnóstico de interfaz.";

    /// <summary>
    /// Renders, validates, closes, and clears the two WPF windows without starting a user session.
    /// </summary>
    /// <param name="cancellationToken">Cancels the bounded UI lifecycle.</param>
    /// <returns>A categorical, non-sensitive result suitable for a process exit code.</returns>
    internal static async Task<UiDiagnosticResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        Dispatcher.CurrentDispatcher.VerifyAccess();

        var secretStore = new DiagnosticSecretStore();
        var audioCatalog = new DiagnosticAudioDeviceCatalog();
        var applicationCatalog = new DiagnosticAudioApplicationCatalog();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "MirrorPowerAI.UiDiagnostic",
            Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(temporaryDirectory, "settings.json");
        MainWindow? settingsWindow = null;
        OverlayPresenter? overlayPresenter = null;
        OverlayWindow? overlayWindow = null;
        var failure = UiDiagnosticFailure.None;

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(DiagnosticTimeout);
        var operationToken = timeoutCancellation.Token;

        try
        {
            settingsWindow = new MainWindow(
                new JsonSettingsStore(settingsPath),
                secretStore,
                audioCatalog,
                LocalizationService.Current,
                audioApplicationCatalog: applicationCatalog);
            await settingsWindow.ReloadAsync(operationToken);
            await ShowAndAwaitRenderAsync(settingsWindow, operationToken);

            failure = UiDiagnosticContract.ValidateSettingsWindow(
                settingsWindow,
                expectCloudConsentVisible: false,
                expectApplicationCaptureVisible: false);
            if (failure == UiDiagnosticFailure.None &&
                !await AwaitSettingsFocusAsync(settingsWindow, operationToken))
            {
                failure = UiDiagnosticFailure.SettingsFocusMissing;
            }

            if (failure == UiDiagnosticFailure.None)
            {
                if (settingsWindow.FindName("ProviderBox") is not WpfComboBox providerBox)
                {
                    failure = UiDiagnosticFailure.SettingsControlsInvalid;
                }
                else
                {
                    providerBox.SelectedValue = TranscriptionProviders.GeminiAudio;
                    await Dispatcher.CurrentDispatcher.InvokeAsync(
                        static () => { },
                        DispatcherPriority.Render,
                        operationToken);
                    failure = UiDiagnosticContract.ValidateSettingsWindow(
                        settingsWindow,
                        expectCloudConsentVisible: true,
                        expectApplicationCaptureVisible: false);
                }
            }

            if (failure == UiDiagnosticFailure.None)
            {
                if (settingsWindow.FindName("CaptureSourceBox") is not WpfComboBox captureSourceBox)
                {
                    failure = UiDiagnosticFailure.SettingsControlsInvalid;
                }
                else
                {
                    captureSourceBox.SelectedValue = AudioCaptureSources.Application;
                    await Dispatcher.CurrentDispatcher.InvokeAsync(
                        static () => { },
                        DispatcherPriority.Render,
                        operationToken);
                    failure = UiDiagnosticContract.ValidateSettingsWindow(
                        settingsWindow,
                        expectCloudConsentVisible: true,
                        expectApplicationCaptureVisible: true);
                }
            }

            if (failure == UiDiagnosticFailure.None)
            {
                failure = UiDiagnosticContract.ValidateIsolation(
                    secretStore.ReadCallCount,
                    audioCatalog.CallCount,
                    temporarySettingsWereCreated: false,
                    expectedSecretStoreCallCount: 2,
                    expectedAudioCatalogCallCount: 1,
                    unexpectedMutationCount: secretStore.MutationCallCount,
                    applicationCatalogCallCount: applicationCatalog.CallCount,
                    expectedApplicationCatalogCallCount: 1);
            }

            if (failure == UiDiagnosticFailure.None && !await CloseSettingsWindowAsync(settingsWindow))
            {
                failure = UiDiagnosticFailure.SettingsCleanupFailed;
            }

            if (failure == UiDiagnosticFailure.None)
            {
                settingsWindow = null;
                overlayPresenter = new OverlayPresenter(new OverlayProtectionService());
                var overlayResult = overlayPresenter.TryShow(DiagnosticQuestion, DiagnosticAnswer);
                if (!overlayResult.WasShown)
                {
                    failure = UiDiagnosticFailure.OverlayProtectionFailed;
                }
                else
                {
                    overlayWindow = FindDisplayedOverlay();
                    if (overlayWindow is null || !await AwaitWindowReadyAsync(overlayWindow, operationToken))
                    {
                        failure = UiDiagnosticFailure.OverlayNotRendered;
                    }
                    else
                    {
                        failure = UiDiagnosticContract.ValidateOverlayWindow(overlayWindow);
                        if (failure == UiDiagnosticFailure.None && !await AwaitAnswerFocusAsync(overlayWindow, operationToken))
                        {
                            failure = UiDiagnosticFailure.OverlayFocusMissing;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            failure = UiDiagnosticFailure.TimedOut;
        }
        catch (Exception)
        {
            failure = UiDiagnosticFailure.UnexpectedFailure;
        }
        finally
        {
            var overlayCleaned = await TryCleanupAsync(() => CloseOverlayAsync(overlayPresenter, overlayWindow));
            var settingsCleaned = await TryCleanupAsync(() => CloseSettingsWindowAsync(settingsWindow));
            var temporarySettingsInspectionSucceeded = TryGetTemporarySettingsWereCreated(
                settingsPath,
                temporaryDirectory,
                out var temporarySettingsWereCreated);
            var temporaryDirectoryDeleted = TryDeleteTemporaryDirectory(temporaryDirectory);

            if (failure == UiDiagnosticFailure.None && !overlayCleaned)
            {
                failure = UiDiagnosticFailure.OverlayCleanupFailed;
            }

            if (failure == UiDiagnosticFailure.None && !settingsCleaned)
            {
                failure = UiDiagnosticFailure.SettingsCleanupFailed;
            }

            if (failure == UiDiagnosticFailure.None)
            {
                failure = UiDiagnosticContract.ValidateIsolation(
                    secretStore.ReadCallCount,
                    audioCatalog.CallCount,
                    temporarySettingsWereCreated,
                    expectedSecretStoreCallCount: 2,
                    expectedAudioCatalogCallCount: 1,
                    unexpectedMutationCount: secretStore.MutationCallCount,
                    applicationCatalogCallCount: applicationCatalog.CallCount,
                    expectedApplicationCatalogCallCount: 1);
            }

            if (failure == UiDiagnosticFailure.None && !temporarySettingsInspectionSucceeded)
            {
                failure = UiDiagnosticFailure.TemporarySettingsInspectionFailed;
            }

            if (failure == UiDiagnosticFailure.None && !temporaryDirectoryDeleted)
            {
                failure = UiDiagnosticFailure.TemporarySettingsCleanupFailed;
            }
        }

        return new UiDiagnosticResult(failure);
    }

    private static async Task ShowAndAwaitRenderAsync(Window window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        Dispatcher.CurrentDispatcher.VerifyAccess();

        var contentRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onContentRendered = (_, _) => contentRendered.TrySetResult();
        window.ContentRendered += onContentRendered;
        try
        {
            window.Show();
            window.Activate();
            await contentRendered.Task.WaitAsync(cancellationToken);
            await Dispatcher.CurrentDispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.Render,
                cancellationToken);
        }
        finally
        {
            window.ContentRendered -= onContentRendered;
        }
    }

    private static async Task<bool> AwaitWindowReadyAsync(Window window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        return await AwaitConditionAsync(
            () => window.IsVisible
                && window.IsLoaded
                && new WindowInteropHelper(window).Handle != nint.Zero
                && PresentationSource.FromVisual(window) is not null,
            cancellationToken);
    }

    private static async Task<bool> AwaitAnswerFocusAsync(OverlayWindow window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        return await AwaitConditionAsync(
            () => window.FindName("AnswerTextBox") is WpfTextBox answerBox && answerBox.IsKeyboardFocused,
            cancellationToken);
    }

    private static async Task<bool> AwaitSettingsFocusAsync(MainWindow window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        return await AwaitConditionAsync(
            () => window.FindName("ApiKeyBox") is WpfPasswordBox apiKeyBox &&
                (apiKeyBox.IsKeyboardFocused ||
                 ReferenceEquals(
                     FocusManager.GetFocusedElement(FocusManager.GetFocusScope(apiKeyBox)),
                     apiKeyBox)),
            cancellationToken);
    }

    private static async Task<bool> AwaitConditionAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        Dispatcher.CurrentDispatcher.VerifyAccess();

        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            await Dispatcher.CurrentDispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.Render,
                cancellationToken);
        }

        return true;
    }

    private static OverlayWindow? FindDisplayedOverlay() =>
        System.Windows.Application.Current?.Windows
            .OfType<OverlayWindow>()
            .SingleOrDefault(window => window.IsVisible);

    private static async Task<bool> CloseSettingsWindowAsync(MainWindow? window)
    {
        if (window is null || !window.IsVisible)
        {
            return true;
        }

        return await CloseWindowAsync(window, window.CloseForApplicationExit);
    }

    private static async Task<bool> CloseOverlayAsync(OverlayPresenter? presenter, OverlayWindow? window)
    {
        if (presenter is null)
        {
            return window is null || !window.IsVisible;
        }

        if (window is null)
        {
            BestEffortCleanup.Run(presenter.Close);
            var application = System.Windows.Application.Current;
            return application is null || application.Windows.OfType<OverlayWindow>().All(candidate => !candidate.IsVisible);
        }

        var closed = await CloseWindowAsync(window, presenter.Close);
        if (!closed)
        {
            BestEffortCleanup.Run(
                window.ClearSensitiveContent,
                () =>
                {
                    if (window.IsVisible || window.IsLoaded)
                    {
                        window.Close();
                    }
                });
            closed = !window.IsVisible;
        }

        var contentsCleared = window.FindName("QuestionTextBox") is WpfTextBox questionBox
            && questionBox.Text.Length == 0
            && window.FindName("AnswerTextBox") is WpfTextBox answerBox
            && answerBox.Text.Length == 0;
        return closed && contentsCleared;
    }

    private static async Task<bool> CloseWindowAsync(Window window, Action close)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(close);
        Dispatcher.CurrentDispatcher.VerifyAccess();

        if (!window.IsVisible)
        {
            return true;
        }

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onClosed = (_, _) => closed.TrySetResult();
        window.Closed += onClosed;
        try
        {
            close();
            if (closed.Task.IsCompleted && !window.IsVisible)
            {
                return true;
            }

            await closed.Task.WaitAsync(CleanupTimeout);
            return !window.IsVisible && closed.Task.IsCompleted;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            window.Closed -= onClosed;
        }
    }

    private static async Task<bool> TryCleanupAsync(Func<Task<bool>> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        try
        {
            return await cleanup();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetTemporarySettingsWereCreated(
        string settingsPath,
        string temporaryDirectory,
        out bool temporarySettingsWereCreated)
    {
        try
        {
            temporarySettingsWereCreated = File.Exists(settingsPath) || Directory.Exists(temporaryDirectory);
            return true;
        }
        catch (Exception)
        {
            temporarySettingsWereCreated = false;
            return false;
        }
    }

    private static bool TryDeleteTemporaryDirectory(string temporaryDirectory)
    {
        try
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class DiagnosticSecretStore : ISecretStore
    {
        internal int ReadCallCount { get; private set; }

        internal int MutationCallCount { get; private set; }

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCallCount++;
            return Task.FromResult<string?>(null);
        }

        public Task SetSecretAsync(string name, string value, CancellationToken cancellationToken = default) =>
            RejectMutation();

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default) =>
            RejectMutation();

        private Task RejectMutation()
        {
            MutationCallCount++;
            return Task.FromException(new InvalidOperationException("The UI diagnostic must not access secrets."));
        }
    }

    private sealed class DiagnosticAudioDeviceCatalog : IAudioDeviceCatalog
    {
        internal int CallCount { get; private set; }

        public Task<IReadOnlyList<AudioDeviceOption>> GetOutputDevicesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            IReadOnlyList<AudioDeviceOption> devices =
            [new AudioDeviceOption(AudioDeviceOption.DefaultDeviceId, "Diagnostic default output")];
            return Task.FromResult(devices);
        }
    }

    private sealed class DiagnosticAudioApplicationCatalog : IAudioApplicationCatalog
    {
        internal int CallCount { get; private set; }

        public Task<IReadOnlyList<AudioApplicationOption>> GetAudioApplicationsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            IReadOnlyList<AudioApplicationOption> applications =
            [new AudioApplicationOption(1234, "diagnostic-player", "Diagnostic player")];
            return Task.FromResult(applications);
        }
    }
}

/// <summary>
/// Validates the non-sensitive WPF/UI Automation contract shared by the local diagnostic and tests.
/// </summary>
internal static class UiDiagnosticContract
{
    /// <summary>Checks the shown settings window and its critical interactive controls.</summary>
    /// <param name="window">The rendered settings window.</param>
    /// <param name="expectCloudConsentVisible">Whether the selected provider should expose cloud consent.</param>
    /// <param name="expectApplicationCaptureVisible">Whether application-only capture controls should be visible.</param>
    /// <returns>A categorical failure, or <see cref="UiDiagnosticFailure.None"/>.</returns>
    internal static UiDiagnosticFailure ValidateSettingsWindow(
        MainWindow window,
        bool expectCloudConsentVisible,
        bool expectApplicationCaptureVisible)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!HasRenderedWindow(window))
        {
            return UiDiagnosticFailure.SettingsNotRendered;
        }

        if (!HasAutomationName(window))
        {
            return UiDiagnosticFailure.SettingsWindowAutomationMissing;
        }

        var criticalControls = ValidateCriticalControls(
        [
            InspectControl(window, "ApiKeyBox", typeof(WpfPasswordBox)),
            InspectControl(window, "ContextBox", typeof(WpfTextBox)),
            InspectControl(window, "CaptureSourceBox", typeof(WpfComboBox)),
            InspectControl(window, "ProviderBox", typeof(WpfComboBox)),
            InspectControl(window, "LanguageBox", typeof(WpfComboBox)),
            expectApplicationCaptureVisible
                ? InspectControl(window, "ApplicationBox", typeof(WpfComboBox))
                : InspectControl(window, "DeviceBox", typeof(WpfComboBox)),
        ]);
        if (criticalControls != UiDiagnosticFailure.None)
        {
            return criticalControls;
        }

        var consentControl = InspectControl(window, "CloudConsentBox", typeof(WpfCheckBox));
        return consentControl.Exists &&
               consentControl.HasAutomationName &&
               consentControl.IsVisible == expectCloudConsentVisible
            ? UiDiagnosticFailure.None
            : UiDiagnosticFailure.SettingsControlsInvalid;
    }

    /// <summary>Checks the shown protected overlay and its selectable text controls.</summary>
    /// <param name="window">The rendered overlay window.</param>
    /// <returns>A categorical failure, or <see cref="UiDiagnosticFailure.None"/>.</returns>
    internal static UiDiagnosticFailure ValidateOverlayWindow(OverlayWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!HasRenderedWindow(window))
        {
            return UiDiagnosticFailure.OverlayNotRendered;
        }

        if (!HasAutomationName(window))
        {
            return UiDiagnosticFailure.OverlayWindowAutomationMissing;
        }

        return ValidateCriticalControls(
        [
            InspectControl(window, "QuestionTextBox", typeof(WpfTextBox)),
            InspectControl(window, "AnswerTextBox", typeof(WpfTextBox)),
        ]) == UiDiagnosticFailure.None
            ? UiDiagnosticFailure.None
            : UiDiagnosticFailure.OverlayControlsInvalid;
    }

    /// <summary>Validates a collection of critical control snapshots without exposing their text values.</summary>
    /// <param name="controls">Rendered control metadata.</param>
    /// <returns>A categorical failure suitable for deterministic tests.</returns>
    internal static UiDiagnosticFailure ValidateCriticalControls(IEnumerable<UiDiagnosticControlSnapshot> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        var snapshots = controls.ToArray();
        return snapshots.Length != 0
            && snapshots.All(control => control.Exists && control.IsVisible && control.HasAutomationName)
            ? UiDiagnosticFailure.None
            : UiDiagnosticFailure.SettingsControlsInvalid;
    }

    /// <summary>Fails closed if an isolated UI diagnostic touched a prohibited dependency or settings path.</summary>
    /// <param name="secretStoreCallCount">Calls observed on the diagnostic in-memory secret store.</param>
    /// <param name="audioCatalogCallCount">Calls observed on the diagnostic in-memory audio catalog.</param>
    /// <param name="temporarySettingsWereCreated">Whether the diagnostic-only settings path appeared on disk.</param>
    /// <param name="expectedSecretStoreCallCount">Expected bounded secret reads from the in-memory adapter.</param>
    /// <param name="expectedAudioCatalogCallCount">Expected bounded calls to the in-memory device adapter.</param>
    /// <param name="unexpectedMutationCount">Unexpected attempts to mutate protected values.</param>
    /// <param name="applicationCatalogCallCount">Calls observed on the in-memory application catalog.</param>
    /// <param name="expectedApplicationCatalogCallCount">Expected bounded calls to the application catalog.</param>
    /// <returns>A categorical isolation failure, or <see cref="UiDiagnosticFailure.None"/>.</returns>
    internal static UiDiagnosticFailure ValidateIsolation(
        int secretStoreCallCount,
        int audioCatalogCallCount,
        bool temporarySettingsWereCreated,
        int expectedSecretStoreCallCount = 0,
        int expectedAudioCatalogCallCount = 0,
        int unexpectedMutationCount = 0,
        int applicationCatalogCallCount = 0,
        int expectedApplicationCatalogCallCount = 0) =>
        secretStoreCallCount != expectedSecretStoreCallCount ||
        audioCatalogCallCount != expectedAudioCatalogCallCount ||
        applicationCatalogCallCount != expectedApplicationCatalogCallCount ||
        unexpectedMutationCount != 0
            ? UiDiagnosticFailure.UnexpectedDependencyUse
            : temporarySettingsWereCreated
                ? UiDiagnosticFailure.UnexpectedSettingsUse
                : UiDiagnosticFailure.None;

    private static bool HasRenderedWindow(Window window) =>
        window.IsVisible
        && window.IsLoaded
        && new WindowInteropHelper(window).Handle != nint.Zero
        && PresentationSource.FromVisual(window) is not null;

    private static UiDiagnosticControlSnapshot InspectControl(Window window, string name, Type expectedType)
    {
        var element = window.FindName(name) as FrameworkElement;
        return new UiDiagnosticControlSnapshot(
            element is not null && expectedType.IsInstanceOfType(element),
            element?.IsVisible == true,
            element is not null && HasAutomationName(element));
    }

    private static bool HasAutomationName(FrameworkElement element)
    {
        if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)))
        {
            return true;
        }

        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        return peer is not null && !string.IsNullOrWhiteSpace(peer.GetName());
    }
}

/// <summary>
/// Contains only the accessibility and render state needed to validate one critical control.
/// </summary>
/// <param name="Exists">Whether the named control existed with the expected type.</param>
/// <param name="IsVisible">Whether WPF reported the control as visible in the rendered window.</param>
/// <param name="HasAutomationName">Whether the control exposed a non-empty UI Automation name.</param>
internal readonly record struct UiDiagnosticControlSnapshot(bool Exists, bool IsVisible, bool HasAutomationName);

/// <summary>Classifies a local UI diagnostic outcome without carrying UI content or exception details.</summary>
internal enum UiDiagnosticFailure
{
    /// <summary>The lifecycle completed successfully.</summary>
    None,

    /// <summary>The settings window was not visible on a real rendered HWND.</summary>
    SettingsNotRendered,

    /// <summary>The settings window did not expose a UI Automation name.</summary>
    SettingsWindowAutomationMissing,

    /// <summary>A critical settings control was missing, hidden, or unnamed.</summary>
    SettingsControlsInvalid,

    /// <summary>The settings window could not be closed cleanly.</summary>
    SettingsCleanupFailed,

    /// <summary>The overlay could not receive verified capture protection.</summary>
    OverlayProtectionFailed,

    /// <summary>The overlay was not visible on a real rendered HWND.</summary>
    OverlayNotRendered,

    /// <summary>The overlay did not expose a UI Automation name.</summary>
    OverlayWindowAutomationMissing,

    /// <summary>A critical overlay text control was missing, hidden, or unnamed.</summary>
    OverlayControlsInvalid,

    /// <summary>The response text box did not receive keyboard focus after rendering.</summary>
    OverlayFocusMissing,

    /// <summary>The overlay could not be cleared and closed cleanly.</summary>
    OverlayCleanupFailed,

    /// <summary>The diagnostic unexpectedly invoked an in-memory secret or audio dependency.</summary>
    UnexpectedDependencyUse,

    /// <summary>The temporary settings path was unexpectedly created.</summary>
    UnexpectedSettingsUse,

    /// <summary>The diagnostic could not inspect whether its temporary settings path was created.</summary>
    TemporarySettingsInspectionFailed,

    /// <summary>The diagnostic could not remove its own exact temporary settings directory.</summary>
    TemporarySettingsCleanupFailed,

    /// <summary>The bounded UI lifecycle timed out or was cancelled.</summary>
    TimedOut,

    /// <summary>An unexpected non-sensitive failure occurred.</summary>
    UnexpectedFailure,

    /// <summary>The settings window did not establish a deterministic initial focus target.</summary>
    SettingsFocusMissing,
}

/// <summary>Contains the categorical result of a local settings-and-overlay lifecycle.</summary>
/// <param name="Failure">The non-sensitive failure category, or <see cref="UiDiagnosticFailure.None"/>.</param>
internal sealed record UiDiagnosticResult(UiDiagnosticFailure Failure)
{
    /// <summary>Gets whether every render, UI Automation, focus, and cleanup check passed.</summary>
    internal bool IsSuccessful => Failure == UiDiagnosticFailure.None;

    /// <summary>Maps a categorical, non-sensitive result to a bounded process exit code.</summary>
    /// <returns>Zero on success; otherwise a code in the 11–27 range that reveals no UI content.</returns>
    internal int ToProcessExitCode() => IsSuccessful ? 0 : 10 + (int)Failure;
}
