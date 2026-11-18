using System.Diagnostics;

namespace VoiceAssistant.Core;

/// <summary>
/// The AI brain: requests the fixed commands don't cover (“open the MrBeast video on YouTube”) are planned by an
/// on-device model from a summary of the screen, then run step by step with the assistant's own abilities. The limits
/// are enforced here whatever the model says: no typing or keys in command windows, no Win+R, web links only, a “yes”
/// before anything that sends, deletes, closes or types a lot, at most 8 steps and 30 seconds. See
/// docs/design/ai-brain.md and docs/specs/voice-commands.md (keep them in sync). Logs tool names, never words or names.
/// </summary>
public sealed partial class InteractionController
{
    private static readonly TimeSpan BrainPlanLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BrainRunLimit = TimeSpan.FromSeconds(30);

    private BrainPending? _brainPending;
    private string? _brainRequest;
    private int _brainRunning;
    private int _brainChanges;

    /// <summary>The last utterance was “Hey Jarvis” on its own: the next one is addressed to Jarvis.</summary>
    private bool _brainAddressed;

    /// <summary>The on-device model for requests the fixed commands don't cover; null keeps the fixed commands only.</summary>
    public IBrainModel? Brain { get; set; }

    /// <summary>True while the brain is planning or acting, so Esc can stop it. Safe to read from any thread.</summary>
    public bool IsBrainRunning => Volatile.Read(ref _brainRunning) != 0;

    /// <summary>
    /// Changes whenever the brain starts or stops work. Read it when speech starts and pass it with the transcript, so
    /// speech that began before the brain's last start or stop is ignored: a “yes” said before it asked never answers it.
    /// Safe to read from any thread.
    /// </summary>
    public int BrainChanges => Volatile.Read(ref _brainChanges);

    private bool BrainTakes => Brain is { Availability: not (BrainAvailability.Off or BrainAvailability.NotInstalled) };

    private enum StepResult
    {
        Done,
        Stop,

        /// <summary>Asked a question or waiting for “yes”: the plan stops without failing.</summary>
        Waiting,
    }

    private sealed class BrainRun
    {
        public required string Heard { get; set; }

        public required string Say { get; init; }

        public DateTime Started { get; set; }

        /// <summary>The place shown before the plan ran: the final message shows the place only if a step changed it.</summary>
        public string? PlaceBefore { get; init; }

        /// <summary>
        /// Windows where this plan typed into a box that isn't a search box: there any send key or click needs a “yes”, so
        /// typing into a search box afterwards can't clear it.
        /// </summary>
        public HashSet<nint> MessageTypedIn { get; } = [];

        /// <summary>Characters typed by this plan so far: more than the limit in all needs a “yes”.</summary>
        public int TypedChars { get; set; }
    }

    private sealed record BrainPending(IReadOnlyList<BrainStep> Steps, int Next, BrainRun Run, WindowRef Window, DateTime At);

    /// <summary>The brain takes the request in place of a fixed command that found nothing (once per utterance).</summary>
    private string? TakeBrainRequest()
    {
        var request = BrainTakes ? _brainRequest : null;
        _brainRequest = null;
        return request;
    }

    private void SetBrainRunning(bool running)
    {
        Interlocked.Exchange(ref _brainRunning, running ? 1 : 0);
        Interlocked.Increment(ref _brainChanges);
    }

