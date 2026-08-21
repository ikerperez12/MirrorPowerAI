using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MirrorPowerAI.Benchmark;

namespace MirrorPowerAI.Benchmark.Tests;

public sealed class CorpusAssetValidatorTests
{
    [Fact]
    public async Task ValidateAsync_ExactWavAndReferenceHashes_PassesBeforeInference()
    {
        using var corpus = new TemporaryCorpus();
        var validator = new CorpusAssetValidator();

        await validator.ValidateAsync(corpus.Manifest, CancellationToken.None);
    }

    [Fact]
    public async Task ValidateAsync_MutatedAudio_RejectsBeforeAnyInferenceAndDoesNotExposePath()
    {
        using var corpus = new TemporaryCorpus();
        var audio = File.ReadAllBytes(corpus.AudioPath);
        audio[^1] ^= 0x01;
        File.WriteAllBytes(corpus.AudioPath, audio);
        var validator = new CorpusAssetValidator();

        var exception = await Assert.ThrowsAsync<CorpusManifestLoader.CorpusManifestException>(() =>
            validator.ValidateAsync(corpus.Manifest, CancellationToken.None));

        Assert.DoesNotContain(corpus.AudioPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_MutatedReference_RejectsBeforeAnyInferenceAndDoesNotExposeText()
    {
        using var corpus = new TemporaryCorpus();
        const string privateReference = "referencia privada que no debe salir";
        await File.WriteAllTextAsync(corpus.ReferencePath, privateReference, Encoding.UTF8);
        var validator = new CorpusAssetValidator();

        var exception = await Assert.ThrowsAsync<CorpusManifestLoader.CorpusManifestException>(() =>
            validator.ValidateAsync(corpus.Manifest, CancellationToken.None));

        Assert.DoesNotContain(privateReference, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(corpus.ReferencePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_ReparsePointReportedByFileSystem_RejectsBeforeOpeningAsset()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MirrorPowerAI-Corpus-Reparse");
        var audioPath = Path.Combine(directory, "audio", "sample.wav");
        var manifest = CreateManifest(
            directory,
            new CorpusManifestItem(
                "item-1",
                audioPath,
                new string('0', 64),
                Path.Combine(directory, "reference", "sample.txt"),
                new string('1', 64)));
        var validator = new CorpusAssetValidator(new ReparseFileSystem(audioPath));

        var exception = await Assert.ThrowsAsync<CorpusManifestLoader.CorpusManifestException>(() =>
            validator.ValidateAsync(manifest, CancellationToken.None));

        Assert.Contains("puntos de reparación", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_Utf16ReferenceWithMatchingHash_RejectsNonUtf8Content()
    {
        using var corpus = new TemporaryCorpus();
        var utf16Encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var utf16Reference = utf16Encoding.GetPreamble()
            .Concat(utf16Encoding.GetBytes("hola mundo"))
            .ToArray();
        File.WriteAllBytes(corpus.ReferencePath, utf16Reference);
        var item = corpus.Manifest.Items[0] with
        {
            ReferenceSha256 = Convert.ToHexStringLower(SHA256.HashData(utf16Reference)),
        };
        var manifest = corpus.Manifest with
        {
            Items = [item],
        };
        var validator = new CorpusAssetValidator();

        var exception = await Assert.ThrowsAsync<CorpusManifestLoader.CorpusManifestException>(() =>
            validator.ValidateAsync(manifest, CancellationToken.None));

        Assert.Contains("referencia", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_Utf8BomReferenceWithMatchingHash_AcceptsUtf8Content()
    {
        using var corpus = new TemporaryCorpus();
        var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var reference = utf8WithBom.GetPreamble()
            .Concat(utf8WithBom.GetBytes("hola mundo"))
            .ToArray();
        var manifest = ReplaceReference(corpus, reference);
        var validator = new CorpusAssetValidator();

        await validator.ValidateAsync(manifest, CancellationToken.None);
    }

    [Theory]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public async Task ValidateAsync_NonUtf8BomReferenceWithMatchingHash_RejectsContent(string encodingName)
    {
        using var corpus = new TemporaryCorpus();
        var reference = CreateNonUtf8BomReference(encodingName);
        var manifest = ReplaceReference(corpus, reference);
        var validator = new CorpusAssetValidator();

        var exception = await Assert.ThrowsAsync<CorpusManifestLoader.CorpusManifestException>(() =>
            validator.ValidateAsync(manifest, CancellationToken.None));

        Assert.DoesNotContain(corpus.ReferencePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("referencia", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_InvalidUtf8ReferenceWithMatchingHash_RejectsContent()
    {
        using var corpus = new TemporaryCorpus();
        byte[] invalidUtf8 = [.. "hola "u8, 0xc3, 0x28];
        var manifest = ReplaceReference(corpus, invalidUtf8);
        var validator = new CorpusAssetValidator();

        var exception = await Assert.ThrowsAsync<CorpusManifestLoader.CorpusManifestException>(() =>
            validator.ValidateAsync(manifest, CancellationToken.None));

        Assert.DoesNotContain(corpus.ReferencePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("referencia", exception.Message, StringComparison.Ordinal);
    }

    private static CorpusManifest ReplaceReference(TemporaryCorpus corpus, byte[] reference)
    {
        File.WriteAllBytes(corpus.ReferencePath, reference);
        var item = corpus.Manifest.Items[0] with
        {
            ReferenceSha256 = Convert.ToHexStringLower(SHA256.HashData(reference)),
        };

        return corpus.Manifest with
        {
            Items = [item],
        };
    }

    private static byte[] CreateNonUtf8BomReference(string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf16-le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf16-be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            "utf32-le" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
            "utf32-be" => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName)),
        };

        return encoding.GetPreamble()
            .Concat(encoding.GetBytes("hola mundo"))
            .ToArray();
    }

    private static CorpusManifest CreateManifest(string directory, CorpusManifestItem item) =>
        new(
            "qa-spanish",
            "2026.08",
            "CC0-1.0",
            "https://example.test/dataset",
            new string('a', 64),
            directory,
            [item]);

    private sealed class TemporaryCorpus : IDisposable
    {
        public TemporaryCorpus()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                $"MirrorPowerAI-CorpusAssets-{Guid.NewGuid():N}");
            var audioDirectory = Path.Combine(DirectoryPath, "audio");
            var referenceDirectory = Path.Combine(DirectoryPath, "reference");
            Directory.CreateDirectory(audioDirectory);
            Directory.CreateDirectory(referenceDirectory);
            AudioPath = Path.Combine(audioDirectory, "sample.wav");
            ReferencePath = Path.Combine(referenceDirectory, "sample.txt");
            File.WriteAllBytes(AudioPath, CreateWave());
            File.WriteAllText(ReferencePath, "hola mundo", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Manifest = CreateManifest(
                DirectoryPath,
                new CorpusManifestItem(
                    "item-1",
                    AudioPath,
                    HashFile(AudioPath),
                    ReferencePath,
                    HashFile(ReferencePath)));
        }

        public string DirectoryPath { get; }

        public string AudioPath { get; }

        public string ReferencePath { get; }

        public CorpusManifest Manifest { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }

        private static string HashFile(string path) =>
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

        private static byte[] CreateWave()
        {
            const int dataByteCount = 320;
            var wave = new byte[44 + dataByteCount];
            "RIFF"u8.CopyTo(wave);
            BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4, 4), checked((uint)(wave.Length - 8)));
            "WAVE"u8.CopyTo(wave.AsSpan(8));
            "fmt "u8.CopyTo(wave.AsSpan(12));
            BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16, 4), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24, 4), 16_000);
            BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28, 4), 32_000);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32, 2), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34, 2), 16);
            "data"u8.CopyTo(wave.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40, 4), dataByteCount);
            return wave;
        }
    }

    private sealed class ReparseFileSystem(string reparsePath) : ICorpusFileSystem
    {
        public bool FileExists(string path) => true;

        public long GetFileLength(string path) => 1;

        public FileAttributes GetAttributes(string path) =>
            string.Equals(path, reparsePath, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.ReparsePoint
                : FileAttributes.Normal;

        public Task<byte[]> ReadAllBytesAsync(
            string path,
            string owningDirectory,
            int maximumBytes,
            CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<byte>());
    }
}
