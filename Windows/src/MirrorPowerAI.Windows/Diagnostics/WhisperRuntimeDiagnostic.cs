using System.Runtime.InteropServices;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace MirrorPowerAI.Windows.Diagnostics;

/// <summary>
/// Loads the pinned Whisper CPU runtime without opening a model or processing user data.
/// </summary>
internal static class WhisperRuntimeDiagnostic
{
    /// <summary>
    /// Verifies the expected operating system, process architecture, native runtime selection, and
    /// a harmless exported system-information call.
    /// </summary>
    internal static bool Verify()
    {
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            RuntimeOptions.LoadedLibrary is not null)
        {
            return false;
        }

        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
        var runtimeInformation = WhisperFactory.GetRuntimeInfo();
        return RuntimeOptions.LoadedLibrary == RuntimeLibrary.Cpu &&
            !string.IsNullOrWhiteSpace(runtimeInformation);
    }
}