    /// <summary>
    /// Plans the request and runs the plan. Null when there was no plan: the model failed or answered nonsense (already
    /// reported), or, with <paramref name="onlyPages"/>, the plan opened no page (not reported: the caller carries on).
    /// Otherwise whether the plan ran to the end (or stopped to ask something).
    /// </summary>
    /// <param name="onlyPages">The request asked for a page (“open X in a new tab”): only steps that open one run.</param>
    private async Task<bool?> AskBrainAsync(string request, string heard, CancellationToken ct, bool onlyPages = false)
    {
        var brain = Brain!;
        _brainRequest = null;
        SetBrainRunning(true);
        var clock = Stopwatch.StartNew();
        try
        {
            Report(StatusKind.Info, brain.Availability == BrainAvailability.Ready ? "Thinking…" : "Waking up the AI brain (a few seconds the first time)…", heard);
            var (screen, scanned, itemCount) = await DescribeScreenAsync(request, ct).ConfigureAwait(false);
            Trace?.Invoke($"brain: screen of {scanned} windows, {itemCount} items, {screen.Length} chars in {clock.ElapsedMilliseconds} ms");

            string reply;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(BrainPlanLimit);
                try
                {
                    reply = await brain.CompleteAsync(BrainPrompt.Messages(screen, request), limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Trace?.Invoke("brain: no answer in time");
                    Report(StatusKind.Warning, "The AI brain took too long. Try again, or say it another way.", heard);
                    return null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The model is another process: any failure there means "no plan", never a crash here.
                    Trace?.Invoke($"brain: failed ({ex.GetType().Name})");
                    Report(StatusKind.Warning, "The AI brain isn't working right now. Try again in a moment.", heard);
                    return null;
                }
            }

            var plan = BrainPlan.Parse(reply, out var error);
            if (plan is null)
            {
                Trace?.Invoke($"brain: unusable reply ({error})");
                Report(StatusKind.Warning, "I couldn't work out how to do that. Try saying it another way.", heard);
                return null;
            }

            var steps = BrainPlan.Tidy(plan.Steps);
            var say = plan.Say;
            Trace?.Invoke($"brain: plan [{string.Join(", ", steps.Select(s => BrainPlan.NameOf(s.Tool)))}] in {clock.ElapsedMilliseconds} ms{(steps.Count < plan.Steps.Count ? $" ({plan.Steps.Count - steps.Count} new-tab step dropped)" : string.Empty)}");
            if (onlyPages)
            {
                if (BrainPlan.OnlyPages(steps) is not { } pages)
                {
                    // "A new GitHub tab" planned as switching to the GitHub app and clicking there: never what was asked.
                    Trace?.Invoke("brain: the plan opens no page; not run");
                    return null;
                }

                if (pages.Count < steps.Count)
                {
                    // The model's sentence may describe the dropped steps ("Switched to WhatsApp and opened GitHub").
                    Trace?.Invoke($"brain: {steps.Count - pages.Count} step(s) besides opening the page dropped");
                    say = string.Empty;
                }

                steps = pages;
            }

            if (steps.Count > 0)
            {
                // Windows and pages are about to change: "send" must not press Enter in a typing target left behind.
                ClearPending();
                Unbind();
            }

            var run = new BrainRun { Heard = heard, Say = say, Started = _options.Clock(), PlaceBefore = _place };
            return await RunPlanAsync(run, steps, 0, confirmedFirst: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Trace?.Invoke("brain: stopped");
            Report(StatusKind.Info, "Stopped.", heard);
            return false;
        }
        finally
        {
            SetBrainRunning(false);
        }
    }

    /// <summary>“yes” after the brain asked: runs the waiting step and the rest of the plan. Null if nothing was waiting.</summary>
    private async Task<bool?> ResumeBrainAsync(string heard, CancellationToken ct)
    {
        if (_brainPending is not { } pending)
        {
            return null;
        }

        _brainPending = null;
        if (_options.Clock() - pending.At > PendingTimeout)
        {
            Report(StatusKind.Info, "That waited too long, so I didn't do it. Ask again.", heard);
            return false;
        }

        if (!pending.Window.SameWindow(_desktop.GetForegroundWindow()))
        {
            Report(StatusKind.Warning, "Focus moved to another window, so I didn't do that.", heard);
            return false;
        }

        pending.Run.Heard = heard;
        pending.Run.Started = _options.Clock();
        SetBrainRunning(true);
        try
        {
            return await RunPlanAsync(pending.Run, pending.Steps, pending.Next, confirmedFirst: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Trace?.Invoke("brain: stopped");
            Report(StatusKind.Info, "Stopped.", heard);
            return false;
        }
        finally
        {
            SetBrainRunning(false);
        }
    }

    private async Task<bool> RunPlanAsync(BrainRun run, IReadOnlyList<BrainStep> steps, int start, bool confirmedFirst, CancellationToken ct)
    {
        if (steps.Count == 0)
        {
            // Chat or a question about something: the answer is the whole reply.
            Report(StatusKind.Success, run.Say.Length > 0 ? run.Say : "I'm not sure what to do there. Try saying it another way.", run.Heard);
            return run.Say.Length > 0;
        }

        for (var i = start; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (_options.Clock() - run.Started > BrainRunLimit)
            {
                Trace?.Invoke("brain: time limit");
                Report(StatusKind.Warning, "That was taking too long, so I stopped.", run.Heard);
                return false;
            }

            var result = await RunStepAsync(run, steps, i, confirmed: confirmedFirst && i == start, ct).ConfigureAwait(false);
            Trace?.Invoke($"brain: {BrainPlan.NameOf(steps[i].Tool)} {result.ToString().ToLowerInvariant()}");
            if (result == StepResult.Stop)
            {
                return false;
            }

            if (result == StepResult.Waiting)
            {
                return true;
            }
        }

        Report(StatusKind.Success, run.Say.Length > 0 ? run.Say : "Done.", run.Heard, _place != run.PlaceBefore ? _place : null);
        return true;
    }

    private async Task<StepResult> RunStepAsync(BrainRun run, IReadOnlyList<BrainStep> steps, int index, bool confirmed, CancellationToken ct)
    {
        var step = steps[index];
        var heard = run.Heard;
        switch (step.Tool)
        {
            case BrainTool.FocusWindow:
                return await BrainFocusAsync(First(step.Target, step.Text), heard, ct).ConfigureAwait(false) ? StepResult.Done : StepResult.Stop;

            case BrainTool.OpenApp:
                return await BrainOpenAppAsync(First(step.Target, step.Text), heard, ct).ConfigureAwait(false) ? StepResult.Done : StepResult.Stop;

            case BrainTool.OpenUrl:
                if (BrainSafety.NormalizeUrl(First(step.Target, step.Text)) is not { } url)
                {
                    Report(StatusKind.Warning, "I only open web links (http or https).", heard);
                    return StepResult.Stop;
                }

                return await OpenPageAsync(url, $"Opening {url.Host}…", heard, last: index == steps.Count - 1, ct).ConfigureAwait(false);

            case BrainTool.WebSearch:
                var (site, query) = step.Text.Length > 0 ? (step.Target, step.Text) : ("google", step.Target);
                if (query.Length == 0)
                {
                    Report(StatusKind.Warning, "I didn't catch what to search for.", heard);
                    return StepResult.Stop;
                }

                var (searchUrl, siteName) = BrainSafety.SearchUrl(site, query);
                return await OpenPageAsync(searchUrl, $"Searching {siteName} for “{Short(query, 40)}”…", heard, last: index == steps.Count - 1, ct).ConfigureAwait(false);

            case BrainTool.Click:
                return await BrainClickAsync(run, steps, index, confirmed, ct).ConfigureAwait(false);

            case BrainTool.TypeText:
                return await BrainTypeAsync(run, steps, index, confirmed, ct).ConfigureAwait(false);

            case BrainTool.Press:
                return BrainPress(run, steps, index, confirmed);

            case BrainTool.Scroll:
                if (_desktop.GetForegroundWindow() is not { } scrolled)
                {
                    Report(StatusKind.Warning, "There's no window in front.", heard);
                    return StepResult.Stop;
                }

                if (_desktop.IsCommandWindow(scrolled))
                {
                    // Page Up in a console brings back an earlier command.
                    RefuseCommandWindow(scrolled, typing: false, heard);
                    return StepResult.Stop;
                }

                if (!CheckField(heard, null, allowUnknown: true))
                {
                    return StepResult.Stop;
                }

                var up = First(step.Target, step.Text).Contains("up", StringComparison.OrdinalIgnoreCase);
                _desktop.PressKeys(up ? "PageUp" : "PageDown");
                Report(StatusKind.Success, up ? "Scrolled up." : "Scrolled down.", heard);
                return StepResult.Done;

            case BrainTool.Ask:
                var question = First(step.Text, step.Target, run.Say);
                Report(StatusKind.Info, question.Length > 0 ? question : "Can you say that another way?", heard);
                return StepResult.Waiting;

            default:
                return StepResult.Stop;
        }
    }

    /// <summary>A window from the screen by title or app name; else a tab with that name, or the app if it isn't open.</summary>
    private async Task<bool> BrainFocusAsync(string target, string heard, CancellationToken ct)
    {
        if (target.Length == 0)
        {
            Report(StatusKind.Warning, "I didn't catch which window.", heard);
            return false;
        }

        var windows = await _desktop.ListWindowsAsync(ct).ConfigureAwait(false);
        var match = windows
            .Select((w, i) => (w.Window, Index: i, Score: WindowScore(target, w.Window)))
            .Where(x => x.Score >= NameMatcher.Partial)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Window)
            .FirstOrDefault();
        if (match is null)
        {
            if (await TrySelectNamedTabAsync(target, heard, ct).ConfigureAwait(false) is { } tab)
            {
                return tab;
            }

            if (_apps.Resolve(target) is AppResolution.Found)
            {
                return await OpenAsync(target, newWindow: false, heard, ct).ConfigureAwait(false);
            }

            Report(StatusKind.Warning, $"I couldn't find a window called “{Short(target, 40)}”.", heard);
            return false;
        }

        if (!match.SameWindow(_desktop.GetForegroundWindow()))
        {
            var result = await _desktop.ActivateWindowAsync(match, ct).ConfigureAwait(false);
            Trace?.Invoke($"brain: window {(result.Success ? "activated" : "not activated")}");
            if (!result.Success)
            {
                Report(StatusKind.Warning, result.Message, heard);
                return false;
            }
        }

        SetPlace(match, PlaceOf(match, _apps.FindProfileForProcess(match.ProcessName, match.Title)));
        return true;
    }

    private int WindowScore(string target, WindowRef window) => Math.Max(
        Math.Max(NameMatcher.Score(target, window.Title), TargetResolver.TabScore(target, window.Title)),
        NameMatcher.Score(target, BrainAppName(window)));

    private async Task<bool> BrainOpenAppAsync(string name, string heard, CancellationToken ct)
    {
        if (name.Length == 0)
        {
            Report(StatusKind.Warning, "I didn't catch which app.", heard);
            return false;
        }

        if (_apps.Resolve(name) is AppResolution.NotFound && await TrySelectNamedTabAsync(name, heard, ct).ConfigureAwait(false) is { } tab)
        {
            // "open YouTube" when YouTube is a tab, not an app.
            return tab;
        }

        return await OpenAsync(name, newWindow: true, heard, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the page. Steps after it act on the browser, so if the browser didn't come to the front the plan stops there
    /// rather than clicking or typing in the window that's still in front.
    /// </summary>
    private async Task<StepResult> OpenPageAsync(Uri url, string message, string heard, bool last, CancellationToken ct)
    {
        var result = await _desktop.OpenUrlAsync(url, ct).ConfigureAwait(false);
        Trace?.Invoke($"brain: page {(result.Success ? "opened" : "not opened")}");
        if (!result.Success)
        {
            Report(StatusKind.Warning, result.Message, heard);
            return StepResult.Stop;
        }

        if (result.Window is not { } browser)
        {
            Report(StatusKind.Info, last ? message : "Opened the page, but the browser didn't come to the front, so I stopped there.", heard);
            return last ? StepResult.Done : StepResult.Waiting;
        }

        SetPlace(browser, AppLabel(browser, _apps.FindProfileForProcess(browser.ProcessName, browser.Title)));
        Report(StatusKind.Info, message, heard);
        return StepResult.Done;
    }

    private async Task<StepResult> BrainClickAsync(BrainRun run, IReadOnlyList<BrainStep> steps, int index, bool confirmed, CancellationToken ct)
    {
        var name = First(steps[index].Target, steps[index].Text);
        var heard = run.Heard;
        var window = _desktop.GetForegroundWindow();
        if (window is null)
        {
            Report(StatusKind.Warning, "There's no window in front.", heard);
            return StepResult.Stop;
        }

        if (name.Length == 0)
        {
            Report(StatusKind.Warning, "I didn't catch what to click.", heard);
            return StepResult.Stop;
        }

        var target = new ElementTarget(name);
        switch (await ResolveClickableAsync(window, target, NameMatcher.Partial, ct).ConfigureAwait(false))
        {
            case Resolution<UiElement>.Single single:
                var label = Short(NameMatcher.CleanElementName(single.Item.Name), 40);

                // After typing a message here, any click could send it. Explorer hides known extensions, so any file
                // there could be a program.
                if (!confirmed && (TargetResolver.IsDestructive(single.Item.Name) || BrainSafety.IsSendLike(single.Item.Name) ||
                    BrainSafety.RunsProgram(single.Item.Name) || BrainSafety.IsShellItem(window, single.Item.Role) ||
                    run.MessageTypedIn.Contains(window.Handle)))
                {
                    Highlight?.Invoke(single.Item.Rect);
                    return AskFirst(run, steps, index, window, $"Click “{label}”?");
                }

                return await ClickVerifiedAsync(single.Item, window, label, heard, ct).ConfigureAwait(false) ? StepResult.Done : StepResult.Stop;

            case Resolution<UiElement>.Ambiguous ambiguous:
                // The user picks with a number; the rest of the plan is dropped.
                Offer(ambiguous.Items.Select((e, i) => new Choice(i + 1, ChoiceKind.Element, window, Element: e)).ToList(), $"Which “{Short(name, 30)}”? Say the number.", heard);
                return StepResult.Waiting;
        }

        // Clicking a text box means putting the cursor in it.
        var boxes = await _desktop.ListTextBoxesAsync(window, ct).ConfigureAwait(false);
        if (TargetResolver.ResolveElement(boxes, target, _desktop.GetWindowRect(window), NameMatcher.Partial) is Resolution<UiElement>.Single box)
        {
            var focused = await _desktop.FocusTextBoxAsync(box.Item, ct).ConfigureAwait(false);
            if (!focused.Success)
            {
                Report(StatusKind.Warning, focused.Message, heard);
                return StepResult.Stop;
            }

            Highlight?.Invoke(box.Item.Rect);
            return StepResult.Done;
        }

        Report(StatusKind.Warning, $"I couldn't find “{Short(name, 40)}” in {AppLabel(window, _apps.FindProfileForProcess(window.ProcessName, window.Title))}.", heard);
        return StepResult.Stop;
    }

    private async Task<StepResult> BrainTypeAsync(BrainRun run, IReadOnlyList<BrainStep> steps, int index, bool confirmed, CancellationToken ct)
    {
        var step = steps[index];
        var heard = run.Heard;

        // type_text(text); some models also name the box in "target", or put the text there.
        var (boxName, text) = step.Text.Length > 0 ? (step.Target, step.Text) : (string.Empty, step.Target);
        var window = _desktop.GetForegroundWindow();
        if (window is null)
        {
            Report(StatusKind.Warning, "There's no window to type into.", heard);
            return StepResult.Stop;
        }

        if (text.Length == 0)
        {
            Report(StatusKind.Warning, "I didn't catch what to type.", heard);
            return StepResult.Stop;
        }

        if (_desktop.IsCommandWindow(window))
        {
            RefuseCommandWindow(window, typing: true, heard);
            return StepResult.Stop;
        }

        if (!confirmed && run.TypedChars + text.Length > BrainSafety.MaxTypedWithoutAsking)
        {
            return AskFirst(run, steps, index, window, $"Type {text.Length} characters into {AppLabel(window, _apps.FindProfileForProcess(window.ProcessName, window.Title))}?");
        }

        var profile = _apps.FindProfileForProcess(window.ProcessName, window.Title);
        var (found, boxLabel) = await FindTypingBoxAsync(window, profile, boxName, heard, ct).ConfigureAwait(false);
        if (!found)
        {
            return StepResult.Stop;
        }

        var browser = IsBrowser(window, profile);
        var focus = _desktop.DescribeFocus();
        boxLabel ??= focus?.Name;
        if (RefuseTypingAt(browser, focus, boxLabel, heard))
        {
            return StepResult.Stop;
        }

        var search = BrainSafety.IsSearchBox(boxLabel);
        if (search && BrainSafety.IsRefusedAddress(text))
        {
            // Search boxes that take addresses open them: a browser's new-tab page, the address bar of a browser no list
            // knows ("Search or enter address").
            Trace?.Invoke("brain: refused an address in a search box");
            Report(StatusKind.Warning, "I don't type web addresses into search boxes. Ask me to open the page instead.", heard);
            return StepResult.Stop;
        }

        if (!CheckField(heard, text, allowUnknown: false))
        {
            return StepResult.Stop;
        }

        _segments.Clear();
        _segments.Push(TypeWithNewLines(text, profile?.NewLineKeys ?? "Shift+Enter"));

        // Bound like "start typing" (without switching to typing), so "send", "scratch that" and "new line" act on it.
        _target = window;
        _targetProfile = profile;
        _typedTail = Tail(text);
        _boxHadText = false;
        _needsLeadingSpace = !char.IsWhiteSpace(text[^1]);
        _endsSentence = text[^1] is '.' or '!' or '?';
        _searchField = search;
        run.TypedChars += text.Length;
        if (!search)
        {
            run.MessageTypedIn.Add(window.Handle);
        }

        SetPlace(window, $"{PlaceOf(window, profile)} › {(boxLabel is { Length: > 0 } ? Short(NameMatcher.CleanElementName(boxLabel), 30) : "Text box")}");
        Report(StatusKind.Success, $"Typed “{Short(text, 40)}”.", heard, _place);
        return StepResult.Done;
    }

    /// <summary>
    /// Puts the cursor where the plan types: the box it named, else whatever already takes text, else the page's main
    /// box (a known site's, the only one, or the one named like a message or search box). Returns the name of the box it
    /// chose, or null when the cursor was already in one (the caller asks the desktop what has the focus).
    /// </summary>
    private async Task<(bool Found, string? Label)> FindTypingBoxAsync(WindowRef window, AppEntry? profile, string boxName, string heard, CancellationToken ct)
    {
        IReadOnlyList<UiElement>? boxes = null;
        if (boxName.Length > 0)
        {
            boxes = await _desktop.ListTextBoxesAsync(window, ct).ConfigureAwait(false);
            var named = TargetResolver.ResolveElement(boxes, new ElementTarget(boxName), _desktop.GetWindowRect(window), NameMatcher.Partial) switch
            {
                Resolution<UiElement>.Single single => single.Item,
                Resolution<UiElement>.Ambiguous ambiguous => ambiguous.Items[0],
                _ => null,
            };
            if (named is not null)
            {
                return await FocusBoxAsync(named, heard, ct).ConfigureAwait(false);
            }
        }

        var prepared = await _desktop.PrepareTextTargetAsync(window, profile, ct).ConfigureAwait(false);
        if (!prepared.Success)
        {
            Report(StatusKind.Warning, prepared.Message, heard);
            return (false, null);
        }

        if (prepared.FocusIsEditable != false)
        {
            // The caller reads the focused box's name.
            return (true, null);
        }

        boxes ??= await _desktop.ListTextBoxesAsync(window, ct).ConfigureAwait(false);
        var candidates = boxes.Where(b => !IsBrowser(window, profile) || !IsAddressBar(b)).ToList();
        UiElement? pick = null;
        if (SiteProfile.ForTitle(window.Title) is { } site)
        {
            pick = site.TextBoxNames
                .Select(n => TargetResolver.SortReadingOrder(candidates.Where(b => NameMatcher.Score(n, NameMatcher.CleanElementName(b.Name)) >= NameMatcher.Words)).FirstOrDefault())
                .FirstOrDefault(b => b is not null);
        }

        pick ??= candidates.Count == 1 ? candidates[0] : null;
        if (pick is null)
        {
            var generic = candidates.Where(b => GenericBoxNames.Any(n => NameMatcher.Score(n, NameMatcher.CleanElementName(b.Name)) >= NameMatcher.Words)).ToList();
            pick = generic.Count == 1 ? generic[0] : null;
        }

        if (pick is null)
        {
            Report(StatusKind.Warning, "I couldn't tell where to type. Say “start typing in the … box” first.", heard);
            return (false, null);
        }

        return await FocusBoxAsync(pick, heard, ct).ConfigureAwait(false);
    }

    private async Task<(bool Found, string? Label)> FocusBoxAsync(UiElement box, string heard, CancellationToken ct)
    {
        var focused = await _desktop.FocusTextBoxAsync(box, ct).ConfigureAwait(false);
        if (!focused.Success)
        {
            Report(StatusKind.Warning, focused.Message, heard);
            return (false, null);
        }

        Highlight?.Invoke(box.Rect);
        return (true, box.Name);
    }

    private StepResult BrainPress(BrainRun run, IReadOnlyList<BrainStep> steps, int index, bool confirmed)
    {
        var heard = run.Heard;
        var keys = First(steps[index].Target, steps[index].Text);
        if (BrainSafety.NormalizeChord(keys) is not { } chord)
        {
            Report(StatusKind.Warning, $"I can't press “{Short(keys, 20)}”.", heard);
            return StepResult.Stop;
        }

        if (BrainSafety.RefusalReason(chord) is { } reason)
        {
            Trace?.Invoke("brain: refused keys");
            Report(StatusKind.Warning, $"I won't press {chord}: {reason}.", heard);
            return StepResult.Stop;
        }

        var window = _desktop.GetForegroundWindow();
        if (window is null)
        {
            Report(StatusKind.Warning, "There's no window in front.", heard);
            return StepResult.Stop;
        }

        if (_desktop.IsCommandWindow(window))
        {
            RefuseCommandWindow(window, typing: false, heard);
            return StepResult.Stop;
        }

        // What Enter, Space or a letter does depends on what has the focus: a letter in a box types it.
        var submit = BrainSafety.IsSubmitChord(chord);
        var character = BrainSafety.IsCharacterKey(chord);
        var browser = IsBrowser(window, _apps.FindProfileForProcess(window.ProcessName, window.Title));
        var focus = submit || character ? _desktop.DescribeFocus() : null;
        if (focus is { Editable: true } && RefuseTypingAt(browser, focus, focus.Name, heard))
        {
            return StepResult.Stop;
        }

        if (character && focus is null && browser)
        {
            // It could land in the address bar.
            Trace?.Invoke("brain: refused keys, focus unknown");
            Report(StatusKind.Warning, $"I couldn't tell where {chord} would go, so I didn't press it.", heard);
            return StepResult.Stop;
        }

        if (!confirmed && submit && SubmitQuestion(run, window, chord, focus) is { } question)
        {
            return AskFirst(run, steps, index, window, question);
        }

        if (!confirmed && BrainSafety.NeedsConfirmation(chord))
        {
            return AskFirst(run, steps, index, window, $"Press {chord}?");
        }

        if (!CheckField(heard, null, allowUnknown: true))
        {
            return StepResult.Stop;
        }

        if (character && focus is { Editable: true })
        {
            // Counted like type_text, so Enter in that box later asks first.
            run.TypedChars++;
            if (!BrainSafety.IsSearchBox(focus.Name))
            {
                run.MessageTypedIn.Add(window.Handle);
            }
        }

        _desktop.PressKeys(chord);
        _segments.Clear();
        _typedTail = null;
        if (TabChangingChords.Contains(chord))
        {
            Unbind();
        }

        Report(StatusKind.Success, $"Pressed {chord}.", heard);
        return StepResult.Done;
    }

    /// <summary>
    /// Whether Enter, Space or Alt+S needs a “yes” first: after the plan typed a message in this window (“Send it?”), in
    /// any text box that isn't a search box (it may hold a draft), on a button that sends, deletes or runs something, and
    /// when what has the focus is unknown. Null when it may go ahead.
    /// </summary>
    private string? SubmitQuestion(BrainRun run, WindowRef window, string chord, FocusInfo? focus)
    {
        if (run.MessageTypedIn.Contains(window.Handle))
        {
            return "Send it?";
        }

        if (focus is null)
        {
            return $"Press {chord}?";
        }

        var name = Short(NameMatcher.CleanElementName(focus.Name), 30);
        if (focus.Editable)
        {
            return BrainSafety.IsSearchBox(focus.Name) ? null : name.Length > 0 ? $"Press {chord} in “{name}”?" : $"Press {chord}?";
        }

        return TargetResolver.IsDestructive(focus.Name) || BrainSafety.IsSendLike(focus.Name) || BrainSafety.RunsProgram(focus.Name)
            ? $"Press {chord} on “{name}”?"
            : null;
    }

    /// <summary>
    /// Typing (type_text, or a letter key into a box) goes only into the page in a browser, never into the browser's own
    /// address, search or find bar (they open file:, local and script addresses), nor into a terminal inside an app.
    /// </summary>
    private bool RefuseTypingAt(bool browser, FocusInfo? focus, string? boxLabel, string heard)
    {
        if (focus is { AddressBar: true } || (browser && focus is not { InPage: true }))
        {
            Trace?.Invoke("brain: refused typing outside the page");
            Report(StatusKind.Warning, "I only type into the page, never the browser's address bar. Ask me to open the page or search instead.", heard);
            return true;
        }

        if (BrainSafety.IsCommandBox(boxLabel) || BrainSafety.IsCommandBox(focus?.Name))
        {
            Trace?.Invoke("brain: refused typing in a command box");
            Report(StatusKind.Warning, "I don't type where text runs as a command. Say “start typing” to dictate there yourself.", heard);
            return true;
        }

        return false;
    }

    /// <summary>A web browser, whether or not the app list has it (Brave, Opera, Vivaldi…).</summary>
    private static bool IsBrowser(WindowRef window, AppEntry? profile) => profile?.AddressBarKeys is not null || BrainSafety.IsBrowser(window);

    private void RefuseCommandWindow(WindowRef window, bool typing, string heard)
    {
        Trace?.Invoke(typing ? "brain: refused typing in a command window" : "brain: refused keys in a command window");
        var explorer = string.Equals(window.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        Report(StatusKind.Warning, (explorer, typing) switch
        {
            (true, true) => "I don't type in File Explorer: it can start programs. Say “start typing” to dictate there yourself.",
            (true, false) => "I don't press keys in File Explorer: they can start programs or delete files.",
            (false, true) => "I don't type into command windows. Say “start typing” to dictate there yourself.",
            _ => "I don't press keys in command windows.",
        }, heard);
    }

    private StepResult AskFirst(BrainRun run, IReadOnlyList<BrainStep> steps, int index, WindowRef window, string question)
    {
        _brainPending = new BrainPending(steps, index, run, window, _options.Clock());
        if (Mode == InteractionMode.Command)
        {
            // The whole wait is available for the answer.
            _awakeSince = _options.Clock();
        }

        Trace?.Invoke($"brain: {BrainPlan.NameOf(steps[index].Tool)} waits for yes");
        Report(StatusKind.Warning, $"{question} Say “yes” to go ahead, or “no”.", run.Heard);
        return StepResult.Waiting;
    }

    /// <summary>
    /// The windows (front first) and, for a few of them, what can be clicked or typed into: the screen text the model
    /// plans from. Never logged.
    /// </summary>
    private async Task<(string Text, int Scanned, int Items)> DescribeScreenAsync(string request, CancellationToken ct)
    {
        var windows = (await _desktop.ListWindowsAsync(ct).ConfigureAwait(false)).Take(ScreenSummary.MaxWindows).ToList();
        if (windows.Count == 0 && _desktop.GetForegroundWindow() is { } front)
        {
            // Platforms without a window list: at least the window in front.
            windows.Add(new DesktopWindow(front, WindowShow.Normal, _desktop.GetWindowRect(front)));
        }

        var toScan = ScreenSummary.ChooseWindowsToScan(windows, request, BrainAppName);
        var scans = await Task.WhenAll(toScan.Select(async w =>
        {
            var clickable = await _desktop.ListClickableAsync(w.Window, ct).ConfigureAwait(false);
            var boxes = await _desktop.ListTextBoxesAsync(w.Window, ct).ConfigureAwait(false);
            return (w.Window.Handle, Items: (IReadOnlyList<UiElement>)[.. clickable, .. boxes]);
        })).ConfigureAwait(false);
        var items = new Dictionary<nint, IReadOnlyList<UiElement>>();
        foreach (var (handle, found) in scans)
        {
            items[handle] = found;
        }

        return (ScreenSummary.Format(windows, items, request, BrainAppName), toScan.Count, items.Values.Sum(i => i.Count));
    }

    private string BrainAppName(WindowRef window) => AppLabel(window, _apps.FindProfileForProcess(window.ProcessName, window.Title));

    private static string First(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? string.Empty;
}
