using System.Text.RegularExpressions;

namespace VoiceAssistant.Core;

/// <summary>
/// Editing grammar: erase or replace part of what was typed. While typing (no wake word) only unusual, explicit forms
/// are commands — “delete the last two words”, “delete the word very”, “replace the word Monday with Tuesday” — so
/// ordinary sentences are still typed. After the wake word, “replace X with Y” and “change X to Y” work too.
/// See docs/specs/voice-commands.md.
/// </summary>
public sealed partial class CommandParser
{
    private const int MaxEditCount = 50;

    private static readonly Dictionary<string, EditUnit> EditUnits = new(StringComparer.Ordinal)
    {
        ["word"] = EditUnit.Word, ["words"] = EditUnit.Word,
        ["sentence"] = EditUnit.Sentence, ["sentences"] = EditUnit.Sentence,
        ["line"] = EditUnit.Line, ["lines"] = EditUnit.Line,
        ["paragraph"] = EditUnit.Paragraph, ["paragraphs"] = EditUnit.Paragraph,
        ["character"] = EditUnit.Character, ["characters"] = EditUnit.Character,
        ["letter"] = EditUnit.Character, ["letters"] = EditUnit.Character,
    };

    private static VoiceCommand? MatchEdit(string n, bool lenient)
    {
        // "delete the last three words", "erase the previous sentence", "remove last line", "delete the last word"
        var m = LastUnitsRegex().Match(n);
        if (m.Success && ParseEditCount(m.Groups["count"].Value) is { } count && EditUnits.TryGetValue(m.Groups["unit"].Value, out var unit))
        {
            return new VoiceCommand(CommandKind.DeleteText) { Edit = new TextEdit(unit, count) };
        }

        // "delete three words", "delete 2 sentences" (a number is required, so "delete words" stays text)
        m = CountUnitsRegex().Match(n);
        if (m.Success && ParseEditCount(m.Groups["count"].Value) is { } n2 && EditUnits.TryGetValue(m.Groups["unit"].Value, out var unit2))
        {
            return new VoiceCommand(CommandKind.DeleteText) { Edit = new TextEdit(unit2, n2) };
        }

        // "delete that sentence", "remove this line"
        m = ThatUnitRegex().Match(n);
        if (m.Success && EditUnits.TryGetValue(m.Groups["unit"].Value, out var unit3))
        {
            return new VoiceCommand(CommandKind.DeleteText) { Edit = new TextEdit(unit3, 1) };
        }

        // "delete the word very", "remove the words as soon as possible"
        m = DeleteWordRegex().Match(n);
        if (m.Success)
        {
            return new VoiceCommand(CommandKind.DeleteText) { Edit = new TextEdit(Phrase: m.Groups["phrase"].Value) };
        }

        // "replace the word monday with tuesday" (always); "replace monday with tuesday", "change monday to tuesday"
        // (only after the wake word: "change the plan to friday" is a normal sentence)
        m = (lenient ? ReplaceRegex() : ReplaceWordRegex()).Match(n);
        if (m.Success)
        {
            return new VoiceCommand(CommandKind.ReplaceText, m.Groups["link"].Value)
            {
                Edit = new TextEdit(Phrase: m.Groups["phrase"].Value, Replacement: m.Groups["with"].Value),
            };
        }

        // After the wake word: "delete very", "erase the word" is too vague, but "delete monday" names a word.
        if (lenient)
        {
            m = DeletePhraseRegex().Match(n);
            var phrase = m.Groups["phrase"].Value;
            if (m.Success && !EditUnits.ContainsKey(phrase) && phrase is not ("that" or "this" or "it" or "everything" or "all")
                && !phrase.StartsWith("the word", StringComparison.Ordinal) && !phrase.StartsWith("the phrase", StringComparison.Ordinal))
            {
                return new VoiceCommand(CommandKind.DeleteText) { Edit = new TextEdit(Phrase: phrase) };
            }
        }

        return null;
    }

