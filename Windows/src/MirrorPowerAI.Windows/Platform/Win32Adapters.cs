using System.Runtime.InteropServices;

namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Isolates the display-affinity APIs so their fail-closed behaviour can be verified without a desktop capture tool.
/// </summary>
internal interface IWindowDisplayAffinityApi
{
    bool SetWindowDisplayAffinity(nint windowHandle, uint affinity);

    bool GetWindowDisplayAffinity(nint windowHandle, out uint affinity);

    int GetLastError();
}

internal sealed class NativeWindowDisplayAffinityApi : IWindowDisplayAffinityApi
{
    public bool SetWindowDisplayAffinity(nint windowHandle, uint affinity) =>
        NativeMethods.SetWindowDisplayAffinity(windowHandle, affinity);

    public bool GetWindowDisplayAffinity(nint windowHandle, out uint affinity) =>
        NativeMethods.GetWindowDisplayAffinity(windowHandle, out affinity);

    public int GetLastError() => Marshal.GetLastWin32Error();
}

/// <summary>
/// Isolates the global-hotkey APIs from WPF message-window ownership.
/// </summary>
internal interface IGlobalHotKeyApi
{
    bool RegisterHotKey(nint windowHandle, int identifier, uint modifiers, uint virtualKey);

    bool UnregisterHotKey(nint windowHandle, int identifier);

    int GetLastError();
}

internal sealed class NativeGlobalHotKeyApi : IGlobalHotKeyApi
{
    public bool RegisterHotKey(nint windowHandle, int identifier, uint modifiers, uint virtualKey) =>
        NativeMethods.RegisterHotKey(windowHandle, identifier, modifiers, virtualKey);

    public bool UnregisterHotKey(nint windowHandle, int identifier) =>
        NativeMethods.UnregisterHotKey(windowHandle, identifier);

    public int GetLastError() => Marshal.GetLastWin32Error();
}

/// <summary>
/// Abstracts a per-user named mutex for deterministic single-instance tests.
/// </summary>
internal interface IInstanceMutex : IDisposable
{
    bool TryAcquire();

    void Release();
}

internal interface IInstanceMutexFactory
{
    IInstanceMutex Create(string name);
}

internal sealed class NativeInstanceMutexFactory : IInstanceMutexFactory
{
    public IInstanceMutex Create(string name) => new NativeInstanceMutex(name);
}

internal sealed class NativeInstanceMutex : IInstanceMutex
{
    private readonly Mutex _mutex;

    public NativeInstanceMutex(string name)
    {
        _mutex = new Mutex(initiallyOwned: false, name);
    }

    public bool TryAcquire()
    {
        try
        {
            return _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    public void Release() => _mutex.ReleaseMutex();

    public void Dispose() => _mutex.Dispose();
}
