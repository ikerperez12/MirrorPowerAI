using MirrorPowerAI.Core.Security;

namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Selects a fail-closed secret write action from the result of the last read and the current UI value.
/// </summary>
public static class SecretWritePolicy
{
    /// <summary>
    /// Determines whether a secret should be set, deleted, or left unchanged.
    /// </summary>
    /// <param name="wasRead">Whether the existing secret was read successfully before editing.</param>
    /// <param name="currentValue">The current value entered in the settings UI.</param>
    /// <returns>A non-destructive action when the prior read failed and no replacement was provided.</returns>
    public static SecretWriteAction Decide(bool wasRead, string? currentValue)
    {
        if (!string.IsNullOrWhiteSpace(currentValue))
        {
            return SecretWriteAction.Set;
        }

        return wasRead ? SecretWriteAction.Delete : SecretWriteAction.Preserve;
    }

    /// <summary>
    /// Persists a secret only when the requested action can be proven non-destructive.
    /// </summary>
    /// <param name="secretStore">The protected secret store to mutate.</param>
    /// <param name="name">The stable, non-secret secret-store key.</param>
    /// <param name="currentValue">The value currently supplied by the user.</param>
    /// <param name="wasRead">Whether the existing value was read successfully before editing.</param>
    /// <param name="cancellationToken">A token used to cancel protected storage I/O.</param>
    /// <returns>
    /// <see langword="true"/> when the store was successfully set or deleted; otherwise
    /// <see langword="false"/> when the previous value was deliberately preserved.
    /// </returns>
    public static async Task<bool> PersistAsync(
        ISecretStore secretStore,
        string name,
        string? currentValue,
        bool wasRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        switch (Decide(wasRead, currentValue))
        {
            case SecretWriteAction.Set:
                await secretStore.SetSecretAsync(name, currentValue!, cancellationToken).ConfigureAwait(false);
                return true;
            case SecretWriteAction.Delete:
                await secretStore.DeleteSecretAsync(name, cancellationToken).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Represents the only secret mutations allowed by the settings save flow.</summary>
public enum SecretWriteAction
{
    /// <summary>Leaves the existing secret untouched because it could not be read safely.</summary>
    Preserve,

    /// <summary>Stores the non-empty value supplied by the user.</summary>
    Set,

    /// <summary>Removes a secret that was successfully read and deliberately left empty.</summary>
    Delete,
}
