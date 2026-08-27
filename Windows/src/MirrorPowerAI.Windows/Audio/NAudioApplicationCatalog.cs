using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MirrorPowerAI.Windows.Platform;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Enumerates running applications that currently own a Windows render-audio session.
/// </summary>
/// <remarks>
/// Only executable metadata is exposed. Window captions are deliberately excluded because meeting
/// titles and browser tab titles may contain sensitive content.
/// </remarks>
public sealed class NAudioApplicationCatalog : IAudioApplicationCatalog
{
    /// <inheritdoc />
    public Task<IReadOnlyList<AudioApplicationOption>> GetAudioApplicationsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Core Audio COM enumeration can briefly block while a device is being restarted. Keep it
        // off the WPF dispatcher so refreshing this list never freezes the settings window.
        return Task.Run(
            () => EnumerateAudioApplications(cancellationToken),
            cancellationToken);
    }

    private static IReadOnlyList<AudioApplicationOption> EnumerateAudioApplications(
        CancellationToken cancellationToken)
    {
        // Key by resolved root PID, not executable name. Two browser profiles or two Electron
        // instances can legitimately render at the same time and must remain selectable separately.
        var applications = new Dictionary<int, AudioApplicationOption>();
        var processTree = ProcessTreeSnapshot.TryCapture();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    AddDeviceSessions(device, applications, processTree, cancellationToken);
                }
            }
        }
        catch (COMException)
        {
            // The settings window remains usable with system-output capture when Core Audio is busy.
        }

        IReadOnlyList<AudioApplicationOption> result = applications.Values
            .OrderBy(static application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static application => application.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static application => application.ProcessId)
            .ToArray();
        return result;
    }

    private static void AddDeviceSessions(
        MMDevice device,
        IDictionary<int, AudioApplicationOption> applications,
        IReadOnlyDictionary<int, ProcessTreeEntry> processTree,
        CancellationToken cancellationToken)
    {
        var sessionManager = device.AudioSessionManager;
        sessionManager.RefreshSessions();
        var sessions = sessionManager.Sessions;
        for (var index = 0; index < sessions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var session = sessions[index];
            if (session.IsSystemSoundsSession ||
                session.State == AudioSessionState.AudioSessionStateExpired ||
                session.GetProcessID is 0 or > int.MaxValue)
            {
                continue;
            }

            if (!AudioApplicationProcessResolver.TryResolve(
                    processName: null,
                    preferredProcessId: checked((int)session.GetProcessID),
                    processTree: processTree,
                    out var process))
            {
                continue;
            }

            applications.TryAdd(
                process.ProcessId,
                new AudioApplicationOption(
                    process.ProcessId,
                    process.ProcessName,
                    process.DisplayName));
        }
    }
}

/// <summary>Contains the non-sensitive process identity required by process-tree loopback.</summary>
/// <param name="ProcessId">Resolved root process identifier.</param>
/// <param name="ProcessName">Executable name without a path.</param>
/// <param name="DisplayName">File-description display name or executable fallback.</param>
internal sealed record AudioApplicationProcess(int ProcessId, string ProcessName, string DisplayName);

/// <summary>Resolves a persisted application selection to a currently running root process.</summary>
internal static class AudioApplicationProcessResolver
{
    private const string NewTeamsProcessName = "ms-teams";
    private const string WebView2ProcessName = "msedgewebview2";

    /// <summary>Resolves a persisted application selection to a running process.</summary>
    internal static bool TryResolve(
        string? processName,
        int? preferredProcessId,
        out AudioApplicationProcess process) =>
        TryResolve(processName, preferredProcessId, processTree: null, out process);

