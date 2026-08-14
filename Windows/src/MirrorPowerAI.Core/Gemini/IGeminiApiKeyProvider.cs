using MirrorPowerAI.Core.Security;

namespace MirrorPowerAI.Core.Gemini;

/// <summary>
/// Supplies the Gemini API key at request time.
/// </summary>
public interface IGeminiApiKeyProvider
{
    /// <summary>
    /// Retrieves the current Gemini API key.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel secret storage I/O.</param>
    /// <returns>The API key, or <see langword="null"/> when it is not configured.</returns>
    Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the Gemini key from the application's protected secret store.
/// </summary>
public sealed class SecretStoreGeminiApiKeyProvider : IGeminiApiKeyProvider
{
    /// <summary>
    /// Gets the stable secret name used for the Gemini key.
    /// </summary>
    public const string SecretName = "gemini-api-key";

    private readonly ISecretStore _secretStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretStoreGeminiApiKeyProvider"/> class.
    /// </summary>
    /// <param name="secretStore">The protected per-user secret store.</param>
    public SecretStoreGeminiApiKeyProvider(ISecretStore secretStore)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    /// <inheritdoc />
    public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default) =>
        _secretStore.GetSecretAsync(SecretName, cancellationToken);
}
