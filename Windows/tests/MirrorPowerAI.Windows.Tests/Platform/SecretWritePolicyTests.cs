using MirrorPowerAI.Core.Security;
using MirrorPowerAI.Windows.Platform;

namespace MirrorPowerAI.Windows.Tests.Platform;

public sealed class SecretWritePolicyTests
{
    [Theory]
    [InlineData(true, "replacement", SecretWriteAction.Set)]
    [InlineData(false, "replacement", SecretWriteAction.Set)]
    [InlineData(true, "", SecretWriteAction.Delete)]
    [InlineData(true, "   ", SecretWriteAction.Delete)]
    [InlineData(false, "", SecretWriteAction.Preserve)]
    [InlineData(false, "   ", SecretWriteAction.Preserve)]
    public void Decide_UsesNonDestructiveActionAfterFailedRead(
        bool wasRead,
        string currentValue,
        SecretWriteAction expected)
    {
        // Act
        var action = SecretWritePolicy.Decide(wasRead, currentValue);

        // Assert
        Assert.Equal(expected, action);
    }

    [Fact]
    public async Task PersistAsync_FailedReadsAndEmptyFields_PreservesBothSecrets()
    {
        // Arrange
        var store = new RecordingSecretStore();

        // Act
        var apiKeyWasWritten = await SecretWritePolicy.PersistAsync(
            store,
            "gemini-api-key",
            string.Empty,
            wasRead: false);
        var contextWasWritten = await SecretWritePolicy.PersistAsync(
            store,
            "project-context",
            string.Empty,
            wasRead: false);

        // Assert
        Assert.False(apiKeyWasWritten);
        Assert.False(contextWasWritten);
        Assert.Empty(store.SetOperations);
        Assert.Empty(store.DeleteOperations);
    }

    [Fact]
    public async Task PersistAsync_SuccessfulReadAndEmptyField_DeletesSecret()
    {
        // Arrange
        var store = new RecordingSecretStore();

        // Act
        var wasWritten = await SecretWritePolicy.PersistAsync(
            store,
            "gemini-api-key",
            string.Empty,
            wasRead: true);

        // Assert
        Assert.True(wasWritten);
        Assert.Empty(store.SetOperations);
        Assert.Equal(["gemini-api-key"], store.DeleteOperations);
    }

    [Fact]
    public async Task PersistAsync_FailedReadAndReplacement_SetsReplacement()
    {
        // Arrange
        var store = new RecordingSecretStore();

        // Act
        var wasWritten = await SecretWritePolicy.PersistAsync(
            store,
            "gemini-api-key",
            "replacement",
            wasRead: false);

        // Assert
        Assert.True(wasWritten);
        Assert.Equal([("gemini-api-key", "replacement")], store.SetOperations);
        Assert.Empty(store.DeleteOperations);
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        public List<(string Name, string Value)> SetOperations { get; } = [];
        public List<string> DeleteOperations { get; } = [];

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task SetSecretAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            SetOperations.Add((name, value));
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            DeleteOperations.Add(name);
            return Task.CompletedTask;
        }
    }
}