    /// <summary>
    /// Resolves an exact persisted PID when it still matches. When deriving an application from an
    /// audio-session child PID, selects the visible or oldest process with that executable name so
    /// multi-process browser/Electron applications normally target their root process tree.
    /// </summary>
    /// <param name="processName">Persisted executable name, or null when deriving it from the PID.</param>
    /// <param name="preferredProcessId">Last selected or audio-session process identifier.</param>
    /// <param name="processTree">Optional captured process tree reused by catalog enumeration.</param>
    /// <param name="process">Resolved non-sensitive process identity.</param>
    /// <returns>Whether a matching running process could be resolved.</returns>
    internal static bool TryResolve(
        string? processName,
        int? preferredProcessId,
        IReadOnlyDictionary<int, ProcessTreeEntry>? processTree,
        out AudioApplicationProcess process)
    {
        process = null!;
        var normalizedName = NormalizeProcessName(processName);

        // The process ID returned by an audio session is often a renderer/utility child (Chrome,
        // Edge, WebView2 and Discord all use this model). Resolve that child to the oldest ancestor
        // with the same executable so IncludeTargetProcessTree covers the application's sibling
        // audio workers as well. The persisted PID is still preferred when it remains alive.
        processTree ??= ProcessTreeSnapshot.TryCapture();
        if (preferredProcessId is > 0 &&
            TryReadProcess(preferredProcessId.Value, out var preferred) &&
            (normalizedName.Length == 0 ||
             string.Equals(preferred.ProcessName, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            normalizedName = preferred.ProcessName;
            if (TryCreateRootSnapshot(preferred, normalizedName, processTree, out var preferredRoot))
            {
                process = ToApplicationProcess(preferredRoot);
                DisposeProcess(preferred.Process);
                DisposeProcess(preferredRoot.Process);
                return true;
            }

            // The process may have exited between the two reads. Continue with a fresh name-based
            // lookup rather than returning a stale PID that could be reused by another process.
            DisposeProcess(preferred.Process);
        }

        if (normalizedName.Length == 0)
        {
            return false;
        }

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(normalizedName);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or NotSupportedException or
            Win32Exception)
        {
            return false;
        }

        try
        {
            var snapshots = candidates
                .Select(TryCreateSnapshot)
                .Where(static snapshot => snapshot is not null)
                .Select(static snapshot => snapshot!)
                .ToArray();
            var roots = snapshots
                .Select(snapshot => TryCreateRootSnapshot(snapshot, normalizedName, processTree, out var root)
                    ? root
                    : snapshot)
                .GroupBy(static snapshot => snapshot.ProcessId)
                .Select(static group => group.First())
                .ToArray();
            var selected = roots
                .OrderByDescending(static snapshot => snapshot.HasMainWindow)
                .ThenBy(static snapshot => snapshot.StartOrder)
                .ThenBy(static snapshot => snapshot.ProcessId)
                .FirstOrDefault();
            if (selected is null)
            {
                return false;
            }

            process = ToApplicationProcess(selected);
            return true;
        }
        finally
        {
            foreach (var candidate in candidates)
            {
                DisposeProcess(candidate);
            }
        }
    }

    /// <summary>
    /// Resolves a process to the oldest same-executable ancestor in a captured process tree.
    /// </summary>
    /// <remarks>
    /// Keeping the executable boundary is deliberate: walking all the way to Explorer would make
    /// an application-loopback request capture unrelated applications hosted by the shell.
    /// </remarks>
    internal static int ResolveSameExecutableRoot(
        int processId,
        string processName,
        IReadOnlyDictionary<int, ProcessTreeEntry> processTree)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(processId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);

        var normalizedName = NormalizeProcessName(processName);
        var currentId = processId;
        var visited = new HashSet<int>();
        while (visited.Add(currentId) &&
               processTree.TryGetValue(currentId, out var current) &&
               current.ParentProcessId > 0 &&
               processTree.TryGetValue(current.ParentProcessId, out var parent) &&
               CanPromoteToParent(current.ProcessName, parent.ProcessName, normalizedName))
        {
            currentId = parent.ProcessId;
        }

        return currentId;
    }

    private static bool CanPromoteToParent(
        string currentProcessName,
        string parentProcessName,
        string requestedProcessName)
    {
        var sameExecutable = string.Equals(
            currentProcessName,
            parentProcessName,
            StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                currentProcessName,
                requestedProcessName,
                StringComparison.OrdinalIgnoreCase);
        var newTeamsWebViewChild = string.Equals(
                currentProcessName,
                WebView2ProcessName,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                parentProcessName,
                NewTeamsProcessName,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                requestedProcessName,
                WebView2ProcessName,
                StringComparison.OrdinalIgnoreCase);

        // This is intentionally the only cross-executable promotion. New Teams renders meeting
        // audio in a WebView2 child, while promoting arbitrary WebView2/host relationships could
        // make process-loopback capture unrelated browser or shell audio.
        return sameExecutable || newTeamsWebViewChild;
    }

