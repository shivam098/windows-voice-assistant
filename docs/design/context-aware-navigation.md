# Context-aware navigation: design

> **Design note, 2026-11-05.** Implemented in v0.5.0. The specification is [docs/specs/navigation.md](../specs/navigation.md); this note keeps the reasoning and the evidence that shaped it. Where they differ, the spec wins.

Builds on [voice-click.md](voice-click.md) (clicking by name).

## The goal

Move around the computer by voice the way you'd tell a person: switch app, pick a tab, click something, then type into the right box. Each command builds on where you already are.

```text
"Hey Jarvis, open Chrome"            → Chrome comes to the front
"go to the first tab"                → tab 1 is selected
"start typing"                       → cursor goes into that page's main text box; you dictate
"go to the YouTube tab"              → the tab whose title says YouTube, in any Chrome/Edge window
"start typing in the search box"     → YouTube's search box; you dictate
"start typing in YouTube"            → both steps in one: go to the YouTube tab, then its search box
"switch to GitHub Copilot"           → the Copilot app comes to the front
"click New on the left"              → clicks *New* in the left pane
"start typing"                       → the Copilot message box; you dictate
"send"                               → sends
```

## What exists today vs. what's new

| Already works (v0.4.0) | New |
|---|---|
| Open / switch to an app by name ("open Chrome", "switch to Copilot") | Pick a **tab** by position ("first tab", "last tab") or by name ("the YouTube tab") |
| "next tab", "previous tab", "new tab" (keyboard shortcuts) | **Click** an element by name, with a hint ("New on the left") |
| "start typing" into the app's known text box (Copilot message box, Notepad, browser page/address bar) | "start typing **in X**", where X is an app, a tab, or a field ("search box", "message box") |
| "go to the search box" / "address bar" | A **current place** that each command builds on, shown on the pill |

## Measured on this laptop (read-only UI Automation scans)

