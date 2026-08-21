using System.Runtime.InteropServices;

namespace MirrorPowerAI.Benchmark;

/// <summary>
/// Verifies the final Windows object reached by an already-open file handle.
/// This closes the path-check/open race for corpus inputs: even if a directory
/// is changed into a junction between validation and open, the opened handle
/// must still resolve below the manifest-owned directory.
/// </summary>
internal static class WindowsPathSafety
{
    private const uint FileNameNormalized = 0;
    private const int MaximumFinalPathCharacters = 32_768;

    public static void EnsureOpenFileIsUnderDirectory(FileStream stream, string owningDirectory)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(owningDirectory);
        if (!OperatingSystem.IsWindows())
        {
            throw Invalid("La comprobación de rutas de corpus requiere Windows.");
        }

        var finalPath = NormalizeDevicePath(GetFinalPath(stream));
        var root = NormalizeDirectory(owningDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!finalPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("Un archivo del corpus no permanece dentro de su directorio autorizado.");
        }
    }

    private static string GetFinalPath(FileStream stream)
    {
        var capacity = 512;
        while (capacity <= MaximumFinalPathCharacters)
        {
            var buffer = new char[capacity];
            var length = GetFinalPathNameByHandle(
                stream.SafeFileHandle,
                buffer,
                checked((uint)buffer.Length),
                FileNameNormalized);
            if (length == 0)
            {
                throw Invalid("No se pudo verificar la ruta final de un archivo de corpus.");
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, checked((int)length));
            }

            capacity = checked((int)length + 1);
        }

        throw Invalid("La ruta final de un archivo de corpus no es válida.");
    }

    private static string NormalizeDevicePath(string path)
    {
        const string ExtendedUncPrefix = "\\\\?\\UNC\\";
        const string ExtendedPathPrefix = "\\\\?\\";
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "\\\\" + path[ExtendedUncPrefix.Length..];
        }

        return path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal)
            ? path[ExtendedPathPrefix.Length..]
            : path;
    }

    private static string NormalizeDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static CorpusManifestLoader.CorpusManifestException Invalid(string message) =>
        new(message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file,
        [Out] char[] path,
        uint pathLength,
        uint flags);
}
