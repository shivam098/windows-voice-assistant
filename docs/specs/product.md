# Product specification

## Purpose

Let people use their computer by voice: **dictate text into any app**, and **control apps** (open, switch, click,
type, send) hands-free. Everything runs on the device.

## Principles (all platforms must hold these)

1. **On-device.** Audio, transcripts and screen contents never leave the device. Optional downloads are models only,
   verified by SHA-256 before use.
2. **Never act on the wrong thing.** Before typing, sending or clicking, verify the target is still the one the user
   chose. If not, do nothing and say why. It is better to refuse than to type into the wrong window.
3. **Commands are whole utterances.** A sentence that contains a command word (“please send me the logs”) is text, never
   a command.
4. **Always show state.** A small status pill shows what the assistant is doing and where.
5. **Destructive actions ask first.** Clicking something like *Delete* or *Merge* needs a spoken “yes”.
6. **Logs hold no content.** Logs contain states, command kinds and timings, never words, names or titles.

## Modes

```text
             "Hey Jarvis"                 "start typing"
 Standby ─────────────────► Command ───────────────────────► Typing (dictation)
    ▲      30 s idle            ▲        "stop typing" / "send"     │
    └───────────────────────────┘◄─────────────────────────────────┘
 Paused (microphone off): from any mode via hotkey, tray, or "pause listening"
```

| Mode | Listens for | Pill |
|---|---|---|
| **Paused** | Nothing (microphone off). Push-to-talk still works. | Grey: “Paused · hold Ctrl+Win to dictate” |
| **Standby** | Only the wake word. Other speech is ignored silently. | Light blue: “Say ‘Hey Jarvis’ · hold Ctrl+Win to dictate” |
| **Command** | Any command, without the wake word, for **30 s** after the last command | Blue: “Listening for a command” |
| **Typing** | Everything is typed into the bound text box, except a short list of strict commands | Red: “Typing — say ‘stop typing’ when done” |

- The wake word (“Hey Jarvis”, plus common mis-hearings: “hi/hello/ok/okay Jarvis”, “hey jervis”, “hey javis”,
  “jarvis”) may be followed by a command in the same breath: “Hey Jarvis, open Chrome”.
- A command said in Standby without the wake word is not run; the pill says “Say ‘Hey Jarvis’ first, then the command.”
- In Typing mode, any command works when prefixed by the wake word (“Hey Jarvis, go to the first tab”). Commands other
  than the typing commands (send, scratch that, new line, stop typing…) leave Typing mode first, so dictation never
  lands in a new place.
- Commands below an average token probability of **0.3** are ignored (“Not sure I heard that right”).
- Wake word can be disabled (`wakeWordEnabled: false`); then Command mode is the resting state.

## Status pill

A small, always-on-top, **never-focused** window at the bottom centre of the screen:

- Mode dot and label (colours above); a **HI + EN** badge when Hindi + English dictation is on.
- Live words while push-to-talk records (see [dictation.md](dictation.md)).
- The last message (fades after **6 s**) and, in italics, what was heard.
- The **current place** after a navigation command: “📍 Google Chrome › YouTube › Search” (see [navigation.md](navigation.md)).
- A **–** button minimises it: it then appears only while there is something to show (awake, typing, recording,
  a message) and hides when idle. The choice persists across restarts; the tray menu restores it.
- Clicking the pill must never take keyboard focus from the app being typed into.

## Feedback sounds

Short tones (can be turned off with `soundFeedback`): wake word heard, typing started, typing stopped, push-to-talk
start and end, cancel.

## Learning

- If an utterance isn't understood and, within **30 s**, the user says a working command that shares a meaningful word
  with it, the first phrase is remembered as meaning the second (“get notepad going” → “open notepad”). The pill says
  “Got it — next time ‘…’ will do that too.”
- App use is counted; when an app name is ambiguous, the clearly most-used app wins.
- Learned data stays on the device (`learned.json`) and can be cleared from the tray (“Forget what I've learned”).

## Settings

User-editable JSON (`%APPDATA%\VoiceAssistant\settings.json` on Windows). Every platform should offer the same keys
where they make sense. The full list and defaults are in the [README](../../README.md#settings). UI-only choices
(pill minimised, Hindi + English on) are stored as separate flags so the settings file is never rewritten by the app.

## Tray / menu bar

Pause/resume, paste last dictation, dictation language, copy untyped text, voice command help, always show status pill,
start at sign-in, forget learned phrases, open settings, open logs, exit.
