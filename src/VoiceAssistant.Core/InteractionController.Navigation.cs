using System.Globalization;

namespace VoiceAssistant.Core;

/// <summary>
/// Navigation: tabs, clicking by name, numbered choices, confirmations, choosing a text box, and the current place.
/// The behaviour is specified in docs/specs/navigation.md; keep the two in sync.
/// </summary>
public sealed partial class InteractionController
{
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] GenericBoxNames = ["search", "message", "type a message", "comment", "reply", "ask", "chat", "write"];

    private readonly List<Choice> _choices = [];
    private DateTime _choicesAt;
    private (UiElement Element, WindowRef Window, DateTime At)? _confirm;
    private string? _place;

    /// <summary>Numbers to draw on screen (null hides them).</summary>
    public event Action<IReadOnlyList<ChoiceBadge>?>? ChoicesChanged;

    /// <summary>Briefly outline what was just clicked, selected or focused.</summary>
    public event Action<ScreenRect>? Highlight;

    /// <summary>Where the assistant last acted, e.g. “Google Chrome › YouTube › Search”.</summary>
    public string? Place => _place;

    private enum ChoiceKind
    {
        Element,
        Tab,
        TextBox,
    }

    private sealed record Choice(int Number, ChoiceKind Kind, WindowRef Window, UiElement? Element = null, BrowserTab? Tab = null);

    /// <summary>“go to X” / “open X”: a tab, an app, or (last) something in the front window called X.</summary>
    private async Task<bool> NavigateAsync(string spokenName, bool newWindow, string heard, CancellationToken ct)
    {
        // "Go to YouTube" means the YouTube tab if one is open, unless an app is called exactly that.
        if (!newWindow && !_apps.HasExactName(spokenName) && await TrySelectNamedTabAsync(spokenName, heard, ct).ConfigureAwait(false) is { } tabResult)
        {
            return tabResult;
        }

        if (_apps.Resolve(spokenName) is not AppResolution.NotFound)
        {
            return await OpenAsync(spokenName, newWindow, heard, ct).ConfigureAwait(false);
        }

        if (newWindow && await TrySelectNamedTabAsync(spokenName, heard, ct).ConfigureAwait(false) is { } openTab)
        {
            return openTab;
        }

        // Not an app or tab: "open Library" / "go to Pull requests" clicks it, but only on a strong name match.
        if (_desktop.GetForegroundWindow() is not null)
        {
            var target = CommandParser.ParseTarget(spokenName);
            var clicked = await ClickAsync(target, heard, minScore: NameMatcher.Words, ct, quietIfMissing: true).ConfigureAwait(false);
            if (clicked is not null)
            {
                return clicked.Value;
            }
        }

        Report(StatusKind.Warning, $"I couldn't find an app, tab or button called “{spokenName}”.", heard);
        return false;
    }

    /// <summary>Null when no tab matches (so the caller tries apps); otherwise whether the tab was selected.</summary>
    private async Task<bool?> TrySelectNamedTabAsync(string name, string heard, CancellationToken ct)
    {
        var tabs = await _desktop.ListTabsAsync(ct).ConfigureAwait(false);
        switch (TargetResolver.ResolveTab(tabs, new ElementTarget(name)))
        {
            case Resolution<BrowserTab>.Single single:
                return await ActivateTabAsync(single.Item, heard, ct).ConfigureAwait(false);
            case Resolution<BrowserTab>.Ambiguous ambiguous:
                OfferTabs(ambiguous.Items, heard);
                return false;
            default:
                return null;
        }
    }

    private async Task<bool> SelectTabAsync(ElementTarget target, string heard, CancellationToken ct)
    {
        var tabs = await _desktop.ListTabsAsync(ct).ConfigureAwait(false);
        if (tabs.Count == 0)
        {
            Report(StatusKind.Warning, "I don't see any browser tabs.", heard);
            return false;
        }

        switch (TargetResolver.ResolveTab(tabs, target))
        {
            case Resolution<BrowserTab>.Single single:
                return await ActivateTabAsync(single.Item, heard, ct).ConfigureAwait(false);
            case Resolution<BrowserTab>.Ambiguous ambiguous:
                OfferTabs(ambiguous.Items, heard);
                return false;
            default:
                var count = tabs.Count(t => t.Window.SameWindow(tabs[0].Window));
                Report(
                    StatusKind.Warning,
                    target.Name.Length == 0 ? $"That window has {count} tab{(count == 1 ? string.Empty : "s")}." : $"I couldn't find a tab called “{target.Name}”.",
                    heard);
                return false;
        }
    }

    private async Task<bool> ActivateTabAsync(BrowserTab tab, string heard, CancellationToken ct)
    {
        var result = await _desktop.SelectTabAsync(tab, ct).ConfigureAwait(false);
        Trace?.Invoke($"tab select {(result.Success ? "ok" : "failed")}");
        if (!result.Success)
        {
            Report(StatusKind.Warning, result.Message, heard);
            return false;
        }

        var title = Short(NameMatcher.CleanTabTitle(tab.Title), 40);
        SetPlace(tab.Window, $"{AppLabel(tab.Window, _apps.FindProfileForProcess(tab.Window.ProcessName, tab.Window.Title))} › {title}");
        Highlight?.Invoke(tab.Rect);
        Report(StatusKind.Success, $"On the “{title}” tab.", heard, _place);
        return true;
    }

    /// <summary>
    /// Clicks the element the user named in the front window. Null (with <paramref name="quietIfMissing"/>) means
    /// nothing matched and nothing was reported, so the caller can try something else.
    /// </summary>
    private async Task<bool?> ClickAsync(ElementTarget target, string heard, int minScore, CancellationToken ct, bool quietIfMissing)
    {
        var window = _desktop.GetForegroundWindow();
        if (window is null)
        {
            Report(StatusKind.Warning, "There's no window in front.", heard);
            return false;
        }

        var elements = await _desktop.ListClickableAsync(window, ct).ConfigureAwait(false);
        var resolution = TargetResolver.ResolveElement(elements, target, _desktop.GetWindowRect(window), minScore);

        // Browsers build a tab's page elements only after the tab is shown, so right after "go to the second tab"
        // the page can still be missing for a moment: look again before saying it isn't there.
        if (resolution is Resolution<UiElement>.None && _apps.FindProfileForProcess(window.ProcessName, window.Title)?.AddressBarKeys is not null)
        {
            for (var attempt = 0; attempt < 3 && resolution is Resolution<UiElement>.None; attempt++)
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
                if (!window.SameWindow(_desktop.GetForegroundWindow()))
                {
                    break;
                }

                elements = await _desktop.ListClickableAsync(window, ct).ConfigureAwait(false);
                resolution = TargetResolver.ResolveElement(elements, target, _desktop.GetWindowRect(window), minScore);
            }
        }

        switch (resolution)
        {
            case Resolution<UiElement>.Single single:
                return await ActOnElementAsync(single.Item, window, heard, ct).ConfigureAwait(false);
            case Resolution<UiElement>.Ambiguous ambiguous:
                Offer(ambiguous.Items.Select((e, i) => new Choice(i + 1, ChoiceKind.Element, window, Element: e)).ToList(), $"Which “{target.Name}”? Say the number.", heard);
                return false;
            default:
                if (quietIfMissing)
                {
                    return null;
                }

                Report(StatusKind.Warning, $"I couldn't find “{Describe(target)}” here. Say “show numbers” to see what I can click.", heard);
                return false;
        }
    }

    private async Task<bool> ClickAsync(ElementTarget target, string heard, int minScore, CancellationToken ct) =>
        await ClickAsync(target, heard, minScore, ct, quietIfMissing: false).ConfigureAwait(false) ?? false;

    private async Task<bool> ActOnElementAsync(UiElement element, WindowRef window, string heard, CancellationToken ct)
    {
        var label = Short(NameMatcher.CleanElementName(element.Name), 40);
        if (TargetResolver.IsDestructive(element.Name))
        {
            _confirm = (element, window, _options.Clock());
            Highlight?.Invoke(element.Rect);
            Trace?.Invoke("click needs confirmation");
            Report(StatusKind.Warning, $"“{label}” can't be undone. Say “yes” to click it, or “no”.", heard);
            return false;
        }

        return await ClickVerifiedAsync(element, window, label, heard, ct).ConfigureAwait(false);
    }

    private async Task<bool> ClickVerifiedAsync(UiElement element, WindowRef window, string label, string heard, CancellationToken ct)
    {
        if (!window.SameWindow(_desktop.GetForegroundWindow()))
        {
            Report(StatusKind.Warning, "Focus moved to another window, so I didn't click.", heard);
            return false;
        }

        var result = await _desktop.ClickAsync(element, ct).ConfigureAwait(false);
        Trace?.Invoke($"click {element.Role} {(result.Success ? "ok" : "failed")}");
        if (!result.Success)
        {
            Report(StatusKind.Warning, result.Message, heard);
            return false;
        }

        Highlight?.Invoke(element.Rect);
        SetPlace(window, AppLabel(window, _apps.FindProfileForProcess(window.ProcessName, window.Title)));
        Report(StatusKind.Success, $"Clicked “{label}”.", heard, _place);
        return true;
    }

    private async Task<bool> ConfirmAsync(string heard, CancellationToken ct)
    {
        if (_confirm is not { } pending || _options.Clock() - pending.At > PendingTimeout)
        {
            _confirm = null;
            Report(StatusKind.Info, "There's nothing waiting for a yes.", heard);
            return false;
        }

        _confirm = null;
        return await ClickVerifiedAsync(pending.Element, pending.Window, Short(NameMatcher.CleanElementName(pending.Element.Name), 40), heard, ct).ConfigureAwait(false);
    }

    private async Task<bool> ShowNumbersAsync(string heard, CancellationToken ct)
    {
        var window = _desktop.GetForegroundWindow();
        if (window is null)
        {
            Report(StatusKind.Warning, "There's no window in front.", heard);
            return false;
        }

        var clickable = TargetResolver.Deduplicate(await _desktop.ListClickableAsync(window, ct).ConfigureAwait(false));
        var boxes = await _desktop.ListTextBoxesAsync(window, ct).ConfigureAwait(false);
        var all = TargetResolver.SortReadingOrder(
                clickable.Select(e => (Element: e, Kind: ChoiceKind.Element))
                    .Concat(boxes.Select(b => (Element: b, Kind: ChoiceKind.TextBox))),
                x => x.Element.Rect)
            .Take(99)
            .Select((x, i) => new Choice(i + 1, x.Kind, window, Element: x.Element))
            .ToList();
        if (all.Count == 0)
        {
            Report(StatusKind.Warning, "I can't see anything to click in this window.", heard);
            return false;
        }

        Offer(all, "Say a number to click it, or “hide numbers”.", heard);
        return true;
    }

    private async Task<bool> ChooseAsync(int number, string heard, CancellationToken ct)
    {
        ExpirePending();
        var choice = _choices.FirstOrDefault(c => c.Number == number);
        if (choice is null)
        {
            Report(StatusKind.Warning, _choices.Count == 0 ? "There's nothing to choose. Say “show numbers” first." : $"There's no number {number}.", heard);
            return false;
        }

        ClearPending();
        return choice.Kind switch
        {
            ChoiceKind.Tab => await ActivateTabAsync(choice.Tab!, heard, ct).ConfigureAwait(false),
            ChoiceKind.TextBox => await FocusBoxAndBindAsync(choice.Element!, choice.Window, heard, ct).ConfigureAwait(false),
            _ => await ActOnElementAsync(choice.Element!, choice.Window, heard, ct).ConfigureAwait(false),
        };
    }

    /// <summary>“start typing in the message box”: the text box with that name in the front window.</summary>
    private async Task<bool> FocusFieldAsync(ElementTarget target, string heard, CancellationToken ct)
    {
        var window = _desktop.GetForegroundWindow();
        if (window is null)
        {
            Report(StatusKind.Warning, "There's no window to type into.", heard);
            return false;
        }

        var boxes = await _desktop.ListTextBoxesAsync(window, ct).ConfigureAwait(false);
        switch (TargetResolver.ResolveElement(boxes, target, _desktop.GetWindowRect(window)))
        {
            case Resolution<UiElement>.Single single:
                return await FocusBoxAndBindAsync(single.Item, window, heard, ct).ConfigureAwait(false);
            case Resolution<UiElement>.Ambiguous ambiguous:
                Offer(ambiguous.Items.Select((b, i) => new Choice(i + 1, ChoiceKind.TextBox, window, Element: b)).ToList(), "Which box? Say the number.", heard);
                return false;
            default:
                Report(StatusKind.Warning, $"I couldn't find a “{target.Name}” box here.", heard);
                return false;
        }
    }

    /// <summary>
    /// For “start typing” when nothing editable has focus: the only text box, else the one named like a message/search
    /// box, else numbers to choose from. Null means there are no text boxes (the caller keeps its old behaviour).
    /// In browsers the address bar doesn't count: it's the fallback when the page has no boxes.
    /// </summary>
    private async Task<bool?> PickTextBoxAsync(WindowRef window, string heard, CancellationToken ct, bool browser = false)
    {
        var boxes = (await _desktop.ListTextBoxesAsync(window, ct).ConfigureAwait(false))
            .Where(b => !browser || !IsAddressBar(b))
            .ToList();
        if (boxes.Count == 0)
        {
            return null;
        }

        if (boxes.Count == 1)
        {
            return await FocusBoxAndBindAsync(boxes[0], window, heard, ct).ConfigureAwait(false);
        }

        var named = boxes
            .Where(b => GenericBoxNames.Any(n => NameMatcher.Score(n, NameMatcher.CleanElementName(b.Name)) >= NameMatcher.Words))
            .ToList();
        if (named.Count == 1)
        {
            return await FocusBoxAndBindAsync(named[0], window, heard, ct).ConfigureAwait(false);
        }

        var choices = TargetResolver.SortReadingOrder(named.Count > 1 ? named : boxes)
            .Take(20)
            .Select((b, i) => new Choice(i + 1, ChoiceKind.TextBox, window, Element: b))
            .ToList();
        Offer(choices, "Which box? Say the number.", heard);
        return false;
    }

    /// <summary>A known website's main box (YouTube's search, WhatsApp's message box). False if it isn't there.</summary>
    private async Task<bool> FocusSiteBoxAsync(WindowRef window, SiteProfile site, string heard, CancellationToken ct)
    {
        var boxes = await _desktop.ListTextBoxesAsync(window, ct).ConfigureAwait(false);
        foreach (var name in site.TextBoxNames)
        {
            var match = TargetResolver.SortReadingOrder(boxes.Where(b => NameMatcher.Score(name, NameMatcher.CleanElementName(b.Name)) >= NameMatcher.Words)).FirstOrDefault();
            if (match is not null)
            {
                return await FocusBoxAndBindAsync(match, window, heard, ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    private async Task<bool> FocusBoxAndBindAsync(UiElement box, WindowRef window, string heard, CancellationToken ct)
    {
        if (!window.SameWindow(_desktop.GetForegroundWindow()))
        {
            Report(StatusKind.Warning, "Focus moved to another window, so I didn't start typing.", heard);
            return false;
        }

        var focused = await _desktop.FocusTextBoxAsync(box, ct).ConfigureAwait(false);
        Trace?.Invoke($"text box focus {(focused.Success ? "ok" : "failed")}");
        if (!focused.Success)
        {
            Report(StatusKind.Warning, focused.Message, heard);
            return false;
        }

        var profile = _apps.FindProfileForProcess(window.ProcessName, window.Title);
        var label = Short(NameMatcher.CleanElementName(box.Name), 30);
        var search = label.Contains("search", StringComparison.OrdinalIgnoreCase);
        BindTarget(window, profile, focused.HasTrailingText == true, searchField: search);
        Highlight?.Invoke(box.Rect);
        SetPlace(window, $"{PlaceOf(window, profile)} › {(label.Length == 0 ? "Text box" : label)}");
        Report(
            StatusKind.Success,
            search ? "In the search box. Say what to search for, then “search”." : $"Typing into “{(label.Length == 0 ? "the text box" : label)}”. Say “stop typing” when done.",
            heard,
            _place);
        return true;
    }

    private static bool IsAddressBar(UiElement box) =>
        box.Name.Contains("address", StringComparison.OrdinalIgnoreCase) || box.Name.Contains("URL", StringComparison.Ordinal);

    private static readonly HashSet<string> TabChangingChords = new(StringComparer.OrdinalIgnoreCase)
        { "Ctrl+T", "Ctrl+W", "Ctrl+Tab", "Ctrl+Shift+Tab", "Alt+Left", "Alt+Right", "Alt+Up", "F5" };

    /// <summary>Commands after which the old typing target may show different content (another tab, page or app).</summary>
    private static bool MovesAway(VoiceCommand command) => command.Kind switch
    {
        CommandKind.OpenApp or CommandKind.SwitchToApp or CommandKind.SelectTab or CommandKind.Click or CommandKind.Choose => true,
        CommandKind.PressKeys => TabChangingChords.Contains(command.Argument ?? string.Empty),
        _ => false,
    };

    private void Unbind()
    {
        _target = null;
        _targetProfile = null;
        _segments.Clear();
        if (Mode == InteractionMode.Dictation)
        {
            Awake();
        }
    }

    private void OfferTabs(IReadOnlyList<BrowserTab> tabs, string heard)
    {
        var choices = tabs.Take(9).Select((t, i) => new Choice(i + 1, ChoiceKind.Tab, t.Window, Tab: t)).ToList();
        var list = string.Join(" · ", choices.Select(c => $"{c.Number} “{Short(NameMatcher.CleanTabTitle(c.Tab!.Title), 24)}”"));
        Offer(choices, $"Which tab? {list}", heard);
    }

    private void Offer(List<Choice> choices, string message, string heard)
    {
        _choices.Clear();
        _choices.AddRange(choices);
        _choicesAt = _options.Clock();
        if (Mode == InteractionMode.Dictation)
        {
            // Numbers are answered by voice: while typing, "two" would be typed instead.
            Awake();
        }

        if (Mode == InteractionMode.Command)
        {
            // Give the user the full window to answer with a number.
            _awakeSince = _options.Clock();
        }

        Trace?.Invoke($"choices offered: {choices.Count}");
        ChoicesChanged?.Invoke(choices
            .Select(c => new ChoiceBadge(
                c.Number,
                c.Tab?.Rect ?? c.Element!.Rect,
                c.Tab is not null ? NameMatcher.CleanTabTitle(c.Tab.Title) : NameMatcher.CleanElementName(c.Element!.Name)))
            .ToList());
        Report(StatusKind.Info, message, heard);
    }

    private void ClearPending()
    {
        _confirm = null;
        if (_choices.Count > 0)
        {
            _choices.Clear();
            ChoicesChanged?.Invoke(null);
        }
    }

    private void ExpirePending()
    {
        var now = _options.Clock();
        if (_confirm is { } pending && now - pending.At > PendingTimeout)
        {
            _confirm = null;
        }

        if (_choices.Count > 0 && now - _choicesAt > PendingTimeout)
        {
            _choices.Clear();
            ChoicesChanged?.Invoke(null);
        }
    }

    private void SetPlace(WindowRef window, string path) => _place = path.Length == 0 ? DisplayName(window) : path;

    /// <summary>The place path's first part: the app, plus the tab for browsers (from the window title).</summary>
    private string PlaceOf(WindowRef window, AppEntry? profile)
    {
        var app = AppLabel(window, profile);
        if (profile?.SearchKeys is null)
        {
            return app;
        }

        var tab = Short(NameMatcher.CleanTabTitle(window.Title), 40);
        return tab.Length == 0 ? app : $"{app} › {tab}";
    }

    private static string AppLabel(WindowRef window, AppEntry? profile)
    {
        if (profile is not null)
        {
            return profile.Name;
        }

        // Apps usually end their title with their name ("Report.docx - Word"); short titles are the name ("Copilot").
        var title = window.Title.Replace('\u200B', ' ').Trim();
        var last = title.Split(" - ").Last().Trim();
        if (last.Length is > 0 and <= 30)
        {
            return last;
        }

        return string.IsNullOrWhiteSpace(window.ProcessName) ? DisplayName(window) : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(window.ProcessName);
    }

    private static string Describe(ElementTarget target)
    {
        var name = target.Name;
        if (target.Ordinal is { } n)
        {
            name = $"{(n == -1 ? "last" : n.ToString(CultureInfo.InvariantCulture) + Suffix(n))} {name}".Trim();
        }

        return target.Region is { } region ? $"{name} on the {region.ToString().ToLowerInvariant()}" : name;

        static string Suffix(int n) => n switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
    }

    private static string Short(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
