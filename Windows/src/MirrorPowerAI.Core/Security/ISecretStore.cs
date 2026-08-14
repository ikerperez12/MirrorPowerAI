namespace MirrorPowerAI.Core.Security;

/// <summary>
/// Stores and retrieves per-user secrets without exposing the storage mechanism.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Retrieves a secret.
    /// </summary>
    /// <param name="name">The stable, non-secret key name.</param>
    /// <param name="cancellationToken">A token used to cancel I/O.</param>
    /// <returns>The secret, or <see langword="null"/> when it does not exist.</returns>
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a secret for the current user.
    /// </summary>
    /// <param name="name">The stable, non-secret key name.</param>
    /// <param name="value">The secret value.</param>
    /// <param name="cancellationToken">A token used to cancel I/O.</param>
    /// <returns>A task that completes after the secret is safely persisted.</returns>
    Task SetSecretAsync(string name, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a secret if it exists.
    /// </summary>
    /// <param name="name">The stable, non-secret key name.</param>
    /// <param name="cancellationToken">A token used to cancel I/O.</param>
    /// <returns>A task that completes after deletion.</returns>
    Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default);
}
