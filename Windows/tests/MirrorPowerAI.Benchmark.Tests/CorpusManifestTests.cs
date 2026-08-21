using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using MirrorPowerAI.Benchmark;

namespace MirrorPowerAI.Benchmark.Tests;

public sealed class CorpusManifestTests
{
    [Fact]
    public void ParseManifest_ClosedV1Schema_ReturnsApprovedCorpusMetadata()
    {
        var loader = new CorpusManifestLoader(new FakeCorpusFileSystem());

        var manifest = loader.ParseManifest(ValidManifest(), ManifestDirectory, new string('a', 64));

        Assert.Equal("qa-spanish", manifest.Id);
        Assert.Equal("2026.08", manifest.Revision);
        Assert.Equal("CC0-1.0", manifest.License);
        Assert.Equal("https://example.test/dataset", manifest.Source);
        Assert.Equal(new string('a', 64), manifest.ManifestSha256);
        Assert.Single(manifest.Items);
    }

    [Theory]
    [InlineData("..\\outside.wav")]
    [InlineData("C:\\private\\audio.wav")]
    [InlineData("\\\\server\\share\\audio.wav")]
    [InlineData("audio\\..\\outside.wav")]
    public void ParseManifest_UnsafeAssetPath_RejectsWithoutEchoingPath(string unsafePath)
    {
        var loader = new CorpusManifestLoader(new FakeCorpusFileSystem());

        var exception = Assert.Throws<CorpusManifestLoader.CorpusManifestException>(() =>
            loader.ParseManifest(ValidManifest(audioPath: unsafePath), ManifestDirectory));

        Assert.DoesNotContain(unsafePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("ruta", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseManifest_UnknownRootMetadata_RejectsClosedSchema()
    {
        var loader = new CorpusManifestLoader(new FakeCorpusFileSystem());
        var json = ValidManifest().Replace(
            "\"items\"",
            "\"unknown\": true, \"items\"",
            StringComparison.Ordinal);

        var exception = Assert.Throws<CorpusManifestLoader.CorpusManifestException>(() =>
            loader.ParseManifest(json, ManifestDirectory));

        Assert.Contains("desconocidos", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseManifest_DuplicateItemIdentifiers_RejectsCorpus()
    {
        var loader = new CorpusManifestLoader(new FakeCorpusFileSystem());
        var json = ValidManifest(
            items: """
                {"id":"item-1","audio":"audio/one.wav","audioSha256":"0000000000000000000000000000000000000000000000000000000000000000","reference":"reference/one.txt","referenceSha256":"1111111111111111111111111111111111111111111111111111111111111111"},
                {"id":"item-1","audio":"audio/two.wav","audioSha256":"2222222222222222222222222222222222222222222222222222222222222222","reference":"reference/two.txt","referenceSha256":"3333333333333333333333333333333333333333333333333333333333333333"}
                """);

        var exception = Assert.Throws<CorpusManifestLoader.CorpusManifestException>(() =>
            loader.ParseManifest(json, ManifestDirectory));

        Assert.Contains("identificadores duplicados", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseManifest_ReusedAssetFile_RejectsCorpus()
    {
        var loader = new CorpusManifestLoader(new FakeCorpusFileSystem());
        var json = ValidManifest(
            items: """
                {"id":"item-1","audio":"audio/shared.wav","audioSha256":"0000000000000000000000000000000000000000000000000000000000000000","reference":"reference/one.txt","referenceSha256":"1111111111111111111111111111111111111111111111111111111111111111"},
                {"id":"item-2","audio":"audio/shared.wav","audioSha256":"2222222222222222222222222222222222222222222222222222222222222222","reference":"reference/two.txt","referenceSha256":"3333333333333333333333333333333333333333333333333333333333333333"}
                """);

        var exception = Assert.Throws<CorpusManifestLoader.CorpusManifestException>(() =>
            loader.ParseManifest(json, ManifestDirectory));

        Assert.Contains("reutiliza", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseManifest_InvalidAssetSha256_RejectsCorpus()
    {
        var loader = new CorpusManifestLoader(new FakeCorpusFileSystem());

        var exception = Assert.Throws<CorpusManifestLoader.CorpusManifestException>(() =>
            loader.ParseManifest(ValidManifest(audioSha256: "not-a-sha"), ManifestDirectory));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseManifest_MissingReference_RejectsCorpus()
    {
        var loader = new CorpusManifestLoader(new FakeCorpusFileSystem
        {
            MissingSuffix = Path.Combine("reference", "item-1.txt"),
        });

        var exception = Assert.Throws<CorpusManifestLoader.CorpusManifestException>(() =>
            loader.ParseManifest(ValidManifest(), ManifestDirectory));

        Assert.Contains("Falta", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseManifest_OversizedReference_RejectsCorpus()
    {
        var loader = new CorpusManifestLoader(new FakeCorpusFileSystem
        {
            OversizedReference = true,
        });

        var exception = Assert.Throws<CorpusManifestLoader.CorpusManifestException>(() =>
            loader.ParseManifest(ValidManifest(), ManifestDirectory));

        Assert.Contains("referencia", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_ReparsePointManifest_RejectsBeforeReadingMetadata()
    {
        var sensitivePath = Path.Combine(ManifestDirectory, "private-corpus.json");
        var fileSystem = new FakeCorpusFileSystem
        {
            ReparseSuffix = "private-corpus.json",
            Json = ValidManifest(),
        };
        var loader = new CorpusManifestLoader(fileSystem);

        var exception = await Assert.ThrowsAsync<CorpusManifestLoader.CorpusManifestException>(() =>
            loader.LoadAsync(sensitivePath, CancellationToken.None));

        Assert.DoesNotContain(sensitivePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fileSystem.ReadCount);
        Assert.Contains("puntos de reparación", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_UsesTheSameBoundedBytesForManifestDigestAndParsing()
    {
        var json = ValidManifest();
        var fileSystem = new FakeCorpusFileSystem
        {
            Json = json,
        };
        var loader = new CorpusManifestLoader(fileSystem);
        var manifestPath = Path.Combine(ManifestDirectory, "corpus.json");

        var manifest = await loader.LoadAsync(manifestPath, CancellationToken.None);

        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
            manifest.ManifestSha256);
        Assert.Equal(1, fileSystem.ReadCount);
        Assert.Single(manifest.Items);
    }

    private static string ManifestDirectory => Path.Combine(Path.GetTempPath(), "MirrorPowerAI-CorpusTests");

    private static string ValidManifest(
        string audioPath = "audio/item-1.wav",
        string audioSha256 = "0000000000000000000000000000000000000000000000000000000000000000",
        string? items = null)
    {
        var defaultItem = JsonSerializer.Serialize(new
        {
            id = "item-1",
            audio = audioPath,
            audioSha256,
            reference = "reference/item-1.txt",
            referenceSha256 = "1111111111111111111111111111111111111111111111111111111111111111",
        });
        return string.Concat(
            "{\"version\":1,\"id\":\"qa-spanish\",\"revision\":\"2026.08\",",
            "\"license\":\"CC0-1.0\",\"source\":\"https://example.test/dataset\",\"items\":[",
            items ?? defaultItem,
            "]}");
    }

    private sealed class FakeCorpusFileSystem : ICorpusFileSystem
    {
        public string? MissingSuffix { get; init; }

        public bool OversizedReference { get; init; }

        public string? ReparseSuffix { get; init; }

        public string Json { get; init; } = ValidManifest();

        public int ReadCount { get; private set; }

        public bool FileExists(string path) =>
            MissingSuffix is null || !path.EndsWith(MissingSuffix, StringComparison.OrdinalIgnoreCase);

        public long GetFileLength(string path) =>
            OversizedReference && path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                ? CorpusManifestLoader.MaximumReferenceBytes + 1L
                : 128;

        public FileAttributes GetAttributes(string path) =>
            ReparseSuffix is not null && path.EndsWith(ReparseSuffix, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.ReparsePoint
                : FileAttributes.Normal;

        public Task<byte[]> ReadAllBytesAsync(
            string path,
            string owningDirectory,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(Encoding.UTF8.GetBytes(Json));
        }
    }
}
