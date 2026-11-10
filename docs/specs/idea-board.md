# Idea board specification

A voice-first board of sticky notes in four lists: **Ideas**, **Brainstorm**, **To-do** and **Reminders**. Notes are
added by voice (push-to-talk or “Hey Jarvis”) or typed in the app window. Reminders with a time pop up when they're
due.

The board is its own project, `src/VoiceAssistant.Board` (no Windows or UI code), so it can move to a separate
repository or app later. Its tests are `tests/VoiceAssistant.Board.Tests`.

## Lists

| List | Spoken names | Notes |
|---|---|---|
| Ideas | “ideas”, “idea list”, “idea board”, “board”, “notes” | Default for “note down …” |
| Brainstorm | “brainstorm”, “brainstorming”, “brainstorming list/board/ideas” | |
| To-do | “to-do”, “to do list”, “todos”, “tasks”, “task list” | |
| Reminders | “reminders”, “reminder list” | May carry a due time |

## Voice grammar

The grammar works on the transcript as spoken; the note keeps the speaker's words (first letter capitalised, trailing
period dropped).

### Explicit list requests: work everywhere

They name a list, so they must not be mistaken for dictation. With **push-to-talk** (`Ctrl`+`Win`) these are filed on
the board **instead of being typed**; while voice typing they need the wake word (“Hey Jarvis, add to my ideas …”) and
typing carries on afterwards.

| Say | Result |
|---|---|
| “Add to my ideas, a voice game for kids” | Ideas: *A voice game for kids* |
| “Put this on my to-do list, renew passport” | To-do: *Renew passport* |
| “Add renew passport to my to-do list” | To-do: *Renew passport* |
| “Save to my brainstorm board: pricing ideas” | Brainstorm: *Pricing ideas* |
| “Add to my reminders, pay rent tomorrow” | Reminders: *Pay rent*, due tomorrow 9:00 |

Verbs: add, put, save, write, note, jot. Prepositions: to, in, into, on, onto, under. Optional “my”, “the”, “our”.
Leading filler is ignored: “and”, “also”, “so”, “um”, “okay”, “can you (please)” (“and add to my reminders, …”). With
push-to-talk, “can / could / would / will you …” means you're asking someone else, so it is typed.

With push-to-talk the list must also be clearly *yours*: “my” or “our” before it, or a name only the board uses (ideas,
brainstorm, reminders). So “put the agenda on the board”, “add these to the tasks” and “please add the action items to
the notes” are typed as dictation, while “put the agenda on my board” is filed.

### Wake-word requests (“Hey Jarvis, …”)

These are common in ordinary speech (“please remind me to send the slides” in a message to a colleague), so
push-to-talk **must** type them; only a command (wake word, or command mode) may treat them as board requests.

| Say | Result |
|---|---|
| “Remind me to call mom tomorrow at 5 pm” / “Set a reminder to …” | Reminders: *Call mom*, due tomorrow 17:00 |
| “Note down that the demo needs subtitles” / “New idea, …” / “I have an idea, …” | Ideas |
| “Show my ideas / reminders / to-do list / brainstorming list” | Opens the window on that list |
| “Show my board” / “Open the idea board” | Opens the whole board |
| “Mark renew passport as done” / “Tick off renew passport” / “Cross pay rent off” | Ticks off the best-matching open note |

Rules:

- In command mode the board is checked **before** app and navigation commands, so “go to my ideas” opens the board
  rather than a browser tab called “ideas”.
- So it doesn't swallow commands meant for the app on screen, it **must not** match:
  - “tick the checkbox”, “complete the form”, “finish typing”, “mark this email as read” (marking done needs “done”
    after “mark”, or “off” with tick/check/cross);
  - “open notes”, “open tasks”, “go to to do” (they may name an app). Showing needs “my” or “the”, or a name only the
    board uses (ideas, brainstorm, reminders, “… list”).
- Ticking off picks the open note whose words cover at least 60 % of what was said (ignoring “the”, “my”, “to”, “a”,
  “an”, “and”); on a tie, the newest. Nothing matched → warning “I couldn't find …”, nothing changes.

### Reminder times

Found anywhere in a reminder and removed from its text. Times are local; a clock time that has passed today means
tomorrow.

