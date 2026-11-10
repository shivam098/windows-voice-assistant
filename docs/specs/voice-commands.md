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

## Feedback

Every command reports success or a reason on the pill. Unknown phrases in Command mode: “Not a command I know. Say it
another way and I'll remember.” In Standby: only commands get a hint (“Say ‘Hey Jarvis’ first”); other speech is
silent.
