# Voice click: design

> **Design note, 2026-11-05.** Implemented in v0.5.0. The specification is [docs/specs/navigation.md](../specs/navigation.md); this note keeps the reasoning and the evidence that shaped it. Where they differ, the spec wins.

Written after trying Windows Voice Access in the Copilot app.

## The problem we saw

In the Copilot app, saying **"select Library"** to Windows Voice Access did nothing useful: the words were typed into the message box.

Why: in Voice Access, *select X* means "select the text X in the focused text box". Its verb for buttons and links is *click X*. When no text "Library" existed in the box, it fell back to dictation. So the failure was the **command grammar**, not the app.

Evidence: a read-only UI Automation scan of the open windows on this laptop.

| Window | Elements | Scan time | Visible, named, clickable | Examples |
|---|---|---|---|---|
| Microsoft Copilot (`copilotapp`) | 280 | 68 ms | 65 | `Library` (link, *Invoke*), `Home` (tab, *Select*), `New project` (button) |
| GitHub Copilot (`github`) | 1,502 | 473 ms | 122 | `Pull requests`, `Issues`, `New` (buttons, *Invoke*), `Sessions` (*Expand/Collapse*), session list rows (*Select*) |

So the targets are reachable. The scan also exposed real-world problems the design must handle:

- **Duplicates:** `Pull requests` appears twice (a list item wrapping a button with the same name).
- **Lying visibility:** hidden web content reports `IsOffscreen = false` with coordinates like x = −11,523, so "visible" must be checked against the window's own rectangle.
- **Speed:** 0.5 s to read 1,500 elements is too slow to do on every command.
- **Noisy names:** `New project, Ctrl + Alt + Shift + N`, `Resolving an Unspecified Issue, unread`.

## How it would feel

| You say | What happens |
|---|---|
| "Hey Jarvis, click Library" / "open Library" / "**select** Library" / "go to Library" | Clicks Library. Any of these verbs work, because "select" is what people naturally say. |
| "Hey Jarvis, open pull requests" | Clicks *Pull requests* in the left pane. |
| "Hey Jarvis, click New" (two *New* buttons) | Numbers appear on just those two → "two". |
| "Hey Jarvis, show numbers" | Numbers on every clickable thing → "click 14". |
| "Hey Jarvis, expand Sessions" | Uses the element's expand action. |
| "Hey Jarvis, open the second session" | Picks by position within the list. |
| "Hey Jarvis, scroll down" / "go back" | Existing keyboard-style commands. |
| While typing (red pill): "click Library" | **Typed**, unless said as "Hey Jarvis, click Library". The rule we already use keeps dictation and commands apart. |

After every click the pill confirms: **"Clicked Library"**. Destructive targets (*Delete*, *Merge*, *Remove*, *Discard*, *Close without saving*) ask first: "Merge pull request? Say *yes* to confirm."

## How it works

```text
"Hey Jarvis, click Library"
   │
   ▼
CommandParser ── verb (click/select/open/go to/tap/press/choose/expand) + target phrase
   │
   ▼
ScreenIndex (foreground window)
   ├─ cached UI Automation snapshot, refreshed when the window or focus changes
   ├─ keep: named, enabled, inside the window rectangle, actionable (Invoke / Select / Expand / Toggle)
   ├─ merge duplicates (same name + overlapping rectangle → keep the actionable one)
   └─ clean names: drop shortcuts ("…, Ctrl + Alt + Shift + N") and states (", unread")
   │
   ▼
Matcher ── exact > word-prefix > fuzzy (same Levenshtein as app names) > learned alias
   │        + hints: "on the left", "in the sidebar", "second", "first"
   ├─ one clear winner ─► Act
   ├─ several ─► number only those candidates ─► "two"
   └─ none ─► OCR fallback (Windows' built-in on-device OCR on a window screenshot) ─► click the text's location
   │
   ▼
Act ── Invoke / SelectionItem.Select / ExpandCollapse / Toggle
       └─ fallback: real mouse click at the element's centre (then put the cursor back)
```

### Making it fast

- Read the tree once per window with a UI Automation *cache request* that fetches only name, type, rectangle, enabled state and patterns in one cross-process call. This is typically several times faster than walking element by element; to be measured.
- Refresh in the background on focus/window change, so a command usually hits a warm cache (target under 150 ms from end of speech to click).
- Only rescan the subtree under the matched element if it disappeared.

### Content-aware (later)

For commands that need understanding rather than a name, e.g. "open the PR Ankit raised yesterday" or "reply to the last comment":

- Send a compact text view of the screen (cleaned names, roles, positions; no screenshot) to a language model, which returns the element to act on.
- **Local first** (a small on-device model) to keep the "nothing leaves your laptop" promise; cloud models only as an explicit opt-in.
- The model only *chooses*; the same Act step and confirmations do the clicking.

## Safety and privacy

- Never act on elevated (administrator) windows or the secure desktop. Windows blocks it anyway; say so clearly.
- Confirm destructive targets; never chain more than one click per command without confirmation.
- Logs record the action type and timing only, never element names or screen text (same rule as dictation).
- Respect the existing pause and the pill. A visible "Clicked X" makes every action auditable.

## Build plan

| Phase | Scope | Effort |
|---|---|---|
| 1. Click by name | Verbs, ScreenIndex with caching and de-duplication, matcher, Invoke/Select/Expand, mouse fallback, "Clicked X" confirmation, destructive-click confirmation | ~2 days |
| 2. Numbers | "show numbers" overlay, numbering only ambiguous matches, "click N", "hide numbers" | ~1–2 days |
| 3. Fallbacks | OCR for apps without accessibility names; mouse grid for everything else | ~2 days |
| 4. Content-aware | On-device language model picking targets from the screen text view; opt-in cloud | 1–2 weeks to prototype |

Measure phase 1 against a fixed script in the two Copilot apps, Chrome, Edge, File Explorer, Outlook and Teams. Track: correct click rate, wrong-click rate (must be ~0), and end-of-speech-to-click latency.

## Open questions

1. Should plain "Library" (no verb) click when the word exactly matches one visible button? It's faster, but there's a higher risk of accidental clicks.
2. After the wake word, should click commands also work without "Hey Jarvis" for the 30-second window, like other commands? (Proposed: yes.)
3. Phase 4 model choice: an on-device model small enough to answer in under a second on this laptop vs. an opt-in cloud model.
