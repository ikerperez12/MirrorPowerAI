using System.Security.Cryptography;
using System.Text;
using MirrorPowerAI.Windows.Platform;

namespace MirrorPowerAI.Windows.Tests.Platform;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "MirrorPowerAI.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SetAndGetSecretAsync_RoundTripsForCurrentUserWithoutPlaintextOnDisk()
    {
        // Arrange
        const string secretName = "gemini-api-key";
        const string secret = "very-sensitive-test-value-123";
        var store = new DpapiSecretStore(_testDirectory);

        // Act
        await store.SetSecretAsync(secretName, secret);
        var loaded = await store.GetSecretAsync(secretName);
        var encrypted = await File.ReadAllBytesAsync(
            Path.Combine(_testDirectory, $"{secretName}.bin"));

        // Assert
        Assert.Equal(secret, loaded);
        Assert.DoesNotContain(
            secret,
            Encoding.UTF8.GetString(encrypted),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSecretAsync_TamperedCiphertext_ThrowsCryptographicException()
    {
        // Arrange
        const string secretName = "context";
        var store = new DpapiSecretStore(_testDirectory);
        await store.SetSecretAsync(secretName, "protected project context");
        var path = Path.Combine(_testDirectory, $"{secretName}.bin");
        var encrypted = await File.ReadAllBytesAsync(path);
        encrypted[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(path, encrypted);

        // Act and assert
        _ = await Assert.ThrowsAsync<CryptographicException>(
            () => store.GetSecretAsync(secretName));
    }

    [Fact]
    public async Task DeleteSecretAsync_ExistingSecret_RemovesItIdempotently()
    {
        // Arrange
        var store = new DpapiSecretStore(_testDirectory);
        await store.SetSecretAsync("api", "secret");

        // Act
        await store.DeleteSecretAsync("api");
        await store.DeleteSecretAsync("api");

        // Assert
        Assert.Null(await store.GetSecretAsync("api"));
    }

    [Fact]
    public async Task SetSecretAsync_PlaintextOver128KiB_RejectsBeforeProtectionOrFileCreation()
    {
        // Arrange
        const string secretName = "oversized";
        var store = new DpapiSecretStore(_testDirectory);
        var oversized = new string('x', (128 * 1024) + 1);

        // Act
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.SetSecretAsync(secretName, oversized));

        // Assert
        Assert.False(File.Exists(Path.Combine(_testDirectory, $"{secretName}.bin")));
    }

    [Fact]
    public async Task GetSecretAsync_CiphertextOver256KiB_RejectsAtLengthGate()
    {
        // Arrange
        const string secretName = "oversized-ciphertext";
        Directory.CreateDirectory(_testDirectory);
        var path = Path.Combine(_testDirectory, $"{secretName}.bin");
        await File.WriteAllBytesAsync(path, new byte[(256 * 1024) + 1]);
        var store = new DpapiSecretStore(_testDirectory);

        // Act and assert
        var exception = await Assert.ThrowsAsync<CryptographicException>(
            () => store.GetSecretAsync(secretName));
        Assert.Contains("safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("folder/secret")]
    [InlineData("folder\\secret")]
    [InlineData("secreto-ñ")]
    public async Task SecretOperations_InvalidName_RejectPathTraversalAndUnicode(string invalidName)
    {
        // Arrange
        var store = new DpapiSecretStore(_testDirectory);

        // Act and assert
        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SetSecretAsync(invalidName, "secret"));
        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => store.GetSecretAsync(invalidName));
        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => store.DeleteSecretAsync(invalidName));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
