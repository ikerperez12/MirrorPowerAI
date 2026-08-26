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
        var applications = new Dictionary<string, AudioApplicationOption>(
            StringComparer.OrdinalIgnoreCase);

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    AddDeviceSessions(device, applications, cancellationToken);
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
            .ToArray();
        return Task.FromResult(result);
    }

    private static void AddDeviceSessions(
        MMDevice device,
        IDictionary<string, AudioApplicationOption> applications,
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
                    out var process))
            {
                continue;
            }

            applications.TryAdd(
                process.ProcessName,
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
    /// <summary>
    /// Resolves an exact persisted PID when it still matches. When deriving an application from an
    /// audio-session child PID, selects the visible or oldest process with that executable name so
    /// multi-process browser/Electron applications normally target their root process tree.
    /// </summary>
    /// <param name="processName">Persisted executable name, or null when deriving it from the PID.</param>
    /// <param name="preferredProcessId">Last selected or audio-session process identifier.</param>
    /// <param name="process">Resolved non-sensitive process identity.</param>
    /// <returns>Whether a matching running process could be resolved.</returns>
    internal static bool TryResolve(
        string? processName,
        int? preferredProcessId,
        out AudioApplicationProcess process)
    {
        process = null!;
        var preferExactProcess = !string.IsNullOrWhiteSpace(processName);
        var normalizedName = NormalizeProcessName(processName);
        if (preferredProcessId is > 0 &&
            TryReadProcess(preferredProcessId.Value, out var preferred) &&
            (normalizedName.Length == 0 ||
             string.Equals(preferred.ProcessName, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            normalizedName = preferred.ProcessName;
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
        catch (InvalidOperationException)
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
            var selected = preferExactProcess
                ? snapshots.FirstOrDefault(snapshot => snapshot.ProcessId == preferredProcessId)
                : null;
            selected ??= snapshots
                .OrderByDescending(static snapshot => snapshot.HasMainWindow)
                .ThenBy(static snapshot => snapshot.StartOrder)
                .FirstOrDefault();
            if (selected is null)
            {
                return false;
            }

            process = new AudioApplicationProcess(
                selected.ProcessId,
                selected.ProcessName,
                selected.DisplayName);
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
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
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
        catch (InvalidOperationException)
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
