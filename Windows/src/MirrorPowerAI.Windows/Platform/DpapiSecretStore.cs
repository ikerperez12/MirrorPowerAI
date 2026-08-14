using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MirrorPowerAI.Core.Security;

namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Protects per-user secrets with Windows DPAPI and stores only encrypted bytes on disk.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("MirrorPowerAI.SecretStore.v1");
    private readonly string _secretsDirectory;

    /// <summary>Maximum UTF-8 size accepted for one plaintext secret.</summary>
    public const int MaximumSecretUtf8Bytes = 128 * 1024;

    /// <summary>Maximum encrypted file size accepted before DPAPI decryption.</summary>
    public const int MaximumProtectedFileBytes = 256 * 1024;

    /// <summary>Initializes the store in <c>%LOCALAPPDATA%\MirrorPowerAI\secrets</c>.</summary>
    public DpapiSecretStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MirrorPowerAI",
            "secrets"))
    {
    }

    /// <summary>Initializes a store in an explicit directory, primarily for isolated tests.</summary>
    /// <param name="secretsDirectory">Absolute directory containing encrypted secret files.</param>
    public DpapiSecretStore(string secretsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsDirectory);
        _secretsDirectory = Path.GetFullPath(secretsDirectory);
    }

    /// <inheritdoc />
    public async Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(name);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumProtectedFileBytes)
        {
            throw new CryptographicException("The protected secret file exceeds the configured safety limit.");
        }

        var encrypted = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(encrypted, cancellationToken).ConfigureAwait(false);
        byte[]? plaintext = null;

        try
        {
            plaintext = Unprotect(encrypted);
            if (plaintext.Length > MaximumSecretUtf8Bytes)
            {
                throw new CryptographicException("The decrypted secret exceeds the configured safety limit.");
            }

            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    /// <inheritdoc />
    public async Task SetSecretAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(name);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        if (Encoding.UTF8.GetByteCount(value) > MaximumSecretUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The secret exceeds the configured safety limit.");
        }

        var plaintext = Encoding.UTF8.GetBytes(value);
        byte[]? encrypted = null;
        var temporaryPath = string.Empty;

        try
        {
            encrypted = Protect(plaintext);
            Directory.CreateDirectory(_secretsDirectory);
            temporaryPath = Path.Combine(_secretsDirectory, $"{name}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = string.Empty;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }

            if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <inheritdoc />
    public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(name);
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetSecretPath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("Secret names may only contain ASCII letters, digits, period, underscore, and hyphen.", nameof(name));
        }

        return Path.Combine(_secretsDirectory, $"{name}.bin");
    }

    private static byte[] Protect(byte[] plaintext)
    {
        var input = AllocateBlob(plaintext);
        var entropy = AllocateBlob(OptionalEntropy);

        try
        {
            if (!CryptProtectData(
                    ref input,
                    nint.Zero,
                    ref entropy,
                    nint.Zero,
                    nint.Zero,
                    CryptProtectUiForbidden,
                    out var output))
            {
                throw CreateCryptographicException("Windows DPAPI could not protect the secret.");
            }

            return CopyAndFreeOutput(output);
        }
        finally
        {
            FreeInputBlob(input);
            FreeInputBlob(entropy);
        }
    }

    private static byte[] Unprotect(byte[] encrypted)
    {
        var input = AllocateBlob(encrypted);
        var entropy = AllocateBlob(OptionalEntropy);

        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    nint.Zero,
                    ref entropy,
                    nint.Zero,
                    nint.Zero,
                    CryptProtectUiForbidden,
                    out var output))
            {
                throw CreateCryptographicException("Windows DPAPI could not read the protected secret.");
            }

            return CopyAndFreeOutput(output);
        }
        finally
        {
            FreeInputBlob(input);
            FreeInputBlob(entropy);
        }
    }

    private static DataBlob AllocateBlob(byte[] value)
    {
        var pointer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, pointer, value.Length);
        return new DataBlob(value.Length, pointer);
    }

    private static void FreeInputBlob(DataBlob blob)
    {
        if (blob.Data == nint.Zero)
        {
            return;
        }

        var zeros = new byte[blob.Size];
        Marshal.Copy(zeros, 0, blob.Data, zeros.Length);
        Marshal.FreeHGlobal(blob.Data);
    }

    private static byte[] CopyAndFreeOutput(DataBlob output)
    {
        try
        {
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != nint.Zero)
            {
                var zeros = new byte[output.Size];
                Marshal.Copy(zeros, 0, output.Data, zeros.Length);
                _ = LocalFree(output.Data);
            }
        }
    }

    private static CryptographicException CreateCryptographicException(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return new CryptographicException($"{message} {new Win32Exception(error).Message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal DataBlob(int size, nint data)
        {
            Size = size;
            Data = data;
        }

        internal int Size;
        internal nint Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        nint description,
        ref DataBlob optionalEntropy,
        nint reserved,
        nint promptStructure,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        nint description,
        ref DataBlob optionalEntropy,
        nint reserved,
        nint promptStructure,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
