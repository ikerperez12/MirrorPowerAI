namespace MirrorPowerAI.Core.Models;

/// <summary>
/// Describes an immutable Whisper model artifact pinned by origin, size, and SHA-256.
/// </summary>
/// <param name="FileName">The safe file name used in the model directory.</param>
/// <param name="DownloadUri">The pinned HTTPS artifact URI.</param>
/// <param name="ExpectedSize">The exact expected file size in bytes.</param>
/// <param name="Sha256">The lowercase expected SHA-256 hexadecimal digest.</param>
public sealed record WhisperModelDescriptor(
    string FileName,
    Uri DownloadUri,
    long ExpectedSize,
    string Sha256)
{
    /// <summary>
    /// Gets the pinned default <c>ggml-base.bin</c> descriptor.
    /// </summary>
    public static WhisperModelDescriptor DefaultBase { get; } = new(
        "ggml-base.bin",
        new Uri(
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/" +
            "5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin"),
        147_951_465,
        "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe");

    /// <summary>
    /// Validates the descriptor before any filesystem or network access occurs.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when a descriptor field is unsafe or invalid.</exception>
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(FileName) ||
            !string.Equals(Path.GetFileName(FileName), FileName, StringComparison.Ordinal) ||
            FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("El nombre del modelo no es un nombre de archivo seguro.", nameof(FileName));
        }

        if (DownloadUri is null ||
            !DownloadUri.IsAbsoluteUri ||
            DownloadUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(DownloadUri.UserInfo))
        {
            throw new ArgumentException("El origen del modelo debe ser una URL HTTPS sin credenciales.", nameof(DownloadUri));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(ExpectedSize, 1);

        if (Sha256.Length != 64 || !Sha256.All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("El SHA-256 del modelo no es válido.", nameof(Sha256));
        }
    }
}

/// <summary>
/// Identifies why a Whisper model could not be safely activated.
/// </summary>
public enum WhisperModelErrorKind
{
    /// <summary>The downloaded or existing file size did not match.</summary>
    SizeMismatch,

    /// <summary>The downloaded or existing SHA-256 did not match.</summary>
    HashMismatch,

    /// <summary>The download endpoint returned an unsuccessful status.</summary>
    DownloadFailed,
}

/// <summary>
/// Indicates that a Whisper model failed a supply-chain integrity gate.
/// </summary>
public sealed class WhisperModelException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WhisperModelException"/> class.
    /// </summary>
    /// <param name="kind">The stable integrity failure category.</param>
    /// <param name="message">A safe message that does not contain a local path or response body.</param>
    /// <param name="innerException">The underlying network exception, when applicable.</param>
    public WhisperModelException(
        WhisperModelErrorKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>
    /// Gets the integrity failure category.
    /// </summary>
    public WhisperModelErrorKind Kind { get; }
}
