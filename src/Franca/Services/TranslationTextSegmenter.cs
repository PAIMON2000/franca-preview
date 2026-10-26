using System.Globalization;
using System.Text.RegularExpressions;

namespace Franca.Services;

public static class TranslationTextSegmenter
{
    public const int MaxTextLength = 2500;
    public const int MaxSegmentLength = 200;
    public sealed record Part(string Text, bool NeedsTranslation);

    // Formatting and substitution tokens are copied verbatim, never sent in incomplete form.
    private static readonly Regex Literals = new(
        @"\r\n|[\r\n]|</>|</?[A-Za-z][^>\r\n]*>|<\#[0-9A-Fa-f]{6,8}>|\{[^{}\r\n]+\}|%(?:\d+\$)?[-+0 #]*\d*(?:\.\d+)?[sdifFeEgGuxXoc]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool HasSameProtectedTokens(string source, string target) =>
        Literals.Matches(source).Select(m => m.Value).SequenceEqual(Literals.Matches(target).Select(m => m.Value), StringComparer.Ordinal);

    public static IReadOnlyList<Part> Split(string text)
    {
        if (text.Length > MaxTextLength)
            throw new ArgumentOutOfRangeException(nameof(text), "Game text exceeds the existing 2500-character limit.");
        var result = new List<Part>();
        var offset = 0;
        foreach (Match match in Literals.Matches(text))
        {
            SplitRun(text[offset..match.Index], result);
            AddLiteral(result, match.Value);
            offset = match.Index + match.Length;
        }
        SplitRun(text[offset..], result);
        return result;
    }

    private static void SplitRun(string text, List<Part> result)
    {
        while (text.Length > 0)
        {
            var length = Math.Min(text.Length, MaxSegmentLength);
            if (length < text.Length)
            {
                // A UTF-16 boundary may bisect a surrogate pair or combining-character sequence.
                length = StringInfo.ParseCombiningCharacters(text).Last(i => i <= length);
                if (length == 0)
                {
                    var next = StringInfo.GetNextTextElement(text);
                    AddLiteral(result, next);
                    text = text[next.Length..];
                    continue;
                }
                var sentence = LastBoundary(text, length, sentence: true);
                var punctuation = sentence == 0 ? LastBoundary(text, length, sentence: false) : 0;
                var boundary = sentence > 0 ? sentence : punctuation;
                if (boundary == 0)
                {
                    for (var i = length - 1; i > 0; i--)
                        if (char.IsWhiteSpace(text[i])) { boundary = i + 1; break; }
                }
                if (boundary > 0) length = boundary;
            }
            var piece = text[..length];
            var trimmed = piece.Trim();
            if (trimmed.Length == 0 || !trimmed.Any(char.IsLetterOrDigit))
                AddLiteral(result, piece);
            else
            {
                var first = piece.IndexOf(trimmed, StringComparison.Ordinal);
                AddLiteral(result, piece[..first]);
                result.Add(new Part(trimmed, true));
                AddLiteral(result, piece[(first + trimmed.Length)..]);
            }
            text = text[length..];
        }
    }

    private static int LastBoundary(string text, int limit, bool sentence)
    {
        for (var i = limit - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (!(sentence ? ".!?。！？…" : ",;:，；：、").Contains(ch)) continue;
            if (sentence && ch == '.')
            {
                if (i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1])) continue;
                if (i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1])) continue;
                var start = i - 1;
                while (start >= 0 && char.IsLetter(text[start])) start--;
                var word = text[(start + 1)..i].ToLowerInvariant();
                if (word is "mr" or "mrs" or "ms" or "dr" or "prof" or "sr" or "jr" or "vs" || word.Length == 1) continue;
            }
            var end = i + 1;
            while (end < limit && "\"'”’）)]}".Contains(text[end])) end++;
            return end;
        }
        return 0;
    }

    private static void AddLiteral(List<Part> parts, string text)
    {
        if (text.Length == 0) return;
        if (parts.Count > 0 && !parts[^1].NeedsTranslation)
            parts[^1] = new Part(parts[^1].Text + text, false);
        else
            parts.Add(new Part(text, false));
    }
}
