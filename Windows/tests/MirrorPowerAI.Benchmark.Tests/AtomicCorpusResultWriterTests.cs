using MirrorPowerAI.Benchmark;

namespace MirrorPowerAI.Benchmark.Tests;

public sealed class AtomicCorpusResultWriterTests
{
    [Fact]
    public async Task WriteAsync_ReplacesTargetWithCompleteDeterministicPayloadAndRemovesTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var targetPath = Path.Combine(directory.Path, "result.json");
        await File.WriteAllTextAsync(targetPath, "previous-safe-result");
        var result = CreateResult();

        await new AtomicCorpusResultWriter().WriteAsync(
            targetPath,
            result,
            CancellationToken.None);

        Assert.Equal(CorpusBenchmarkOutput.Serialize(result), await File.ReadAllTextAsync(targetPath));
        Assert.Empty(TemporaryFiles(directory.Path));
    }

    [Fact]
    public async Task WriteAsync_ReplacementFailure_PreservesExistingTargetAndRemovesTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var targetPath = Path.Combine(directory.Path, "result.json");
        const string priorResult = "previous-safe-result";
        await File.WriteAllTextAsync(targetPath, priorResult);

        using (var lockTarget = new FileStream(
                   targetPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                new AtomicCorpusResultWriter().WriteAsync(
                    targetPath,
                    CreateResult(),
                    CancellationToken.None));
        }

        Assert.Equal(priorResult, await File.ReadAllTextAsync(targetPath));
        Assert.Empty(TemporaryFiles(directory.Path));
    }

    [Fact]
    public async Task WriteAsync_PreCancelledRequest_PreservesExistingTargetWithoutCreatingTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var targetPath = Path.Combine(directory.Path, "result.json");
        const string priorResult = "previous-safe-result";
        await File.WriteAllTextAsync(targetPath, priorResult);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AtomicCorpusResultWriter().WriteAsync(
                targetPath,
                CreateResult(),
                cancellationSource.Token));

        Assert.Equal(priorResult, await File.ReadAllTextAsync(targetPath));
        Assert.Empty(TemporaryFiles(directory.Path));
    }

    private static IEnumerable<string> TemporaryFiles(string directoryPath) =>
        Directory.EnumerateFiles(directoryPath, ".result.json.*.tmp", SearchOption.TopDirectoryOnly);

    private static CorpusBenchmarkResult CreateResult() =>
        new(
            "qa-spanish",
            "2026.08",
            "CC0-1.0",
            "https://example.test/dataset",
            new string('a', 64),
            BenchmarkModel.Base,
            "es",
            4,
            Stable: true,
            ItemCount: 2,
            AudioDuration: TimeSpan.FromSeconds(10),
            ModelVerificationElapsed: TimeSpan.FromMilliseconds(10),
            WhisperElapsed: TimeSpan.FromSeconds(1.9),
            EditCount: 2,
            ReferenceWordCount: 100);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"MirrorPowerAI-AtomicCorpus-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