| Phrase | Due |
|---|---|
| “in 10 minutes”, “in half an hour”, “in an hour”, “in 2 days”, “in a week” | Now plus that |
| “at 5:30 pm”, “5 pm”, “17:45”, “at noon”, “at midnight” | The next time that clock time comes (tomorrow if it has passed today) |
| “at 5” (no am/pm, no day) | The next 5 o'clock: 5 pm in the afternoon, 5 am tomorrow late at night |
| “tomorrow at 5”, “on Friday at 3” | An hour from 1 to 6 after a day means pm; 7 to 11 means am |
| “tonight”, “this evening”, “this afternoon”, “this morning” | Today 20:00, 18:00, 15:00, 9:00 |
| “in the morning / afternoon / evening”, “at night” | 9:00, 15:00, 18:00, 21:00 (tomorrow if passed); with a clock time, sets am/pm |
| “tomorrow”, “the day after tomorrow” | That day 9:00, or the given time |
| “on Friday”, “next Wednesday” | The next such weekday (a week ahead if it is today), 9:00 unless given |

A bare number needs “at” or am/pm, so “call 3 people” keeps its 3. A reminder without a time is still filed and never
pops up.

## Dictation with nowhere to type

When push-to-talk ends and there's **no text box** to type into, the dictation is saved on the board instead of being
lost. The pill says “No text box, so I saved it. Added to …”.

“No text box” is deliberately narrow, so dictation into apps with unusual text boxes (terminals, editors, spreadsheet
cells, web apps) is never diverted:

- no app window is in front, or the Voice Assistant window itself is;
- the desktop or the taskbar is in front, unless you're typing in it (renaming an icon is still typed);
- focus is on something that positively doesn't take text: a list or list item (files, chats), a tree, a button, a
  link, a menu, a tab, a checkbox. Anything else, including a focus that can't be checked, still gets the text.

Which list it goes to (only clear tasks become to-dos or reminders; everything else is kept word for word as an idea):

| Dictation | List | Note |
|---|---|---|
| Names a list or says “remind me / note down …” | As asked | Same grammar as above |
| A task: “I/we have to / need to / must / should / I've got to …”, “don't forget to …”, “remember to …”, “to-do: …”, or starts with an action verb (call, email, pay, buy, book, send, renew, review, …) | To-do | Lead-in removed: “Draft a reply to Tata Neu” |
| A task with a **future** time (“call the bank tomorrow at 11 am”) | Reminders | Time removed from the text |
| Anything else, even with a time in it (“the demo today went well”, “we met at 10”) | Ideas | As dictated |

Nothing is saved for dictation with no words (“new line” on its own). “Paste last dictation” (`Alt`+`Shift`+`Z`)
never files to the board.

With push-to-talk into a text box, a request that starts “can / could / would / will you …” is always typed: it's
addressed to someone else.

## Storage

- One JSON file, `%APPDATA%\VoiceAssistant\board.json`. Saved after every change by writing a temporary file and
  renaming it, so a crash never leaves half a file.
- An unreadable file is kept as `board.json.unreadable-<time>` and a fresh board starts; notes are never silently lost.
- Item fields: id, list, text, created, due, done, notified.
- Notes stay on the device. Logs record only the list and whether it has a time (`board add Todo with time`), never
  the note text.

## App window

The Windows app has a window (Home, Idea board, Settings, What you can say). Closing it keeps the assistant running in
the tray; Exit is in the tray menu. Signing in starts it in the tray (`--background`); starting it again (Start menu)
opens the running copy's window.

The **Idea board** page shows one column per list:

- An “Add a note…” box on each column (Enter adds; the Reminders box understands the same times as voice).
- Each note: a done checkbox, its text, its due time (red when overdue). Double-click to edit; right-click to edit,
  move to another list, snooze (reminders) or delete.
- “Show done” (on by default) also lists ticked-off notes, below the open ones; turn it off to hide them.
- “Show my reminders” (and the other lists) opens the window and briefly outlines that column.

## Reminder pop-ups

- Checked every 15 seconds and once at start-up, so reminders that came due while the app was closed still appear.
- Each due reminder shows a card in the bottom-right corner, above the taskbar. It is topmost and **never takes
  keyboard focus**, so it can't steal what you are typing. A sound plays once per batch. It stays until answered:
  **Done**, **Snooze 10 min**, or **Open board**.
- At most four cards at a time; the rest appear as cards are answered.
- A reminder pops up once (`notified`); snoozing sets a new time and lets it pop up again.

## Porting

The `Board` project (model, store, grammar, times) is platform-neutral .NET and is ported with its tests. Each platform
supplies the window, the pop-up (a notification on macOS, Android and iOS) and the voice hook. See
[platform-porting.md](platform-porting.md).
