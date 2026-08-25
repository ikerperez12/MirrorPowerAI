using System.ComponentModel;
using System.Windows.Interop;
using System.Windows.Threading;
using MirrorPowerAI.Windows.Diagnostics;

namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Owns a stable message-only window and registers Alt+Shift+L as a global hotkey.
/// </summary>
public sealed class GlobalHotKeyService : IDisposable, IShellDiagnosticHotKeyResource
{
    internal const int HotKeyIdentifier = 0x4D50;
    private readonly HwndSource _messageWindow;
    private readonly IGlobalHotKeyApi _hotKeyApi;
    private bool _registered;
    private bool _disposed;
    private bool _messageWindowDisposed;
    private bool? _unregistrationSucceeded;

    bool IShellDiagnosticResource.IsDisposed => _disposed && _messageWindowDisposed;

    bool IShellDiagnosticHotKeyResource.IsRegistered => Registration.IsRegistered;

    nint IShellDiagnosticHotKeyResource.WindowHandle => _messageWindow.Handle;

    bool? IShellDiagnosticHotKeyResource.UnregistrationSucceeded => _unregistrationSucceeded;

    /// <summary>
    /// Initializes the message-only HWND and attempts to register the hotkey.
    /// </summary>
    public GlobalHotKeyService()
        : this(new NativeGlobalHotKeyApi())
    {
    }

    internal GlobalHotKeyService(IGlobalHotKeyApi hotKeyApi)
    {
        _hotKeyApi = hotKeyApi ?? throw new ArgumentNullException(nameof(hotKeyApi));
        Dispatcher.CurrentDispatcher.VerifyAccess();

        var parameters = new HwndSourceParameters("MirrorPowerAI.HotKeyWindow")
        {
            ParentWindow = NativeMethods.HwndMessage,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };

        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WindowProcedure);

        Registration = Register(_messageWindow.Handle, _hotKeyApi);
        _registered = Registration.IsRegistered;
    }

    /// <summary>Raised on the WPF dispatcher when Alt+Shift+L is pressed.</summary>
    public event EventHandler? Pressed;

    /// <summary>Gets the registration outcome.</summary>
    public HotKeyRegistration Registration { get; }

    internal static HotKeyRegistration Register(nint windowHandle, IGlobalHotKeyApi hotKeyApi)
    {
        ArgumentNullException.ThrowIfNull(hotKeyApi);
        if (hotKeyApi.RegisterHotKey(
                windowHandle,
                HotKeyIdentifier,
                NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModNoRepeat,
                NativeMethods.VirtualKeyL))
        {
            return HotKeyRegistration.Success;
        }

        var error = hotKeyApi.GetLastError();
        return new HotKeyRegistration(
            false,
            error,
            error == NativeMethods.ErrorHotKeyAlreadyRegistered
                ? "Alt+Shift+L is already registered by another application."
                : new Win32Exception(error).Message);
    }

    internal static bool Unregister(nint windowHandle, IGlobalHotKeyApi hotKeyApi)
    {
        ArgumentNullException.ThrowIfNull(hotKeyApi);
        return hotKeyApi.UnregisterHotKey(windowHandle, HotKeyIdentifier);
    }

    private nint WindowProcedure(nint windowHandle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (!_disposed && message == NativeMethods.WmHotKey && wParam == HotKeyIdentifier)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return nint.Zero;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed && _messageWindowDisposed)
        {
            return;
        }

        _disposed = true;
        if (_registered)
        {
            try
            {
                _unregistrationSucceeded = Unregister(_messageWindow.Handle, _hotKeyApi);
            }
            catch (Exception)
            {
                _unregistrationSucceeded = false;
            }
            finally
            {
                _registered = false;
            }
        }

        BestEffortCleanup.Run(
            () => _messageWindow.RemoveHook(WindowProcedure),
            () => _messageWindow.Dispose());
        try
        {
            _messageWindowDisposed = _messageWindow.Handle == nint.Zero;
        }
        catch (Exception)
        {
            _messageWindowDisposed = false;
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Describes the result of registering the application hotkey.
/// </summary>
/// <param name="IsRegistered">Whether registration succeeded.</param>
/// <param name="Win32Error">The Win32 error code, or zero on success.</param>
/// <param name="Message">A diagnostic message containing no user data.</param>
public sealed record HotKeyRegistration(bool IsRegistered, int Win32Error, string Message)
{
    /// <summary>Represents a successful registration.</summary>
    public static HotKeyRegistration Success { get; } = new(true, 0, string.Empty);
}
