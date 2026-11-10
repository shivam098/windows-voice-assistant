using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Core;

public enum EditUnit
{
    Character,
    Word,
    Sentence,
    Line,
    Paragraph,
}

/// <summary>
/// A spoken edit: “delete the last 3 words” (Unit + Count), “delete the word very” (Phrase), or “replace Monday with
/// Tuesday” (Phrase + Replacement). Phrases are normalised (lower case, no punctuation); Replacement keeps what was said.
/// </summary>
public sealed record TextEdit(EditUnit Unit = EditUnit.Word, int Count = 1, string? Phrase = null, string? Replacement = null);

/// <summary>
/// The keystrokes for an edit, worked out from the text before the cursor: move left past <see cref="KeepAfter"/> text
/// elements, select <see cref="Delete"/> elements to the left (Shift+Left), then delete them or type <see cref="Insert"/>
/// over them, then move right again. Selecting with the arrow keys keeps movement and deletion in the same units the app
/// uses (Backspace alone deletes one code point in some scripts).
/// </summary>
public sealed record EditPlan(int KeepAfter, int Delete, string Insert, string TextAfterEdit)
{
    /// <summary>The (normalised) text that was removed, for “Deleted …” messages.</summary>
    public string Removed { get; init; } = string.Empty;
}

/// <summary>
/// Turns spoken edits into exact keystrokes. Works on the text before the cursor (newlines normalised to "\n"), counts
/// in text elements so emoji and accents are one Backspace each, and never looks further back than it was given.
/// The same rules apply on every platform; only reading the text and pressing keys are platform-specific.
/// </summary>
public static partial class TextEditing
{
    /// <summary>The furthest an edit may reach back, in text elements: arrow-key edits further than this are refused.</summary>
    public const int MaxReach = 3000;

    public static string NormalizeNewLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>Deletes the last <see cref="TextEdit.Count"/> units before the cursor. Null if there is nothing to delete.</summary>
    public static EditPlan? DeleteLast(string before, EditUnit unit, int count)
    {
        before = NormalizeNewLines(before);
        var end = before.Length;
        var cut = end;
        for (var i = 0; i < count && cut > 0; i++)
        {
            cut = unit switch
            {
                EditUnit.Character => PreviousTextElement(before, cut),
                EditUnit.Word => PreviousWordStart(before, cut),
                EditUnit.Sentence => PreviousSentenceStart(before, cut),
                EditUnit.Line => PreviousLineStart(before, cut),
                _ => PreviousParagraphStart(before, cut),
            };
        }

        if (unit != EditUnit.Character)
        {
            // Take the spaces before the deleted text too, so the cursor sits right after the previous word.
            while (cut > 0 && before[cut - 1] is ' ' or '\t')
            {
                cut--;
            }
        }

        if (cut >= end || (unit is EditUnit.Word or EditUnit.Sentence && before[cut..].Trim().Length == 0))
        {
            return null;
        }

        var removed = before[cut..];
        if (Elements(removed) > MaxReach)
        {
            return null;
        }

        return new EditPlan(0, Elements(removed), string.Empty, before[..cut]) { Removed = removed.Trim() };
    }

    /// <summary>
    /// Finds the last whole-word occurrence of <paramref name="phrase"/> before the cursor and removes it (with one
    /// neighbouring space) or replaces it. Null if it isn't there or is too far back.
    /// </summary>
    public static EditPlan? ReplaceLast(string before, string phrase, string? replacement)
    {
        before = NormalizeNewLines(before);
        var match = FindLast(before, phrase);
        if (match is null)
        {
            return null;
        }

        var (start, length) = match.Value;
        var found = before.Substring(start, length);
        var end = start + length;
        string insert;
        if (string.IsNullOrEmpty(replacement))
        {
            insert = string.Empty;

            // "very good" → "good": drop the space before (or, at the start of a line, the one after).
            if (start > 0 && before[start - 1] == ' ')
            {
                start--;
            }
            else if (end < before.Length && before[end] == ' ')
            {
                end++;
            }
        }
        else
        {
            insert = MatchCase(found, replacement.Trim());
        }

        var keepAfter = Elements(before[end..]);
        if (keepAfter + Elements(before[start..end]) > MaxReach)
        {
            return null;
        }

        var after = before[..start] + insert + before[end..];
        return new EditPlan(keepAfter, Elements(before[start..end]), insert, after) { Removed = found };
    }

