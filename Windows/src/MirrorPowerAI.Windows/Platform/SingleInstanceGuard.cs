using System.Security.Principal;

namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Owns a per-user named mutex that prevents concurrent application instances.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly IInstanceMutex _mutex;
    private bool _ownsMutex;
    private bool _disposed;

    private SingleInstanceGuard(IInstanceMutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    /// <summary>
    /// Tries to acquire the application mutex for the current interactive Windows user.
    /// </summary>
    /// <param name="guard">The owned guard when acquisition succeeds; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when this is the only running instance.</returns>
    public static bool TryAcquire(out SingleInstanceGuard? guard)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return TryAcquire(new NativeInstanceMutexFactory(), sid, out guard);
    }

    internal static bool TryAcquire(
        IInstanceMutexFactory mutexFactory,
        string userScope,
        out SingleInstanceGuard? guard)
    {
        ArgumentNullException.ThrowIfNull(mutexFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(userScope);

        var mutex = mutexFactory.Create($"Local\\MirrorPowerAI.Windows.{userScope}");
        bool ownsMutex;

        try
        {
            ownsMutex = mutex.TryAcquire();
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        if (!ownsMutex)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex, ownsMutex: true);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_ownsMutex)
            {
                _mutex.Release();
                _ownsMutex = false;
            }
        }
        finally
        {
            _mutex.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
