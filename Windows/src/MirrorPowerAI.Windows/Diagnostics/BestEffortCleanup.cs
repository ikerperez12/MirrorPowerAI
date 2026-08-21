namespace MirrorPowerAI.Windows.Diagnostics;

/// <summary>
/// Executes independent shutdown actions without allowing one failed native or managed resource
/// to prevent the remaining resources from being released.
/// </summary>
internal static class BestEffortCleanup
{
    /// <summary>
    /// Runs every supplied action and deliberately suppresses individual cleanup failures.
    /// </summary>
    /// <param name="actions">Independent cleanup actions, in their required release order.</param>
    /// <remarks>
    /// Callers must not include diagnostics, secrets, question text, answers, or exception details in
    /// the actions. Shutdown has no safe recovery path, so retaining the first exception would only
    /// risk leaking sensitive state while leaving native resources behind.
    /// </remarks>
    internal static void Run(params Action?[] actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        foreach (var action in actions)
        {
            if (action is null)
            {
                continue;
            }

            try
            {
                action();
            }
            catch (Exception)
            {
                // Shutdown must continue. Individual resources are intentionally best-effort.
            }
        }
    }
}
