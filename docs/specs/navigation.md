# Navigation specification

Move around the computer by voice: switch apps, pick browser tabs, click things by name, and start typing in the
right box. Each command builds on where the user already is.

Executable reference: `src/VoiceAssistant.Core/Navigation.cs` (cleaning, matching, resolving),
`NavigationGrammar.cs` (phrases), `InteractionController.Navigation.cs` (behaviour), and
`tests/VoiceAssistant.Core.Tests/NavigationTests.cs`. Real-app acceptance: `tests/NavigationLab`.

## Example

```text
"Hey Jarvis, open Chrome"          → Chrome comes to the front                    📍 Google Chrome
"go to the first tab"              → tab 1 of the front Chrome window             📍 Google Chrome › WhatsApp
"go to the tab where YouTube is open"                                             📍 Google Chrome › Lofi beats - YouTube
"start typing"                     → YouTube's search box; dictation starts       📍 … › Search
"lofi music"  →  "search"          → typed, Enter
"switch to GitHub Copilot"         → app to the front
"click New on the left"            → the New button in the left pane (not the one on the right)
"start typing"                     → Copilot's message box                         →  "fix the login test"  →  "send"
```

## Places

Everything the user can go to is a **place**, nested:

```text
App window (Chrome, GitHub Copilot, Notepad …)
 └─ Tab (browsers only)
     └─ Element (button, link, list/tree/menu item, tab, checkbox …)
         └─ Text box (search box, message box, address bar, document)
```

- The assistant remembers the **current place**: the last place a command went to. It is shown on the pill
  (“📍 Google Chrome › YouTube › Search”) with the message that got there.
- Commands always act on the **foreground window**. If the user moves elsewhere with mouse or keyboard, the next
  command follows the new foreground window; nothing acts on a stale place.

## Grammar