| What | Result |
|---|---|
| Chrome tabs | 6 tabs, titles and the selected tab, found in **28 ms** |
| Edge tabs | 20 tabs found in **35 ms** |
| Text boxes in the active Chrome page | 5 (address bar and the page's inputs), found in **24 ms** |
| Microsoft Copilot app | 65 named clickable elements incl. `Library`, `Home`, `New project`; 68 ms |
| GitHub Copilot app | 122 incl. `New`, `Pull requests`, `Issues`, `Sessions`; 473 ms (needs caching) |

Tab titles need cleaning before matching. Chrome adds extras, e.g. `(53) WhatsApp - Pinned - Memory usage - …`, so the notification count, `Pinned`, `Memory usage …` and `- Google Chrome` must be stripped.

## The model: places

Everything you can go to is a **place**, nested:

```text
App window  (Chrome, GitHub Copilot, Notepad …)
 └─ Tab       (browsers only: "YouTube", "WhatsApp" …)
     └─ Element (button, link, list item: "New", "Library", "Pull requests")
         └─ Text box (search box, message box, address bar, document)
```

The assistant keeps a **current place**: the deepest place you last went to that is still in front. Each new command resolves relative to it:

| You say | Resolved against |
|---|---|
| "start typing" | the current place: its text box, or the app's main box |
| "first tab" | the current browser window; if you're not in a browser, the browser you used last |
| "click New" | the current window |
| "go to YouTube" | everything open: tabs first, then apps; if nothing matches, offer to open it |

If you change windows with the mouse or keyboard, the current place follows the foreground window, so the assistant never acts on a stale target.

The pill always shows where you are: **Chrome › YouTube › Search box**.

## Resolving a name ("YouTube", "Copilot", "New")

One resolver, tried in this order. The first single clear match wins:

1. **Element in the current window**, for click verbs ("click New", "select Library").
2. **Tab** in any browser window: cleaned title or a known site alias (YouTube, Gmail, WhatsApp, GitHub). The front window wins ties.
3. **Open app window**: the existing app matching (fuzzy, learned aliases, usage counts).
4. **Installed app**: launch it (the existing "open").
5. **Website**: "open YouTube" with no tab open goes to youtube.com in the current browser. This needs a short built-in site list; ask first for anything else.

Ambiguity is never guessed:
- Two "YouTube" tabs: the pill lists them ("1 YouTube – Lofi beats · 2 YouTube Music"), and you say "one" or "two".
- Two "New" buttons: numbers appear on just those, as in the click design.
- "on the left / right / top / bottom" and "first / second / last" narrow the choice by position.

## "start typing [in X]": choosing the text box

1. **X given:** resolve X with the resolver above. If it's a field name ("search box", "message box", "address bar", "comment box"), match a text box by name in the current place.
2. **Known app or site:** use its profile. Copilot → message box at the bottom. Notepad → document. YouTube / Google → search box. WhatsApp → message box of the open chat. The existing app profiles (`textArea`, `searchKeys`) grow per-site entries matched on tab title.
3. **Generic page or app:**
   - A text box that already has focus → use it.
   - Otherwise, if there's exactly one visible text box → use it.
   - Otherwise, one named like *Search* / *Message* / *Comment* / *Ask* → use it.
   - Otherwise, if the box near the bottom is clearly the chat input → use it.
   - Otherwise show numbers on the candidates.
4. Never type into password fields or elevated windows. These existing safety checks stay.

Then dictation works as today. "send" presses the app's or site's submit key: Enter, or the site profile's key.

## How actions are performed

| Action | Primary method | Fallback |
|---|---|---|
| Switch app | Existing activate-window logic | Alt+Tab-style focus trick |
| Select a tab | UI Automation `SelectionItem.Select` on the tab | Ctrl+1…8 / Ctrl+9 (last) for positions, then verify the title |
| Click element | `Invoke` / `Select` / `ExpandCollapse` | Real mouse click at the element's centre, then the cursor is put back |
| Focus text box | UI Automation `SetFocus` | Click inside it; for search boxes the site's shortcut (`/` on YouTube, `Ctrl+L` for the address bar) |

Every action is **verified**: after selecting a tab, the selected tab's title must match; after focusing, the focused element must be the chosen box. If not, the pill says what happened, and nothing gets typed into the wrong place. This uses the same idea as today's field checks.

## Walkthroughs

**A. Chrome → first tab → type**
1. "Hey Jarvis, open Chrome": Chrome is already running, so it comes to the front. Place = Chrome.
2. "go to the first tab": select tab 1 (WhatsApp); verify. Place = Chrome › WhatsApp.
3. "start typing": the WhatsApp profile gives the message box of the open chat. If no chat is open, ask "Which chat?" or fall back to the search box, which needs a decision. Dictation starts.

**B. YouTube**
1. "go to the tab where YouTube is open": strip the filler ("the tab where … is open" → tab "YouTube"); select it.
2. "start typing in the search box": YouTube's search box (by name, else `/`); dictate "lofi music"; "search" presses Enter.
3. Or in one go: "start typing in YouTube" does both steps.

**C. Copilot**
1. "switch to GitHub Copilot": the app comes to the front.
2. "click New on the left": the `New` button in the left pane (x ≈ 15). The "left" hint resolves the duplicate list-item/button pair.
3. "start typing": Copilot's message box; dictate; "send".

## Effort

| Phase | Scope | Effort |
|---|---|---|
| A. Places + tabs | Current place and pill path, tab listing and cleaning, tab by position/name, "start typing in X", generic text-box choice, verification | ~3 days |
| B. Click by name | From the click design, with position hints | ~2 days |
| C. Site profiles | YouTube, Google, Gmail, WhatsApp Web, GitHub, Teams, Outlook web: text box + send key | ~1 day, then grows with feedback |
| D. Numbers overlay | For ambiguity and unnamed elements | ~1–2 days |

Test with a fixed script against real apps (Chrome, Edge, both Copilot apps, YouTube, WhatsApp Web), extending the existing `--self-test` runner. Measure correct-target rate, wrong-target rate (must be ~0), and end-of-speech-to-action time (target under 300 ms).

## Open questions

1. With no chat open in WhatsApp Web, should "start typing" pick the search box, or ask?
2. Should "open YouTube" with no YouTube tab open the site automatically, or ask first?
3. When two browsers are open (as here: Chrome and Edge), should "first tab" mean the browser in front, or the one used last? (Proposed: the one in front, else the one used last.)
4. Should places be remembered across apps, so "go back" returns to the previous place (app *and* tab)?