    /// <summary>Start and length of the last whole-word, case-insensitive match of a normalised phrase.</summary>
    public static (int Start, int Length)? FindLast(string text, string phrase)
    {
        var words = TranscriptText.Normalize(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return null;
        }

        // Each word may carry the apostrophes and hyphens that normalising removed ("dont" finds "don't"), and words
        // may be separated by spaces, hyphens or punctuation.
        var pattern = string.Join(@"[\s\p{P}]+", words.Select(w => string.Join(@"['’\-]?", w.Select(c => Regex.Escape(c.ToString())))));
        var regex = new Regex($@"(?<![\p{{L}}\p{{N}}]){pattern}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.RightToLeft, TimeSpan.FromMilliseconds(200));
        var m = regex.Match(text);
        return m.Success ? (m.Index, m.Length) : null;
    }

    /// <summary>Keeps a capital at the start of a sentence: replacing “Monday” with “tuesday” types “Tuesday”.</summary>
    private static string MatchCase(string found, string replacement)
    {
        if (replacement.Length == 0 || found.Length == 0)
        {
            return replacement;
        }

        if (found.All(c => !char.IsLetter(c) || char.IsUpper(c)) && found.Count(char.IsLetter) > 1)
        {
            return replacement.ToUpperInvariant();
        }

        return char.IsUpper(found[0]) && char.IsLower(replacement[0])
            ? char.ToUpperInvariant(replacement[0]) + replacement[1..]
            : replacement;
    }

    private static int Elements(string text) => new StringInfo(text).LengthInTextElements;

    private static int PreviousTextElement(string text, int end)
    {
        var starts = StringInfo.ParseCombiningCharacters(text[..end]);
        return starts.Length == 0 ? 0 : starts[^1];
    }

    /// <summary>A word is a run of non-space characters, with the punctuation attached to it (“world.”).</summary>
    private static int PreviousWordStart(string text, int end)
    {
        var i = end;
        while (i > 0 && char.IsWhiteSpace(text[i - 1]))
        {
            i--;
        }

        while (i > 0 && !char.IsWhiteSpace(text[i - 1]))
        {
            i--;
        }

        return i;
    }

    /// <summary>Back to just after the previous “. ”, “? ”, “! ” or line break.</summary>
    private static int PreviousSentenceStart(string text, int end)
    {
        var i = end;
        while (i > 0 && char.IsWhiteSpace(text[i - 1]))
        {
            i--;
        }

        // Skip this sentence's own closing punctuation.
        while (i > 0 && text[i - 1] is '.' or '!' or '?' or '"' or '”' or ')')
        {
            i--;
        }

        while (i > 0)
        {
            var c = text[i - 1];
            if (c == '\n' || (c is '.' or '!' or '?' && i < text.Length && char.IsWhiteSpace(text[i])))
            {
                break;
            }

            i--;
        }

        return i;
    }

    /// <summary>The whole last line, including the line break before it (so the cursor ends on the line above).</summary>
    private static int PreviousLineStart(string text, int end)
    {
        var i = end;
        while (i > 0 && text[i - 1] is ' ' or '\t')
        {
            i--;
        }

        if (i > 0 && text[i - 1] == '\n')
        {
            // The cursor is on an empty line: that line is the one to delete.
            return i - 1;
        }

        if (i == 0)
        {
            return 0;
        }

        var lineBreak = text.LastIndexOf('\n', i - 1);
        return lineBreak < 0 ? 0 : lineBreak;
    }

    /// <summary>
    /// Back to the previous blank line or, in editors that separate paragraphs with a single line break (Word, most
    /// chat and mail apps), the previous line break.
    /// </summary>
    private static int PreviousParagraphStart(string text, int end)
    {
        var i = end;
        while (i > 0 && char.IsWhiteSpace(text[i - 1]))
        {
            i--;
        }

        if (i == 0)
        {
            return 0;
        }

        var lineBreak = text.LastIndexOf('\n', i - 1);
        if (lineBreak < 0)
        {
            return 0;
        }

        // Take the blank lines that separated it from the paragraph before.
        while (lineBreak > 0 && text[lineBreak - 1] == '\n')
        {
            lineBreak--;
        }

        return lineBreak;
    }
}