All phrases are matched as whole utterances after normalisation and polite-padding removal
([voice-commands.md](voice-commands.md#matching-rules)). Navigation is matched after the core commands, so
“new tab”, “next tab”, “select all”, “click the search box” and “press enter” keep their meaning.

### Tabs

| Say | Target |
|---|---|
| “(go to / switch to / open / select / show / jump to / take me to) the first / second / … / tenth / last tab” | By position in the **front-most browser window** |
| “tab 3”, “tab three”, “tab number 3”, “go to tab 3” | By position |
| “the YouTube tab”, “go to the YouTube tab”, “open the WhatsApp tab” | By name |
| “the tab with / where / that has / showing / for YouTube (is open / open)” | By name |

### Clicking

| Say | Meaning |
|---|---|
| “click / click on / tap / press / hit / select / choose / pick *name*” | Click the element called *name* |
| “expand / collapse / toggle *name*” | Same; the element's own action decides (expand, collapse, toggle) |
| “… on the left (side) / in the left pane / in the sidebar / on the right / at the top / at the bottom” | Region hint |
| “the first / second / last *name*” | Ordinal among matches, in reading order |
| “… button / link / option / menu / item / icon” | Ignored noise words |
| “… and start typing” | Then start typing |

### Start typing somewhere

| Say | Does |
|---|---|
| “start typing in / into / on *app or site*” | Go to it (tab or app, see resolution order), then start typing |
| “start typing in the first tab”, “… in the YouTube tab” | Select the tab, then start typing |
| “start typing in the message / comment / reply … box (field, bar, area, input)” | Focus the text box with that name in the front window |
| “start typing in the search box / address bar” | As the core “go to the search box / address bar” |
| “start typing in it / here / this / the text box” | Plain “start typing” |
| Verbs: start typing, begin typing, continue typing, start writing, start dictating, let me type, type, write, dictate | |

### Numbers, confirmation

| Say | Does |
|---|---|
| “show numbers / labels”, “numbers” | Number everything clickable and every text box in the front window (max 99) |
| “hide numbers”, “clear numbers” | Hide them |
| “3”, “three”, “click 3”, “number 3”, “choose 3”, “pick 3”, “the second one” | Pick a numbered choice |
| “yes”, “yes do it”, “confirm”, “do it”, “go ahead”, “yeah”, “sure” | Confirm the pending destructive click |
| “no”, “cancel”, “never mind”, “don't”, “forget it” | Drop pending choices and confirmations |

Bare numbers and yes/no are commands only in Command mode. While typing they are text (“two” is typed). In Standby
(no wake word) they are ignored silently: “yes”, “no” and numbers are everyday words.

### Leaving typing mode

- While typing, any command said with the wake word other than the typing commands (send, scratch that, new line,
  press *key*, stop typing…) **stops typing first**: “Hey Jarvis, next tab” switches tab and nothing more is typed.
- Offering numbered choices also stops typing, so the number is heard as a choice, not typed.
- After switching app or tab, clicking, choosing, or a tab/back/forward/refresh shortcut, the old typing target is
  **forgotten**: “send” can't press Enter in the page or app that's now there. Say “start typing” again.

## Resolution order

What “go to X” / “open X” / “click X” mean:

| Command | Tries, in order |
|---|---|
| “go to X”, “switch to X” | 1. If X is **exactly** an app's name or alias → that app. 2. A **tab** whose title matches X. 3. An app matching X (fuzzy). 4. A **button/element** in the front window whose name matches X strongly (score ≥ 70). 5. “I couldn't find an app, tab or button called ‘X’.” |
| “open X” | 1. An app matching X (opens a new window where defined). 2. A tab. 3. A strong element match. |
| “click X” (and select, tap, press …) | Elements in the front window only (score ≥ 50). |
| “the X tab”, “first tab” | Tabs only. |

One clear best match acts. Several equally good matches → **choices** (see below). None → a message saying what was
tried, suggesting “show numbers”.

## Names: cleaning and scoring

Accessibility names and tab titles carry extras that must be removed before matching:

| Raw | Cleaned |
|---|---|
| `New project, Ctrl + Alt + Shift + N` | `New project` (shortcut suffix) |
| `Resolving an Unspecified Issue, unread` | `Resolving an Unspecified Issue` (state suffix: unread, selected, expanded, pinned, “Status: …”) |
| Tab `(53) WhatsApp - Pinned - Memory usage - 120 MB` | `WhatsApp` (notification count, status segments) |
| Tab `Lofi beats - YouTube - Google Chrome` | `Lofi beats - YouTube` (browser name) |

Score of spoken phrase *s* against a cleaned name *n* (both normalised):

| Score | Rule | Example |
|---|---|---|
| 100 | Equal | “library” = “Library” |
| 95 | Equal ignoring spaces and a plural “s” | “pull request” = “Pull requests”, “git hub” = “GitHub” |
| 80 − extra words (min 70) | *s* is a word-prefix of *n* | “new” vs “New project” → 79 |
| 70 − extra words (min 60) | Every word of *s* is a word of *n* | “youtube” vs “Lofi beats YouTube” |
| 60 | Every word of *n* is in *s* (user said more) | “the pull requests page” vs “Pull requests” |
| 50 | 1 letter different (4–7 letters) or 2 (8+) | “libary” vs “Library” |
| 0 | Otherwise | |

- **Tabs** score against the whole cleaned title *and* each “ - ” segment, keeping the best; a tab needs ≥ 60.
  (“youtube” vs “Lofi beats - YouTube” = 100 via the segment; vs “YouTube Music” = 79, so the first wins.)
- **Elements** need ≥ 50 for “click”, ≥ 70 for the “go to X” fallback.
- Only the **best score** survives; ties are ambiguous.

## Narrowing

1. **De-duplicate:** the same control is often reported twice (a list item wrapping a button of the same name). Elements
   with the same cleaned name where one's centre lies inside the other collapse to the most clickable role
   (button/link/tab/menu item > tree item/checkbox/radio/combo/text box > list item > other).
2. **Region hint:** left = centre in the left 35 % of the window; right = right 35 %; top = top 30 %; bottom = bottom 30 %.
3. **Ordinal:** the n-th (or last) in **reading order** (top-to-bottom; same row, within half an element's height,
   left-to-right).
4. **Tabs only:** browser tabs are the tab items in the browser's own UI, **outside web content** (a page's own tab
   controls, and the whole page of an installed web app, don't count). Horizontal and vertical tab strips are both
   numbered in reading order. The same name in several browser windows → the front-most window's tab wins; several in
   one window → choices. Positions (“first tab”) always refer to the front-most browser window.

## Choices (numbers)

- Shown when a name is ambiguous, when “start typing” can't tell which box, and on “show numbers”.
- Each candidate gets a number badge on screen (yellow, at its top-left corner); the pill lists tab choices
  (“Which tab? 1 YouTube – Lofi · 2 YouTube Music”).
- The user says the number. Choices last **30 s**, are cleared by any other command (except yes/no/cancel/hide), and
  keep the wake window open while waiting.
- Choosing a **text box** focuses it and starts typing; a **tab** selects it; an **element** clicks it (with the
  destructive check below).

## Destructive clicks

- Names containing (as words) delete, remove, merge, discard, uninstall, erase, format, reset, sign out, log out,
  unsubscribe, deactivate, close account, leave, block, empty trash / recycle bin, pay, buy, purchase, place order,
  send money, transfer, don't save, close without saving, force push, revoke, terminate, shut down, restart
  need confirmation: the element is outlined and the pill says “‘Delete account’ can't be undone. Say ‘yes’ to click it, or
  ‘no’.”
- “yes” as the **very next utterance** within **30 s** clicks it, **only if the same window is still in front** and the
  element still exists with the same name. Anything else said in between drops it.
- A single command never performs more than one destructive click.

## Choosing the text box

When “start typing” runs and nothing editable has focus:

| Front window | Choice |
|---|---|
| App with a profile (Copilot → message box at the bottom; Notepad → largest document) | The profile's box |
| Browser on a known site | The site's box by name (YouTube → “Search”, WhatsApp → “Type a message”, Gmail → “Search mail”, ChatGPT → “Ask anything”…). The site is recognised from a **segment** of the cleaned tab title (“Lofi beats - YouTube” → YouTube; “Amazon.in: Shoes” → Amazon), never from the browser's name (“Google Chrome” doesn't make every page Google) |
| Browser, other page | The page's only text box; else the one named like search/message/comment/reply/ask/chat; else **choices**. The address bar is not a candidate. No boxes → the address bar |
| Other app | Same rule (only box → named box → choices); no boxes → type where the cursor is |

The box is focused, then **verified**: the focused element must be the box (or inside it) and editable, else a real
click in its centre is tried once, else “I couldn't put the cursor in that box. Click it once, then say ‘start typing’.”
Password fields are refused. Focusing a box named like “search” makes it a search field (Enter = search; no trailing
full stop).

## Acting and verifying

| Action | Primary | Fallback | Verification |
|---|---|---|---|
| Select a tab | Bring its window to the front; the tab's *select* action | Its *invoke* action, then a real click on it | Window title shows the tab's title within 1.5 s |
| Click an element | *Invoke*; else *select*; else *toggle*; else *expand/collapse* | Real left click at its centre, then the pointer goes back | Before acting: the matched window is still in front, and the element still exists **with the same name** (else “That changed on screen”). A real click happens only if the point belongs to the target window |
| Focus a text box | *Set focus* | Real click at its centre (same window check) | Focused element is the box and editable |

- An element that disappeared is **never** clicked at its old position. Work that times out (4 s) is cancelled before
  any real click.

- After any successful click, selection or focus, the target is **outlined** for ~1 s (blue).
- Windows running with higher privileges are refused (the OS blocks input to them anyway).
- Scans read only named, enabled, visible elements inside the window's bounds (hidden web content can report far-away
  coordinates as “on screen”). On Windows one cached query per scan keeps it to tens of milliseconds
  (measured: 25 elements 26–47 ms; GitHub Copilot 75 numbered elements; Chrome tabs 26–70 ms).

## Privacy

Element names, tab titles and screen text are used only in memory and on the pill/overlay. Logs record counts, command
kinds, outcomes and timings only (“clickable scan: 25 of 25 in 40 ms”, “click Button ok”).

## Not yet specified (future)

- OCR for apps that expose no accessibility names; a mouse grid.
- “go back” to the previous place (app and tab).
- Content-aware commands (“open the PR Ankit raised yesterday”) via an on-device language model choosing among the
  scanned elements, with the same acting and verification rules.
- Opening a website when no tab matches (“open YouTube” → youtube.com).
