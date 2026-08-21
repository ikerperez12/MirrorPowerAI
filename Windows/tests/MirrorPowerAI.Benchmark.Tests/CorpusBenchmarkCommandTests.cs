using MirrorPowerAI.Benchmark;
using MirrorPowerAI.Core.Models;

namespace MirrorPowerAI.Benchmark.Tests;

public sealed class CorpusBenchmarkCommandTests
{
    [Fact]
    public async Task RunAsync_UsesWeightedAggregateWerAndRtfAfterFullPreflight()
    {
        var events = new List<string>();
        var command = new CorpusBenchmarkCommand(
            new FixedManifestLoader(CreateManifest()),
            new RecordingPreflight(events),
            new RecordingModelResolver(events),
            new SequenceExecutor(
                events,
                [
                    new CorpusItemMetrics(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1),
                    new CorpusItemMetrics(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(0.9), 1, 99),
                ]),
            new RecordingResultWriter());

        var result = await command.RunAsync(CreateOptions(), CancellationToken.None);

        Assert.Equal(2, result.ItemCount);
        Assert.Equal(2, result.EditCount);
        Assert.Equal(100, result.ReferenceWordCount);
        Assert.Equal(0.02, result.WordErrorRate, precision: 10);
        Assert.Equal(0.19, result.RealTimeFactor, precision: 10);
        Assert.Equal(["preflight", "model", "item", "item"], events);
    }

    [Fact]
    public async Task RunAsync_StableModePassesCachedRequirementToModelResolverWithoutNetworkAbstraction()
    {
        var modelResolver = new RecordingModelResolver([]);
        var command = new CorpusBenchmarkCommand(
            new FixedManifestLoader(CreateManifest()),
            new RecordingPreflight([]),
            modelResolver,
            new SequenceExecutor(
                [],
                [
                    new CorpusItemMetrics(TimeSpan.FromSeconds(1), TimeSpan.Zero, 0, 1),
                    new CorpusItemMetrics(TimeSpan.FromSeconds(1), TimeSpan.Zero, 0, 1),
                ]),
            new RecordingResultWriter());

        await command.RunAsync(CreateOptions(stable: true), CancellationToken.None);

        Assert.True(modelResolver.SawRequireCachedModel);
        Assert.True(modelResolver.SawStable);
    }

    [Fact]
    public async Task RunAsync_StableWithoutCachedSpanishRequirements_FailsBeforeLoadingManifest()
    {
        var loader = new FixedManifestLoader(CreateManifest());
        var command = new CorpusBenchmarkCommand(
            loader,
            new RecordingPreflight([]),
            new RecordingModelResolver([]),
            new SequenceExecutor([], []),
            new RecordingResultWriter());

        var invalidOptions = CreateOptions(stable: true) with
        {
            RequireCachedModel = false,
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            command.RunAsync(invalidOptions, CancellationToken.None));

        Assert.False(loader.WasCalled);
    }

    [Fact]
    public async Task RunAndWriteAsync_ItemFailure_DoesNotWritePartialSuccessResult()
    {
        var writer = new RecordingResultWriter();
        var command = new CorpusBenchmarkCommand(
            new FixedManifestLoader(CreateManifest()),
            new RecordingPreflight([]),
            new RecordingModelResolver([]),
            new ThrowingSecondItemExecutor(),
            writer);

        await Assert.ThrowsAsync<IOException>(() =>
            command.RunAndWriteAsync(CreateOptions(), CancellationToken.None));

        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task RunAsync_ItemFailure_ReleasesTheVerifiedCachedModelLock()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), $"MirrorPowerAI-ModelLock-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(modelPath, [0x00]);
        var model = new ResolvedBenchmarkModel(
            modelPath,
            WhisperModelDescriptor.DefaultBase,
            TimeSpan.Zero,
            new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        var command = new CorpusBenchmarkCommand(
            new FixedManifestLoader(CreateManifest()),
            new RecordingPreflight([]),
            new FixedModelResolver(model),
            new ThrowingSecondItemExecutor(),
            new RecordingResultWriter());

        try
        {
            await Assert.ThrowsAsync<IOException>(() => command.RunAsync(CreateOptions(), CancellationToken.None));

            File.Delete(modelPath);
            Assert.False(File.Exists(modelPath));
        }
        finally
        {
            model.Dispose();
            if (File.Exists(modelPath))
            {
                File.Delete(modelPath);
            }
        }
    }

