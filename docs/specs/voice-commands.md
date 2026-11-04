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

### Keys and shortcuts (Command mode)

| Say | Keys |
|---|---|
| “press / hit / tap enter, tab, escape, backspace, delete, space, up, down, left, right, page up/down, home, end, shift tab” | That key |
| “select all”, “copy”, “paste”, “cut”, “undo”, “redo”, “save” | Ctrl+A/C/V/X/Z/Y/S |
| “new tab”, “close tab”, “next tab”, “previous tab” | Ctrl+T / Ctrl+W / Ctrl+Tab / Ctrl+Shift+Tab |
| “go back”, “go forward”, “refresh”, “scroll down/up” | Alt+Left / Alt+Right / F5 / Page Down/Up |

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
