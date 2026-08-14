using System.Net;
using System.Security.Cryptography;
using MirrorPowerAI.Core.Models;

namespace MirrorPowerAI.Core.Tests;

public sealed class WhisperModelManagerTests
{
    [Fact]
    public void DefaultBase_Always_UsesPinnedRevisionSizeAndHash()
    {
        var descriptor = WhisperModelDescriptor.DefaultBase;

        Assert.Contains(
            "5359861c739e955e79d9a303bcbc70fb988958b1",
            descriptor.DownloadUri.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.Equal(147_951_465, descriptor.ExpectedSize);
        Assert.Equal(
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
            descriptor.Sha256);
    }

    [Fact]
    public async Task EnsureAvailableAsync_ValidExistingModel_ReusesWithoutNetwork()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var descriptor = CreateDescriptor(bytes);
        using var directory = new TemporaryDirectory();
        var target = System.IO.Path.Combine(directory.Path, descriptor.FileName);
        await File.WriteAllBytesAsync(target, bytes);
        using var handler = RecordingHttpMessageHandler.Json("unreachable");
        using var httpClient = new HttpClient(handler);
        using var manager = new WhisperModelManager(httpClient, descriptor);

        var result = await manager.EnsureAvailableAsync(directory.Path);

        Assert.Equal(target, result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task EnsureAvailableAsync_ValidDownload_AtomicallyActivatesAndRemovesTemporaryFile()
    {
        var bytes = new byte[] { 9, 8, 7, 6, 5 };
        var descriptor = CreateDescriptor(bytes);
        using var directory = new TemporaryDirectory();
        using var handler = BytesHandler(bytes);
        using var httpClient = new HttpClient(handler);
        using var manager = new WhisperModelManager(httpClient, descriptor);

        var result = await manager.EnsureAvailableAsync(directory.Path);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(result));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.download"));
        Assert.True(await manager.IsValidAsync(result));
    }

    [Fact]
    public async Task EnsureAvailableAsync_InvalidExistingModel_ReplacesOnlyAfterValidDownload()
    {
        var bytes = new byte[] { 1, 3, 3, 7 };
        var descriptor = CreateDescriptor(bytes);
        using var directory = new TemporaryDirectory();
        var target = System.IO.Path.Combine(directory.Path, descriptor.FileName);
        await File.WriteAllBytesAsync(target, new byte[] { 0, 0, 0, 0 });
        using var handler = BytesHandler(bytes);
        using var httpClient = new HttpClient(handler);
        using var manager = new WhisperModelManager(httpClient, descriptor);

        _ = await manager.EnsureAvailableAsync(directory.Path);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task EnsureAvailableAsync_HashMismatch_LeavesNoActiveOrPartialModel()
    {
        var expected = new byte[] { 1, 2, 3, 4 };
        var downloaded = new byte[] { 4, 3, 2, 1 };
        var descriptor = CreateDescriptor(expected);
        using var directory = new TemporaryDirectory();
        using var handler = BytesHandler(downloaded);
        using var httpClient = new HttpClient(handler);
        using var manager = new WhisperModelManager(httpClient, descriptor);

        var exception = await Assert.ThrowsAsync<WhisperModelException>(() =>
            manager.EnsureAvailableAsync(directory.Path));

        Assert.Equal(WhisperModelErrorKind.HashMismatch, exception.Kind);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task EnsureAvailableAsync_IncompleteDownload_FailsSizeGateAndCleansPartial()
    {
        var expected = new byte[] { 1, 2, 3, 4 };
        var descriptor = CreateDescriptor(expected);
        using var directory = new TemporaryDirectory();
        using var handler = BytesHandler([1, 2]);
        using var httpClient = new HttpClient(handler);
        using var manager = new WhisperModelManager(httpClient, descriptor);

        var exception = await Assert.ThrowsAsync<WhisperModelException>(() =>
            manager.EnsureAvailableAsync(directory.Path));

        Assert.Equal(WhisperModelErrorKind.SizeMismatch, exception.Kind);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task EnsureAvailableAsync_CancelledDownload_CleansPartialFile()
    {
        var expected = new byte[] { 1, 2, 3, 4 };
        var descriptor = CreateDescriptor(expected);
        using var directory = new TemporaryDirectory();
        using var stream = new BlockingAfterPrefixStream([1, 2]);
        using var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            }));
        using var httpClient = new HttpClient(handler);
        using var manager = new WhisperModelManager(httpClient, descriptor);
        using var cancellation = new CancellationTokenSource();

        var operation = manager.EnsureAvailableAsync(directory.Path, cancellation.Token);
        await stream.FirstRead.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task EnsureAvailableAsync_ConcurrentCallers_DownloadExactlyOnce()
    {
        var bytes = new byte[] { 5, 4, 3, 2, 1 };
        var descriptor = CreateDescriptor(bytes);
        using var directory = new TemporaryDirectory();
        using var handler = BytesHandler(bytes);
        using var httpClient = new HttpClient(handler);
        using var manager = new WhisperModelManager(httpClient, descriptor);

        var results = await Task.WhenAll(
            manager.EnsureAvailableAsync(directory.Path),
            manager.EnsureAvailableAsync(directory.Path));

        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task EnsureAvailableAsync_HttpError_ReturnsTypedDownloadFailure()
    {
        var descriptor = CreateDescriptor([1]);
        using var directory = new TemporaryDirectory();
        using var handler = RecordingHttpMessageHandler.Json("sensitive", HttpStatusCode.NotFound);
        using var httpClient = new HttpClient(handler);
        using var manager = new WhisperModelManager(httpClient, descriptor);

        var exception = await Assert.ThrowsAsync<WhisperModelException>(() =>
            manager.EnsureAvailableAsync(directory.Path));

        Assert.Equal(WhisperModelErrorKind.DownloadFailed, exception.Kind);
        Assert.DoesNotContain("sensitive", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../model.bin", "https://example.test/model.bin")]
    [InlineData("model.bin", "http://example.test/model.bin")]
    public void Constructor_UnsafeDescriptor_RejectsBeforeNetwork(string fileName, string uri)
    {
        var descriptor = new WhisperModelDescriptor(
            fileName,
            new Uri(uri),
            1,
            new string('0', 64));
        using var httpClient = new HttpClient();

        Assert.Throws<ArgumentException>(() => new WhisperModelManager(httpClient, descriptor));
    }

    private static WhisperModelDescriptor CreateDescriptor(byte[] expectedBytes) =>
        new(
            "model.bin",
            new Uri("https://example.test/model.bin"),
            expectedBytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(expectedBytes)));

    private static RecordingHttpMessageHandler BytesHandler(byte[] bytes) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        }));

    private sealed class BlockingAfterPrefixStream(byte[] prefix) : Stream
    {
        private bool _prefixReturned;

        public Task FirstRead => FirstReadSource.Task;

        private TaskCompletionSource<bool> FirstReadSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_prefixReturned)
            {
                _prefixReturned = true;
                prefix.CopyTo(buffer);
                FirstReadSource.SetResult(true);
                return prefix.Length;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