    [Fact]
    public async Task RunAndWriteAsync_PreflightFailure_DoesNotResolveModelExecuteOrWriteJson()
    {
        var events = new List<string>();
        var modelResolver = new RecordingModelResolver(events);
        var writer = new RecordingResultWriter();
        var command = new CorpusBenchmarkCommand(
            new FixedManifestLoader(CreateManifest()),
            new FailingPreflight(events),
            modelResolver,
            new SequenceExecutor(events, []),
            writer);

        await Assert.ThrowsAsync<IOException>(() =>
            command.RunAndWriteAsync(CreateOptions(), CancellationToken.None));

        Assert.Equal(["preflight"], events);
        Assert.False(modelResolver.SawRequireCachedModel);
        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task RunAsync_OutputMatchingManifest_FailsBeforePreflightOrModelResolution()
    {
        var events = new List<string>();
        var loader = new FixedManifestLoader(CreateManifest());
        var modelResolver = new RecordingModelResolver(events);
        var command = new CorpusBenchmarkCommand(
            loader,
            new RecordingPreflight(events),
            modelResolver,
            new SequenceExecutor(events, []),
            new RecordingResultWriter());
        var options = CreateOptions() with
        {
            OutputJsonPath = "private-manifest-path.json",
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            command.RunAsync(options, CancellationToken.None));

        Assert.True(loader.WasCalled);
        Assert.Empty(events);
        Assert.False(modelResolver.SawRequireCachedModel);
    }

    private static CorpusBenchmarkOptions CreateOptions(bool stable = false) =>
        new(
            "private-manifest-path.json",
            "private-result-path.json",
            "private-model-directory",
            BenchmarkModel.Base,
            "es",
            4,
            RequireCachedModel: stable,
            Stable: stable);

    private static CorpusManifest CreateManifest() =>
        new(
            "qa-spanish",
            "2026.08",
            "CC0-1.0",
            "https://example.test/dataset",
            new string('a', 64),
            "private-corpus-directory",
            [
                new CorpusManifestItem("private-item-one", "private-one.wav", new string('0', 64), "private-one.txt", new string('1', 64)),
                new CorpusManifestItem("private-item-two", "private-two.wav", new string('2', 64), "private-two.txt", new string('3', 64)),
            ]);

    private sealed class FixedManifestLoader(CorpusManifest manifest) : ICorpusManifestLoader
    {
        public bool WasCalled { get; private set; }

        public Task<CorpusManifest> LoadAsync(string manifestPath, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(manifest);
        }
    }

    private sealed class RecordingPreflight(List<string> events) : ICorpusAssetPreflight
    {
        public Task ValidateAsync(CorpusManifest manifest, CancellationToken cancellationToken)
        {
            events.Add("preflight");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingModelResolver(List<string> events) : IBenchmarkModelResolver
    {
        public bool SawRequireCachedModel { get; private set; }

        public bool SawStable { get; private set; }

        public Task<ResolvedBenchmarkModel> ResolveAsync(
            CorpusBenchmarkOptions options,
            CancellationToken cancellationToken)
        {
            events.Add("model");
            SawRequireCachedModel = options.RequireCachedModel;
            SawStable = options.Stable;
            return Task.FromResult(
                new ResolvedBenchmarkModel(
                    "private-model.bin",
                    WhisperModelDescriptor.DefaultBase,
                    TimeSpan.FromMilliseconds(10)));
        }
    }

    private sealed class FixedModelResolver(ResolvedBenchmarkModel model) : IBenchmarkModelResolver
    {
        public Task<ResolvedBenchmarkModel> ResolveAsync(
            CorpusBenchmarkOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(model);
        }
    }

    private sealed class FailingPreflight(List<string> events) : ICorpusAssetPreflight
    {
        public Task ValidateAsync(CorpusManifest manifest, CancellationToken cancellationToken)
        {
            events.Add("preflight");
            throw new IOException("asset integrity failure");
        }
    }

    private sealed class SequenceExecutor(
        List<string> events,
        IReadOnlyList<CorpusItemMetrics> metrics) : ICorpusItemExecutor
    {
        private int _index;

        public Task<CorpusItemMetrics> ExecuteAsync(
            CorpusManifest manifest,
            CorpusManifestItem item,
            ResolvedBenchmarkModel model,
            CorpusBenchmarkOptions options,
            CancellationToken cancellationToken)
        {
            events.Add("item");
            return Task.FromResult(metrics[_index++]);
        }
    }

    private sealed class ThrowingSecondItemExecutor : ICorpusItemExecutor
    {
        private int _index;

        public Task<CorpusItemMetrics> ExecuteAsync(
            CorpusManifest manifest,
            CorpusManifestItem item,
            ResolvedBenchmarkModel model,
            CorpusBenchmarkOptions options,
            CancellationToken cancellationToken)
        {
            if (_index++ == 0)
            {
                return Task.FromResult(new CorpusItemMetrics(TimeSpan.FromSeconds(1), TimeSpan.Zero, 0, 1));
            }

            throw new IOException("item failure");
        }
    }

    private sealed class RecordingResultWriter : ICorpusResultWriter
    {
        public int WriteCount { get; private set; }

        public Task WriteAsync(
            string outputJsonPath,
            CorpusBenchmarkResult result,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            return Task.CompletedTask;
        }
    }
}
