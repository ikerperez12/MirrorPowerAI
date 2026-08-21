using System.Buffers.Binary;

namespace MirrorPowerAI.Benchmark;

internal sealed class NormalizedWaveFile : IDisposable
{
    private const int ExpectedSampleRate = 16_000;
    private const int ExpectedChannelCount = 1;
    private const int ExpectedBitsPerSample = 16;
    private const int ExpectedBlockAlignment = 2;
    private const int ExpectedBytesPerSecond = 32_000;
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(5);

    private NormalizedWaveFile(FileStream stream, TimeSpan duration)
    {
        Stream = stream;
        Duration = duration;
    }

    public FileStream Stream { get; }

    public TimeSpan Duration { get; }

    public static NormalizedWaveFile Open(string path) => Open(path, validateOpenedStream: null);

    /// <summary>
    /// Opens a WAV and gives a caller a chance to validate the final opened
    /// handle before any RIFF metadata is read from it.
    /// </summary>
    /// <param name="path">The candidate WAV path.</param>
    /// <param name="validateOpenedStream">Optional validation applied to the opened handle before parsing.</param>
    /// <returns>A validated normalized WAV stream.</returns>
    internal static NormalizedWaveFile Open(
        string path,
        Action<FileStream>? validateOpenedStream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new ArgumentException("La ruta del WAV de entrada no es válida.", nameof(path), exception);
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new UnauthorizedAccessException("No se pudo acceder al WAV de entrada.", exception);
        }
        catch (IOException exception)
        {
            throw new IOException("No se pudo abrir el WAV de entrada.", exception);
        }

        try
        {
            validateOpenedStream?.Invoke(stream);
            var duration = ValidateAndGetDuration(stream);
            stream.Position = 0;
            return new NormalizedWaveFile(stream, duration);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Stream.Dispose();
        GC.SuppressFinalize(this);
    }

    private static TimeSpan ValidateAndGetDuration(Stream stream)
    {
        if (stream.Length < 44 || stream.Length > uint.MaxValue + 8L)
        {
            throw InvalidWave("el tamaño del archivo no es válido");
        }

        Span<byte> riffHeader = stackalloc byte[12];
        stream.ReadExactly(riffHeader);
        if (!riffHeader[..4].SequenceEqual("RIFF"u8)
            || !riffHeader[8..12].SequenceEqual("WAVE"u8))
        {
            throw InvalidWave("faltan las cabeceras RIFF/WAVE");
        }

        var declaredRiffSize = BinaryPrimitives.ReadUInt32LittleEndian(riffHeader[4..8]);
        if (declaredRiffSize + 8L != stream.Length)
        {
            throw InvalidWave("el tamaño RIFF declarado no coincide con el archivo");
        }

        var formatFound = false;
        var dataFound = false;
        uint dataByteCount = 0;
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> format = stackalloc byte[16];

        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < chunkHeader.Length)
            {
                throw InvalidWave("hay una cabecera de bloque incompleta");
            }

            stream.ReadExactly(chunkHeader);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            var paddedChunkSize = (long)chunkSize + (chunkSize & 1u);
            if (paddedChunkSize > stream.Length - stream.Position)
            {
                throw InvalidWave("un bloque excede el final del archivo");
            }

            if (chunkHeader[..4].SequenceEqual("fmt "u8))
            {
                if (formatFound || chunkSize != 16)
                {
                    throw InvalidWave("el bloque fmt no es PCM canónico");
                }

                stream.ReadExactly(format);
                ValidateFormat(format);
                formatFound = true;
                continue;
            }

            if (chunkHeader[..4].SequenceEqual("data"u8))
            {
                if (dataFound || chunkSize == 0 || chunkSize % ExpectedBlockAlignment != 0)
                {
                    throw InvalidWave("el bloque de audio está vacío, duplicado o desalineado");
                }

                dataFound = true;
                dataByteCount = chunkSize;
            }

            stream.Seek(paddedChunkSize, SeekOrigin.Current);
        }

        if (!formatFound || !dataFound)
        {
            throw InvalidWave("faltan los bloques fmt o data");
        }

        var duration = TimeSpan.FromSeconds(dataByteCount / (double)ExpectedBytesPerSecond);
        if (duration > MaximumDuration)
        {
            throw InvalidWave("la duración supera el límite de cinco minutos de la aplicación");
        }

        return duration;
    }

    private static void ValidateFormat(ReadOnlySpan<byte> format)
    {
        var encoding = BinaryPrimitives.ReadUInt16LittleEndian(format[..2]);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..4]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..8]);
        var bytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(format[8..12]);
        var blockAlignment = BinaryPrimitives.ReadUInt16LittleEndian(format[12..14]);
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format[14..16]);

        if (encoding != 1
            || channels != ExpectedChannelCount
            || sampleRate != ExpectedSampleRate
            || bytesPerSecond != ExpectedBytesPerSecond
            || blockAlignment != ExpectedBlockAlignment
            || bitsPerSample != ExpectedBitsPerSample)
        {
            throw InvalidWave("se requiere PCM de 16 kHz, mono y 16 bits");
        }
    }

    private static InvalidDataException InvalidWave(string detail) =>
        new($"El WAV no está normalizado: {detail}.");
}
