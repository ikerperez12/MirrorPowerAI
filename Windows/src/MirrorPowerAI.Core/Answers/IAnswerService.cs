namespace MirrorPowerAI.Core.Answers;

/// <summary>
/// Produces a concise answer for a transcribed question and optional project context.
/// </summary>
public interface IAnswerService
{
    /// <summary>
    /// Requests an answer for the supplied question.
    /// </summary>
    /// <param name="question">The transcribed question.</param>
    /// <param name="context">Optional, potentially sensitive project context that must never be logged.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>The answer as plain text.</returns>
    Task<string> AskAsync(
        string question,
        string? context,
        CancellationToken cancellationToken = default);
}