    private static bool TryCreateRootSnapshot(
        ProcessSnapshot snapshot,
        string processName,
        IReadOnlyDictionary<int, ProcessTreeEntry> processTree,
        out ProcessSnapshot root)
    {
        var rootId = processTree.Count == 0
            ? snapshot.ProcessId
            : ResolveSameExecutableRoot(snapshot.ProcessId, processName, processTree);
        if (rootId == snapshot.ProcessId)
        {
            root = snapshot;
            return true;
        }

        try
        {
            using var rootProcess = Process.GetProcessById(rootId);
            var rootSnapshot = TryCreateSnapshot(rootProcess);
            if (rootSnapshot is not null)
            {
                root = rootSnapshot;
                return true;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // The ancestor can disappear during process startup/shutdown. The original session
            // process is a safe fallback and still restricts capture to its own descendants.
        }

        root = snapshot;
        return true;
    }

    private static AudioApplicationProcess ToApplicationProcess(ProcessSnapshot snapshot) =>
        new(snapshot.ProcessId, snapshot.ProcessName, snapshot.DisplayName);

    private static string NormalizeProcessName(string? processName)
    {
        var normalized = (processName ?? string.Empty).Trim();
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private static bool TryReadProcess(int processId, out ProcessSnapshot snapshot)
    {
        snapshot = null!;
        try
        {
            using var process = Process.GetProcessById(processId);
            var created = TryCreateSnapshot(process);
            if (created is null)
            {
                return false;
            }

            snapshot = created with { Process = null };
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or NotSupportedException or
            Win32Exception)
        {
            return false;
        }
    }

    private static ProcessSnapshot? TryCreateSnapshot(Process process)
    {
        try
        {
            var processName = process.ProcessName;
            if (string.IsNullOrWhiteSpace(processName))
            {
                return null;
            }

            return new ProcessSnapshot(
                process.Id,
                processName,
                GetDisplayName(process, processName),
                process.MainWindowHandle != nint.Zero,
                GetStartOrder(process),
                process);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return null;
        }
    }

    private static string GetDisplayName(Process process, string processName)
    {
        try
        {
            var description = process.MainModule?.FileVersionInfo.FileDescription?.Trim();
            return string.IsNullOrWhiteSpace(description) ? $"{processName}.exe" : description;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return $"{processName}.exe";
        }
    }

    private static long GetStartOrder(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return long.MaxValue;
        }
    }

    private static void DisposeProcess(Process? process)
    {
        try
        {
            process?.Dispose();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private sealed record ProcessSnapshot(
        int ProcessId,
        string ProcessName,
        string DisplayName,
        bool HasMainWindow,
        long StartOrder,
        Process? Process);
}

/// <summary>Minimal process-tree record used to resolve browser and Electron child processes.</summary>
internal readonly record struct ProcessTreeEntry(
    int ProcessId,
    int ParentProcessId,
    string ProcessName);

/// <summary>Best-effort snapshot of parent process relationships from Toolhelp32.</summary>
internal static class ProcessTreeSnapshot
{
    private const uint SnapshotAllProcesses = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);

    internal static IReadOnlyDictionary<int, ProcessTreeEntry> TryCapture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new Dictionary<int, ProcessTreeEntry>();
        }

        var handle = CreateToolhelp32Snapshot(SnapshotAllProcesses, 0);
        if (handle == InvalidHandleValue)
        {
            return new Dictionary<int, ProcessTreeEntry>();
        }

        try
        {
            var entries = new Dictionary<int, ProcessTreeEntry>();
            var entry = new NativeProcessEntry { Size = (uint)Marshal.SizeOf<NativeProcessEntry>() };
            if (!Process32First(handle, ref entry))
            {
                return entries;
            }

            do
            {
                if (entry.ProcessId is > 0 and <= int.MaxValue)
                {
                    var processName = entry.ExecutableName?.TrimEnd('\0') ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(processName))
                    {
                        entries[(int)entry.ProcessId] = new ProcessTreeEntry(
                            (int)entry.ProcessId,
                            entry.ParentProcessId is > int.MaxValue ? 0 : (int)entry.ParentProcessId,
                            NormalizeProcessName(processName));
                    }
                }
            }
            while (Process32Next(handle, ref entry));

            return entries;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
        {
            return new Dictionary<int, ProcessTreeEntry>();
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private static string NormalizeProcessName(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref NativeProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref NativeProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeProcessEntry
    {
        internal uint Size;
        internal uint Usage;
        internal uint ProcessId;
        internal nint DefaultHeapId;
        internal uint ModuleId;
        internal uint Threads;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string ExecutableName;
    }
}
