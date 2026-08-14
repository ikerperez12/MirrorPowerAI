using System.Globalization;
using System.Text;

namespace MirrorPowerAI.Benchmark;

internal readonly record struct WordErrorRateResult(
    int EditCount,
    int ReferenceWordCount,
    int HypothesisWordCount)
{
    public double Rate => EditCount / (double)ReferenceWordCount;
}

internal static class WordErrorRate
{
    public static WordErrorRateResult Calculate(string reference, string hypothesis)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(hypothesis);

        var referenceWords = Tokenize(reference);
        if (referenceWords.Length == 0)
        {
            throw new ArgumentException(
                "La referencia debe contener al menos una palabra tras normalizarla.",
                nameof(reference));
        }

        var hypothesisWords = Tokenize(hypothesis);
        var previous = new int[hypothesisWords.Length + 1];
        var current = new int[hypothesisWords.Length + 1];
        for (var column = 0; column < previous.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= referenceWords.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= hypothesisWords.Length; column++)
            {
                var substitutionCost = string.Equals(
                    referenceWords[row - 1],
                    hypothesisWords[column - 1],
                    StringComparison.Ordinal)
                    ? 0
                    : 1;
                current[column] = Math.Min(
                    Math.Min(
                        previous[column] + 1,
                        current[column - 1] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return new WordErrorRateResult(
            previous[hypothesisWords.Length],
            referenceWords.Length,
            hypothesisWords.Length);
    }

    internal static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        var needsSeparator = false;

        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (Rune.IsLetterOrDigit(rune))
            {
                if (needsSeparator && normalized.Length > 0)
                {
                    normalized.Append(' ');
                }

                normalized.Append(Rune.ToLowerInvariant(rune));
                needsSeparator = false;
            }
            else
            {
                needsSeparator = normalized.Length > 0;
            }
        }

        return normalized.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string[] Tokenize(string value) =>
        Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
