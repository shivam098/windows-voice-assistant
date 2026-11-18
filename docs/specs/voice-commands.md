# Voice command specification

The grammar the assistant understands. The executable reference is `CommandParser` (`src/VoiceAssistant.Core`) and
its tests; navigation phrases are specified in [navigation.md](navigation.md).

## Matching rules

1. The recogniser's text is **normalised**: lowercase, punctuation removed, hyphens and apostrophes joined
   (“Co-Pilot.” → “copilot”, “Let's” → “lets”).
2. A leading **wake word** is stripped and noted (“hey jarvis open chrome” → “open chrome”, woke = true).
3. **Polite padding** is stripped repeatedly from both ends (lenient matching only):
   prefixes like “can you (please)”, “could you”, “would you”, “please”, “i want to”, “i need to”, “lets”, “go ahead and”,
   “now”, “just”, “okay”, “so”, “and”, “then”, “um”, “uh”; suffixes like “please”, “for me”, “right now”, “thanks”.
4. The **whole remaining utterance** must match a command. Nothing is matched inside a longer sentence.
5. **Learned phrases** (see [product.md](product.md#learning)) are tried when nothing matches.

## Strict vs. lenient

| Situation | Matching |
|---|---|
| Command mode, or after the wake word | **Lenient**: padding stripped; all commands below |
| Typing mode without the wake word | **Strict**: only the exact phrases in the “While typing” table; everything else is typed |
| Typing mode with the wake word | Lenient typing commands first, then every command (navigation leaves Typing mode first) |

“Post it.”, “That's all.”, “Send me the logs” are typed while dictating; only a standalone “Send.” sends.

## Commands

### Apps

| Say | Does |
|---|---|
| “open *app*”, “launch *app*”, “start *app*”, “fire up *app*”, “pull up *app*” | Opens the app. Apps with a new-window key (Notepad) open a **fresh window**, never the open document |
| “switch to *app*”, “go to *app*”, “go back to *app*”, “bring up *app*”, “show *app*” | Brings the app's window to the front (launches it if needed) |
| “… and start typing” | Then starts typing in it |

App names are matched against configured apps, then Start-menu apps: exact name or alias, then without vendor
(“Microsoft Outlook (new)” = “outlook”), then a prefix (≥ 4 letters), then 1–2 letter slips (“notpad”, “crome”).
Ties go to configured apps, then to the clearly most-used app; otherwise “Which one? …”.
**Navigation extends “go to” / “open”** to tabs and on-screen buttons; see [navigation.md](navigation.md#resolution-order).

### Typing

| Say | Does |
|---|---|
| “start typing”, “start dictation”, “type”, “let me type”, “start writing”, “dictate”, … | Typing mode in the front app's text box (see [navigation.md](navigation.md#choosing-the-text-box)) |
| “start typing in *place*” | Go there, then type (see navigation) |
| “go to the search box”, “search box”, “click the search bar”, “type in the search box” | Focus the search box (browsers: address bar) and type there |
| “go to the address bar” | Browsers |
| “search for *query*”, “google *query*”, “look up *query*” | Focus search, type, Enter |

### While typing (strict phrases)

| Say | Does |
|---|---|
| “send”, “send it”, “submit”, “send message”, “search” | Press the app's send key (Enter; none for Notepad), back to Command mode |
| “scratch that”, “undo that”, “delete that” | Delete the last typed phrase |
| “clear box”, “clear the box”, “clear text”, “clear all”, “clear message” | Select all + delete |
| “new line”, “next line” / “new paragraph” | Line break(s) with the app's new-line key |
| “stop typing”, “stop dictation”, “command mode”, “stop writing” | Back to Command mode, nothing sent |
| “pause listening”, “stop listening”, “go to sleep”, “pause” | Microphone off |
| “press *key*” | Press it |

### Editing what you typed

Works while typing (no wake word needed for these exact forms) and, after the wake word, in the focused text field.
The text before the cursor is read with the platform's accessibility API (Windows: UI Automation TextPattern, else
the field's value with the cursor assumed at the end), and the edit is made with keys: Left over the text after the
change, Backspace, type, Right back. Edits never reach more than 3,000 characters back.

| Say | Does |
|---|---|
| “delete the last word”, “delete the last three words”, “delete 3 words”, “delete the last few words” (3), “…couple of…” (2) | Remove that many words before the cursor (a word keeps its punctuation: “world.”), plus the space before them |
| “delete the last sentence”, “erase the previous two sentences”, “delete that sentence” | Back to the previous “. ”, “? ”, “! ” or line break |
| “delete the last line”, “delete the last paragraph” | The whole line (and the line break before it) / back to the previous blank line |
| “delete the last 5 characters / letters” | Characters (emoji count as one) |
| “delete the word *very*”, “remove the words *as soon as possible*” | The last whole-word match before the cursor, with one neighbouring space |
| “replace the word *Monday* with *Tuesday*” | Replace the last match; the replacement keeps what was said (“GitHub Actions”) and a capital at the start of a sentence |
| After the wake word: “replace *X* with *Y*”, “change *X* to *Y*”, “delete *X*” | Same, without “the word” (while typing without the wake word these are ordinary sentences) |

“scratch the last line”, “undo the last word” also work. Counts are 1–50. If the text can't be read, “delete the last N
words” falls back to Ctrl+Backspace and “… characters” to Backspace (only while typing); anything else says the box
can't be read.

Safety: nothing is edited while text is selected (typing would replace the selection), in read-only text (pages,
terminal output), or — outside typing — in a field that couldn't be checked. When a field exposes only its whole value
(no cursor position), it is trusted only while it still ends with what was just typed; a cursor moved by clicking
without typing can't be detected there.

### Keys and shortcuts (Command mode)

| Say | Keys |
|---|---|
| “press / hit / tap enter, tab, escape, backspace, delete, space, up, down, left, right, page up/down, home, end, shift tab” | That key |
| “select all”, “copy”, “paste”, “cut”, “undo”, “redo”, “save” | Ctrl+A/C/V/X/Z/Y/S |
| “new tab”, “close tab”, “next tab”, “previous tab” | Ctrl+T / Ctrl+W / Ctrl+Tab / Ctrl+Shift+Tab |
| “go back”, “previous page”, “go to the previous page”, “go back one page”, “take me back” | Alt+Left (browser and File Explorer back) |
| “go forward”, “next page”, “go to the next page” | Alt+Right (forward). A page's own “Next” link is “click next” |
| “go up”, “go up one level”, “parent folder”, “go to the parent folder” | Alt+Up (File Explorer: parent folder) |
| “refresh”, “scroll down/up” | F5 / Page Down/Up |
| *Note* | These exact phrases win over “go to *app*”: “go to the next page” is never treated as an app called “next page”. |

Platforms map these to their own shortcuts (macOS: Cmd instead of Ctrl, Cmd+[ / Cmd+] for back/forward, Cmd+R).

### Listening

| Say | Does |
|---|---|
| “Hey Jarvis” | Wake (tone); Command mode for 30 s |
| “pause listening”, “stop listening”, “go to sleep”, “be quiet” | Microphone off |

### Idea board

Notes, to-dos and reminders (“add to my ideas, …”, “remind me to … at 5 pm”, “show my reminders”, “mark … as done”).
Checked **before** the other commands. Full grammar: [idea-board.md](idea-board.md).

### Anything else: the AI brain

When the AI brain is on (Settings › AI brain; it runs on this PC), a request the commands above don't match goes to it
instead of “Not a command I know”: “open the MrBeast video on YouTube”, “mute the video”, “reply thanks to Rahul on
WhatsApp”, “what's the weather in Bangalore”, “tell me a joke”. So does a single “open / go to / click *X*” whose *X*
isn't an app, tab or on-screen name (“open the MrBeast video on YouTube” was read as an app called “the MrBeast video
on YouTube”). While the brain is on, a description of three or more words needs a strong tab match: “open the MrBeast
video on YouTube” no longer just switches to the YouTube tab.

Only speech **addressed to Jarvis** goes to the brain: said after the wake word (“Hey Jarvis, mute the video”), or as
the next phrase after “Hey Jarvis” on its own, or any phrase when the wake word is switched off. Conversation in the 30
seconds after a command is never planned and acted on (it gets “Start with ‘Hey Jarvis’ to ask the AI brain…”), and
unclear speech (below `minCommandConfidence`) is ignored like an unclear command.

1. The pill shows “Thinking…” (“Waking up the AI brain…” while the model loads).
2. The brain gets the request as said (wake word removed) and a **screen summary**: the open windows front to back
   (title, app, left/right half, maximized, minimized), and what can be clicked or typed into in up to two of them
   (the front one, then the one the request names, else the next). Busy pages are cut to 14 items per window, keeping
   those that share words with the request, text boxes and content-like names (titles, messages, files), so the prompt
   stays near 300 tokens.
3. It answers with a **plan**: up to six steps with these tools, plus one sentence to show.

| Tool | Runs as |
|---|---|
| `focus_window(title)` | Brings that window to the front (restores it if minimized); else a tab of that name; else opens the app |
| `open_app(name)` | “open *app*” (a fresh Notepad window, etc.) |
| `open_url(address)` | Opens the page in the default browser, then waits for it to come to the front |
| `web_search(site, query)` | YouTube, Bing, Wikipedia, GitHub or Google Maps results page; Google for anything else |
| `click(name)` | Clicks that item in the front window (a strong name match; text boxes get the cursor); ties show numbers |
| `type_text(text)` (optional box name) | Types into that box, else the focused box, else the page's main box; the box then counts as the typing target, so “send it” and “scratch that” work on it. In a browser only into the page, never the browser's own address, search or find bar |
| `press(keys)` | Presses keys like Ctrl+W, Win+Up, M; a letter pressed while a text box has the focus counts as typing |
| `scroll(up/down)` | Page Up / Page Down |
| `ask(question)` | Shows the question; nothing runs |

Steps run in order; any step that fails stops the plan with the reason, and the plan's sentence shows at the end. A
page that opens without its browser coming to the front stops the plan there (later steps would act on the wrong
window). Questions and chat (“how are you”) are answered in the sentence with no steps.

**Asks first** (“Click ‘Delete’? Say ‘yes’ to go ahead, or ‘no’.”, within 30 s, same window): destructive buttons (the
same list as clicks); buttons that send, post, repost, submit, share, install, run, allow or agree; files that start a
program (.exe, .bat, .ps1, .js, .msi…), and any file in File Explorer or on the desktop (Windows hides known
extensions); once the plan has typed into a box that isn't a search box, **any** click, Enter or Space in that window
(“Send it?”); Enter (with any modifier), Space or Alt+S in any other box that isn't a search box (it may hold your
draft), on a focused button that sends, deletes or runs something, or when what has the focus can't be read; Alt+F4,
Delete, Ctrl+D, Ctrl+Q, Ctrl+Shift+W and Alt+letter (menu and ribbon keys); and more than 200 characters typed in all.
“yes” runs that step and the rest; “no” or any other command drops them.

**Never**: typing or keys in terminals, PowerShell, cmd, the Run box, File Explorer and the desktop, Task Manager,
PowerToys Run, the Start menu's search or the Registry Editor, nor typing into a terminal or console inside an app (VS
Code's); in a browser, typing anywhere but the page (its address bar in any language: pages open only through open_url
and web_search, which check the address); typing a local, numeric or non-web address into any search box; Win
shortcuts other than Win+arrows, Win+D and Win+M, Ctrl+Esc, clipboard keys (Ctrl+C, Ctrl+X, Ctrl+V: it types instead)
and menu keys (Alt, F10, Shift+F10, Alt+Space: it clicks menu items by name instead); links other than http(s) pages on
a public host name (no files, scripts, settings, localhost, `.local`, `.lan` or numeric addresses); more than 8 steps
or 30 seconds. **Esc** stops a plan at once (listening carries on; Esc is read by the Ctrl+Win dictation key hook, so
it needs `pushToTalk` on, the default).

If the model fails, times out (60 s) or answers something that isn't a plan, the pill says so and a rephrasing that
works is still learned. With the brain off, nothing changes; with it on but not downloaded, unknown phrases also say
where to get it.

## Feedback

Every command reports success or a reason on the pill. Unknown phrases in Command mode: “Not a command I know. Say it
another way and I'll remember.” (or, with the AI brain on, its answer; see above). In Standby: only commands get a hint
(“Say ‘Hey Jarvis’ first”); other speech is silent.