    /// <summary>
    /// Edits are matched before polite words are trimmed from the end: “delete see you again” must not lose “again”.
    /// Leading padding (“can you please …”) is still removed.
    /// </summary>
    private static IReadOnlyList<VoiceCommand>? WithUntrimmedEdit(IReadOnlyList<VoiceCommand>? commands, string normalized)
    {
        if (commands is not null and not [{ Kind: CommandKind.DeleteText or CommandKind.ReplaceText }])
        {
            return commands;
        }

        var text = normalized;
        bool changed;
        do
        {
            changed = false;
            foreach (var prefix in PolitePrefixes)
            {
                if (text.StartsWith(prefix + " ", StringComparison.Ordinal))
                {
                    text = text[(prefix.Length + 1)..].TrimStart();
                    changed = true;
                }
            }
        }
        while (changed && text.Length > 0);

        return MatchEdit(text, lenient: true) is { } untrimmed ? [untrimmed] : commands;
    }

    private static int? ParseEditCount(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return 1;
        }

        if (text is "few" or "a few")
        {
            return 3;
        }

        if (text is "couple of" or "a couple of" or "couple" or "a couple")
        {
            return 2;
        }

        var value = int.TryParse(text, out var digits) ? digits : NumberWords.GetValueOrDefault(text);
        return value is >= 1 and <= MaxEditCount ? value : null;
    }

    /// <summary>
    /// The replacement as it was said, with its capitals and punctuation (“with GitHub Actions.” → “GitHub Actions”).
    /// The normalised one is kept if the original can't be found.
    /// </summary>
    private static IReadOnlyList<VoiceCommand> WithSpokenReplacement(IReadOnlyList<VoiceCommand> commands, string cleaned)
    {
        if (commands is not [{ Kind: CommandKind.ReplaceText, Edit: { } edit, Argument: { } link } command])
        {
            return commands;
        }

        var m = Regex.Match(cleaned, $@"\b{Regex.Escape(link)}\b\s+(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.RightToLeft | RegexOptions.CultureInvariant);
        if (!m.Success)
        {
            return commands;
        }

        var spoken = m.Groups["rest"].Value.Trim().TrimEnd('.', '!', '?', ',').Trim();
        return TranscriptText.Normalize(spoken) == TranscriptText.Normalize(edit.Replacement ?? string.Empty)
            ? [command with { Edit = edit with { Replacement = spoken } }]
            : commands;
    }

    private const string EditVerb = "(?:delete|erase|remove|scratch|undo)";
    private const string Count = @"(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|a few|few|a couple of|couple of)";
    private const string Unit = "(?<unit>words?|sentences?|lines?|paragraphs?|characters?|letters?)";

    [GeneratedRegex($@"^{EditVerb} (?:the )?(?:last|previous|past) (?:{Count} )?{Unit}$")]
    private static partial Regex LastUnitsRegex();

    [GeneratedRegex($@"^(?:delete|erase|remove) {Count} {Unit}$")]
    private static partial Regex CountUnitsRegex();

    [GeneratedRegex($@"^{EditVerb} (?:that|this) {Unit}$")]
    private static partial Regex ThatUnitRegex();

    [GeneratedRegex(@"^(?:delete|erase|remove) the (?:word|words|phrase) (?<phrase>.+)$")]
    private static partial Regex DeleteWordRegex();

    [GeneratedRegex(@"^replace the (?:word|words|phrase) (?<phrase>.+?) (?<link>with) (?<with>.+)$")]
    private static partial Regex ReplaceWordRegex();

    [GeneratedRegex(@"^(?:replace|change)(?: the (?:word|words|phrase))? (?<phrase>.+?) (?<link>with|to|into|by) (?<with>.+)$")]
    private static partial Regex ReplaceRegex();

    [GeneratedRegex(@"^(?:delete|erase|remove) (?<phrase>.+)$")]
    private static partial Regex DeletePhraseRegex();
}
