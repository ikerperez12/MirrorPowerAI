namespace MirrorPowerAI.Core.Sessions;

/// <summary>
/// Applies a small local filter so continuous listening does not ask Gemini to answer every
/// sentence in a meeting.
/// </summary>
public static class ConversationQuestionDetector
{

    private static readonly string[] QuestionStarters =
    [
        "que ", "qué ", "como ", "cómo ", "cuando ", "cuándo ", "donde ", "dónde ",
        "quien ", "quién ", "cual ", "cuál ", "cuales ", "cuáles ", "cuanto ", "cuánto ",
        "por que ", "por qué ", "para que ", "para qué ", "puedes ", "podrias ", "podrías ",
        "deberia ", "debería ", "why ", "what ", "which ", "how ", "when ", "where ", "who ",
        "can you ", "could you ", "would you ", "is it ", "are you ", "do you ",
        "does it ", "should ", "will ", "have you ", "has it ", "podemos ",
    ];

    private static readonly string[] QuestionPhrases =
    [
        "es posible ", "me explicas ", "me puedes ", "puedes decirme ", "sabes si ",
        "sería posible ", "seria posible ", "tienes alguna ", "hay alguna ",
        "necesito saber ", "can you tell me ", "could you tell me ", "do you know ",
    ];

    private static readonly string[] ConversationalPrefixes =
    [
        "bueno ", "entonces ", "oye ", "perdona ", "disculpa ", "una pregunta ",
        "a ver ", "vale ", "well ", "so ", "excuse me ", "quick question ",
    ];

    /// <summary>
    /// Determines whether a transcript is likely to contain a direct question.
    /// </summary>
    /// <param name="transcript">The short transcript produced by the active provider.</param>
    /// <returns><see langword="true"/> when punctuation or a bounded question cue is present.</returns>
    public static bool IsLikelyQuestion(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        var normalized = transcript.Trim().ToLowerInvariant();
        if (normalized.Contains('?') || normalized.Contains('¿'))
        {
            return true;
        }

        // Whisper may omit punctuation. Restrict cue matching to the beginning or a short
        // polite phrase so ordinary statements such as "dijo que mañana" do not trigger Gemini.
        var firstWords = $"{RemoveConversationalPrefix(normalized)} ";
        return QuestionStarters.Any(firstWords.StartsWith) ||
               QuestionPhrases.Any(firstWords.Contains);
    }

    private static string RemoveConversationalPrefix(string value)
    {
        var remaining = value;
        for (var pass = 0; pass < 2; pass++)
        {
            var prefix = ConversationalPrefixes.FirstOrDefault(candidate =>
            {
                var cue = candidate.TrimEnd();
                return remaining.StartsWith(cue, StringComparison.Ordinal) &&
                       (remaining.Length == cue.Length ||
                        char.IsWhiteSpace(remaining[cue.Length]) ||
                        remaining[cue.Length] is ',' or ':' or '-' or ';');
            });
            if (prefix is null)
            {
                break;
            }

            remaining = remaining[prefix.TrimEnd().Length..].TrimStart(',', ':', '-', ';', ' ');
        }

        return remaining;
    }

    /// <summary>
    /// Determines whether a forced-duration segment must be held before it is treated as a
    /// complete turn. Whisper can add punctuation to a transcript even when the native segment
    /// cap cuts through a speaker turn, so every non-empty forced segment is joined to the next
    /// natural turn.
    /// </summary>
    /// <param name="transcript">The transcript produced for the segment.</param>
    /// <param name="forcedBoundary">Whether the segment was cut by the duration cap.</param>
    /// <returns><see langword="true"/> when the transcript should be held briefly for continuation.</returns>
    public static bool IsLikelyIncomplete(string transcript, bool forcedBoundary)
    {
        if (!forcedBoundary || string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        // A forced-duration boundary has no knowledge of conversational turn-taking. The
        // recognizer may invent '.', '?' or another terminal mark at the cut, so punctuation
        // cannot release the fragment. Only the next natural pause can close this turn.
        return true;
    }
}
