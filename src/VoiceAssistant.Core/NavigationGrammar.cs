using System.Text.RegularExpressions;

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

    // A new tab or window as the place to open something: "in a new tab", "on another tab", "in a new browser window".
    // Never "the other tab" or "the second tab": those are tabs already open. ("tap" is how speech recognition often
    // hears "tab" here.)
    private const string NewPlaceWords = @"(?:(?:a |the )?(?:new|separate|fresh)|(?:a )?different|another|an other|a second)";
    private const string NewPlace = $@"{NewPlaceWords} (?:browser )?(?<place>tabs?|tap|windows?)";
    private const string OpenPlaceVerbs = "open up|open|launch|load|pull up|bring up|go to|take me to|show me|show|get me|start|visit|navigate to";

    // Not something to open: things on screen ("it", "this link"), kinds of tab ("a blank page in a new tab", "incognito in
    // a new window") and typing ("open a new tab and start dictating").
    private static readonly HashSet<string> NotAPageName =
    [
        "it", "this", "that", "them", "this one", "that one", "link", "this link", "that link", "page", "this page", "that page",
        "incognito", "private", "inprivate", "in private", "blank", "empty", "browser",
        "typing", "type", "writing", "write", "dictating", "dictation", "dictate",
    ];

    private static readonly string[] PageNameTails = [" website", " web site", " webpage", " web page", " homepage", " home page", " site", " page"];

    /// <summary>
    /// “open Instagram in a new tab”, “open instagram.com in another tab”, “open a new tab and go to GitHub”, “open Notepad
    /// in a new window”: opening something there, never selecting a tab called “instagram in new”. (“Open a new Instagram
    /// tab” stays a tab name, as “the What's New tab” is one; when no tab has it, the AI brain gets the request.)
    /// </summary>
    private static VoiceCommand? MatchOpenInNewPlace(string n)
    {
        // "start typing in a new tab", "open a new tab and start typing": typing commands, not a page called "typing".
        if (TypingVerbs.Any(v => n.StartsWith(v + " ", StringComparison.Ordinal)) || ChainedStartTyping.Any(s => n.EndsWith(s, StringComparison.Ordinal)))
        {
            return null;
        }

        var m = InNewPlaceRegex().Match(n) is { Success: true } trailing ? trailing : NewPlaceThenRegex().Match(n);
        if (!m.Success)
        {
            return null;
        }

        var name = StripArticles(m.Groups["name"].Value.Trim());
        foreach (var tail in PageNameTails)
        {
            if (name.EndsWith(tail, StringComparison.Ordinal) && name.Length > tail.Length)
            {
                name = name[..^tail.Length].Trim();
                break;
            }
        }

        // "open it in a new tab" is about something on screen: the AI brain (or the click grammar) works that out.
        // "go to the WhatsApp tab in a different window" names a tab that's open already.
        return name.Length == 0 || NotAPageName.Contains(name) || EndsWithWord(name, "tab", "tabs", "window", "windows")
            ? null
            : new VoiceCommand(m.Groups["place"].Value.StartsWith("window", StringComparison.Ordinal) ? CommandKind.OpenInNewWindow : CommandKind.OpenInNewTab, name);
    }

    private static bool EndsWithWord(string text, params string[] words) =>
        words.Any(w => text == w || text.EndsWith(" " + w, StringComparison.Ordinal));

    /// <summary>
    /// “first link”, “this video”, “downloads folder”, “number 3”, “link 3”, “the top result”: something on screen rather
    /// than a website or app, so “open … in a new tab” never searches the web for it. Real names are spared: “Prime
    /// Video”, “election results”, “Formula 1”, “Capital One”.
    /// </summary>
    internal static bool NamesSomethingOnScreen(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }

        var last = words[^1];
        var numbered = int.TryParse(last, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _) ||
            NumberWords.ContainsKey(last);
        return (numbered && words is [_] or ["number" or "link" or "result" or "item" or "video" or "option" or "choice", _]) ||
            words is ["top" or "bottom" or "other" or "latest" or "newest" or "oldest" or "same" or "main" or "current" or "middle", "one" or "ones" or "result" or "results" or "video" or "videos"] ||
            OrdinalWords.ContainsKey(words[0]) || words[0] is "this" or "that" or "these" or "those" or "next" or "previous" ||
            EndsWithWord(name, "link", "links", "folder", "folders", "file", "files", "item", "items", "button", "buttons") ||
            name is "video" or "videos" or "image" or "images" or "picture" or "pictures" or "photo" or "photos" or "email" or "emails" or
                "message" or "messages" or "post" or "posts" or "article" or "articles" or "result" or "results";
    }

    [GeneratedRegex($@"^(?:{OpenPlaceVerbs}) (?<name>.+?) (?:in|on|into|inside) {NewPlace}$")]
    private static partial Regex InNewPlaceRegex();

    [GeneratedRegex($@"^(?:open up|open|launch|start) {NewPlace}(?: and then| and| then)? (?:{OpenPlaceVerbs}|with|for|to) (?<name>.+)$")]
    private static partial Regex NewPlaceThenRegex();

    // The end of "open Instagram in a new" before "tab": a new tab, not a tab's name.
    [GeneratedRegex($@"(?:^| )(?:in|on|into|inside) {NewPlaceWords}(?: browser)?$")]
    private static partial Regex NewPlaceTailRegex();

    /// <summary>The command's words as said: the replacement in “replace X with Y”, and web addresses (“instagram.com”).</summary>
    private static IReadOnlyList<VoiceCommand> AsSaid(IReadOnlyList<VoiceCommand> commands, string cleaned) =>
        WithWebAddresses(WithSpokenReplacement(commands, cleaned), cleaned);

    /// <summary>
    /// “open instagram.com in a new tab”: normalizing turned the address into “instagram com”, so it is taken back from
    /// the words as said (or “instagram dot com” is joined up).
    /// </summary>
    private static IReadOnlyList<VoiceCommand> WithWebAddresses(IReadOnlyList<VoiceCommand> commands, string cleaned)
    {
        if (!commands.Any(OpensSomething))
        {
            return commands;
        }

        var said = SaidAddressRegex().Matches(cleaned).Select(m => m.Value.TrimEnd('.', ',', '!', '?', ';', ':').ToLowerInvariant()).ToList();
        return commands
            .Select(c => OpensSomething(c) && c.Argument is { } name && AddressAsSaid(name, said) is { } address ? c with { Argument = address } : c)
            .ToList();

        static bool OpensSomething(VoiceCommand c) =>
            c.Kind is CommandKind.OpenApp or CommandKind.SwitchToApp or CommandKind.OpenInNewTab or CommandKind.OpenInNewWindow;
    }

    private static string? AddressAsSaid(string name, IReadOnlyList<string> said)
    {
        foreach (var address in said)
        {
            if (TranscriptText.Normalize(address) == name)
            {
                return address;
            }
        }

        return SpokenDotsRegex().IsMatch(name) ? name.Replace(" dot ", ".", StringComparison.Ordinal) : null;
    }

    [GeneratedRegex(@"(?:https?://)?(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}(?:/\S*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SaidAddressRegex();

    [GeneratedRegex(@"^[a-z0-9-]+(?: dot [a-z0-9-]+)+$")]
    private static partial Regex SpokenDotsRegex();

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

        if (NewPlaceTailRegex().IsMatch(before))
        {
            // "switch to Instagram in a new tab": a new tab, not one called "instagram in new".
            return null;
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
