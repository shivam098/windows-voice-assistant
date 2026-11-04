namespace VoiceAssistant.Core;

/// <summary>
/// Navigation grammar: tabs, clicking by name, “start typing in X”, numbers, and yes/no. Kept separate from the core
/// command grammar so it reads as one specification (see docs/specs/navigation.md).
/// </summary>
public sealed partial class CommandParser
{
    private static readonly Dictionary<string, int> OrdinalWords = new(StringComparer.Ordinal)
    {
        ["first"] = 1, ["1st"] = 1, ["second"] = 2, ["2nd"] = 2, ["third"] = 3, ["3rd"] = 3, ["fourth"] = 4, ["4th"] = 4,
        ["fifth"] = 5, ["5th"] = 5, ["sixth"] = 6, ["6th"] = 6, ["seventh"] = 7, ["7th"] = 7, ["eighth"] = 8, ["8th"] = 8,
        ["ninth"] = 9, ["9th"] = 9, ["tenth"] = 10, ["10th"] = 10, ["last"] = -1,
    };

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
        ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20,
    };

    private static readonly HashSet<string> ShowNumbersPhrases =
        ["show numbers", "show the numbers", "show labels", "show the labels", "number everything", "label everything", "show me the numbers", "numbers"];

    private static readonly HashSet<string> HideNumbersPhrases =
        ["hide numbers", "hide the numbers", "hide labels", "hide the labels", "clear numbers", "remove numbers", "no numbers"];

    private static readonly HashSet<string> ConfirmPhrases = ["yes", "yes do it", "yes please", "confirm", "do it", "go ahead", "yeah", "yep", "sure"];

    private static readonly HashSet<string> CancelPhrases = ["no", "nope", "cancel", "never mind", "nevermind", "dont", "dont do it", "cancel that", "forget it"];

    private static readonly string[] TabVerbs =
        ["go to", "go back to", "switch to", "switch over to", "open", "select", "show me", "show", "jump to", "take me to", "move to", "bring up", "focus"];

    private static readonly string[] ClickVerbs =
        ["click on", "click", "tap on", "tap", "press on", "press", "hit", "select", "choose", "pick", "expand", "collapse", "toggle"];

    private static readonly string[] TypingVerbs =
        ["start typing", "begin typing", "continue typing", "start writing", "start dictating", "let me type", "type", "write", "dictate"];

    private static readonly (string Phrase, ScreenRegion Region)[] RegionSuffixes =
    [
        ("on the left side", ScreenRegion.Left), ("on the left hand side", ScreenRegion.Left), ("in the left pane", ScreenRegion.Left),
        ("in the left panel", ScreenRegion.Left), ("in the sidebar", ScreenRegion.Left), ("in the side bar", ScreenRegion.Left),
        ("on the sidebar", ScreenRegion.Left), ("on the left", ScreenRegion.Left), ("at the left", ScreenRegion.Left), ("to the left", ScreenRegion.Left),
        ("on the right side", ScreenRegion.Right), ("on the right hand side", ScreenRegion.Right), ("in the right pane", ScreenRegion.Right),
        ("in the right panel", ScreenRegion.Right), ("on the right", ScreenRegion.Right), ("at the right", ScreenRegion.Right),
        ("at the top", ScreenRegion.Top), ("on the top", ScreenRegion.Top), ("on top", ScreenRegion.Top), ("at the bottom", ScreenRegion.Bottom),
        ("on the bottom", ScreenRegion.Bottom),
    ];

    private static readonly string[] ElementNoise = ["button", "link", "option", "menu item", "menu", "item", "icon", "entry"];

    private static readonly string[] FieldSuffixes = ["text box", "box", "field", "bar", "area", "input"];

    /// <summary>
    /// Parses a spoken description of something on screen: “the second Issues on the left” → Name “issues”, Ordinal 2,
    /// Region Left. Used by the grammar and by the “go to X” fallback.
    /// </summary>
    public static ElementTarget ParseTarget(string phrase)
    {
        var text = phrase.Trim();
        ScreenRegion? region = null;
        foreach (var (suffix, r) in RegionSuffixes)
        {
            if (text.EndsWith(" " + suffix, StringComparison.Ordinal) || text == suffix)
            {
                text = text[..^suffix.Length].Trim();
                region = r;
                break;
            }
        }

        text = StripArticles(text);
        int? ordinal = null;
        var space = text.IndexOf(' ');
        if (space > 0 && OrdinalWords.TryGetValue(text[..space], out var o))
        {
            ordinal = o;
            text = text[(space + 1)..].Trim();
        }
        else if (OrdinalWords.TryGetValue(text, out var only))
        {
            ordinal = only;
            text = string.Empty;
        }

        foreach (var noise in ElementNoise)
        {
            if (text == noise || text == "one")
            {
                text = string.Empty;
                break;
            }

            if (text.EndsWith(" " + noise, StringComparison.Ordinal))
            {
                text = text[..^(noise.Length + 1)].Trim();
                break;
            }
        }

        return new ElementTarget(Simplify(StripArticles(text)), region, ordinal);
    }

    private static string StripArticles(string text)
    {
        foreach (var article in new[] { "on the ", "the ", "a ", "an ", "my " })
        {
            if (text.StartsWith(article, StringComparison.Ordinal))
            {
                return text[article.Length..].Trim();
            }
        }

        return text;
    }

    private static IReadOnlyList<VoiceCommand>? MatchNavigation(string n)
    {
        if (ShowNumbersPhrases.Contains(n)) return [new VoiceCommand(CommandKind.ShowNumbers)];
        if (HideNumbersPhrases.Contains(n)) return [new VoiceCommand(CommandKind.HideNumbers)];
        if (ConfirmPhrases.Contains(n)) return [new VoiceCommand(CommandKind.Confirm)];
        if (CancelPhrases.Contains(n)) return [new VoiceCommand(CommandKind.Cancel)];
        if (MatchChoice(n) is { } choice) return [new VoiceCommand(CommandKind.Choose, choice.ToString(System.Globalization.CultureInfo.InvariantCulture))];

        foreach (var suffix in ChainedStartTyping)
        {
            if (n.EndsWith(suffix, StringComparison.Ordinal) && MatchTabOrClick(n[..^suffix.Length].Trim()) is { } first)
            {
                return [first, new VoiceCommand(CommandKind.StartTyping)];
            }
        }

        return MatchStartTypingIn(n) ?? (MatchTabOrClick(n) is { } single ? [single] : null);
    }

    private static VoiceCommand? MatchTabOrClick(string n) => MatchTab(n) ?? MatchClick(n);

    /// <summary>“click 3”, “number three”, “choose two”, “3”, “the second one”.</summary>
    private static int? MatchChoice(string n)
    {
        var text = n;
        foreach (var prefix in new[] { "click on number ", "click number ", "click on ", "click ", "choose number ", "choose ", "pick number ", "pick ", "select number ", "select ", "number ", "option ", "go with ", "the " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }

        if (text.StartsWith("the ", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var digits))
        {
            return digits is > 0 and < 100 ? digits : null;
        }

        if (NumberWords.TryGetValue(text, out var word))
        {
            return word;
        }

        // "the second one" picks the second numbered choice.
        if (text.EndsWith(" one", StringComparison.Ordinal) && OrdinalWords.TryGetValue(text[..^4], out var ordinal) && ordinal > 0)
        {
            return ordinal;
        }

        return null;
    }

    /// <summary>“go to the first tab”, “tab 3”, “last tab”, “the YouTube tab”, “the tab where YouTube is open”.</summary>
    private static VoiceCommand? MatchTab(string n)
    {
        var text = n;
        foreach (var verb in TabVerbs)
        {
            if (text.StartsWith(verb + " ", StringComparison.Ordinal))
            {
                text = text[(verb.Length + 1)..].Trim();
                break;
            }
        }

        text = StripArticles(text);
        foreach (var lead in new[] { "tab with ", "tab where ", "tab that has ", "tab that shows ", "tab showing ", "tab containing ", "tab for ", "tab of " })
        {
            if (text.StartsWith(lead, StringComparison.Ordinal))
            {
                var name = text[lead.Length..].Trim();
                foreach (var tail in new[] { " is open", " is opened", " is playing", " open", " opened", " on it", " is" })
                {
                    if (name.EndsWith(tail, StringComparison.Ordinal))
                    {
                        name = name[..^tail.Length].Trim();
                        break;
                    }
                }

                name = Simplify(StripArticles(name));
                return name.Length == 0 ? null : Tab(name, null);
            }
        }

        if (text.StartsWith("tab ", StringComparison.Ordinal))
        {
            var rest = text[4..].Trim();
            if (rest.StartsWith("number ", StringComparison.Ordinal))
            {
                rest = rest[7..].Trim();
            }

            if (int.TryParse(rest, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0)
            {
                return Tab(string.Empty, number);
            }

            return NumberWords.TryGetValue(rest, out var word) ? Tab(string.Empty, word) : null;
        }

        if (!text.EndsWith(" tab", StringComparison.Ordinal))
        {
            return null;
        }

        var before = StripArticles(text[..^4].Trim());
        if (OrdinalWords.TryGetValue(before, out var ordinal))
        {
            return Tab(string.Empty, ordinal);
        }

        // "new tab", "next tab", "close tab" are shortcuts handled earlier; anything else names the tab.
        return before.Length == 0 || before is "new" or "next" or "previous" or "this" or "that" or "other" ? null : Tab(Simplify(before), null);

        static VoiceCommand Tab(string name, int? ordinal) => new(CommandKind.SelectTab) { Target = new ElementTarget(name, null, ordinal) };
    }

    /// <summary>“click New on the left”, “select Library”, “press the Issues button”, “expand Sessions”.</summary>
    private static VoiceCommand? MatchClick(string n)
    {
        foreach (var verb in ClickVerbs)
        {
            if (!n.StartsWith(verb + " ", StringComparison.Ordinal))
            {
                continue;
            }

            var target = ParseTarget(n[(verb.Length + 1)..]);
            return target.Name.Length == 0 && target.Ordinal is null
                ? null
                : new VoiceCommand(CommandKind.Click, verb) { Target = target };
        }

        return null;
    }

    /// <summary>“start typing in YouTube”, “type in the message box”, “start typing in the first tab”.</summary>
    private static IReadOnlyList<VoiceCommand>? MatchStartTypingIn(string n)
    {
        foreach (var verb in TypingVerbs)
        {
            foreach (var preposition in new[] { " in ", " into ", " on " })
            {
                var lead = verb + preposition;
                if (!n.StartsWith(lead, StringComparison.Ordinal))
                {
                    continue;
                }

                var rest = n[lead.Length..].Trim();
                var bare = StripArticles(rest);
                if (bare is "it" or "here" or "this" or "there" or "this window" or "this app")
                {
                    return [new VoiceCommand(CommandKind.StartTyping)];
                }

                if (SearchTargets.Contains(bare)) return [new VoiceCommand(CommandKind.FocusSearch)];
                if (AddressTargets.Contains(bare)) return [new VoiceCommand(CommandKind.FocusAddressBar)];

                foreach (var suffix in FieldSuffixes)
                {
                    if (bare == suffix || bare.EndsWith(" " + suffix, StringComparison.Ordinal))
                    {
                        var field = bare == suffix ? string.Empty : bare[..^(suffix.Length + 1)].Trim();
                        return field is "" or "text"
                            ? [new VoiceCommand(CommandKind.StartTyping)]
                            : [new VoiceCommand(CommandKind.FocusField) { Target = new ElementTarget(field) }];
                    }
                }

                if (MatchTab(rest) is { } tab)
                {
                    return [tab, new VoiceCommand(CommandKind.StartTyping)];
                }

                var app = CleanAppName(rest);
                return app.Length == 0 ? null : [new VoiceCommand(CommandKind.SwitchToApp, app), new VoiceCommand(CommandKind.StartTyping)];
            }
        }

        return null;
    }
}
