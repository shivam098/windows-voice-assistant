# Windows Voice Assistant

Hands-free voice control for Windows: open an app, dictate into it, and send — without touching the keyboard.

> “Open GitHub Copilot” → “Start typing” → *speak your message* → *pause* → “Send”

Everything runs **on this PC**. Speech is transcribed locally with [whisper.cpp](https://github.com/ggerganov/whisper.cpp) (via [Whisper.net](https://github.com/sandrohanea/whisper.net)); audio and transcripts are never written to disk or sent anywhere.

## Install

Requirements: Windows 10/11 (ARM64 or x64), a microphone, ~700 MB disk. No administrator rights.

```powershell
git clone https://github.com/shivam098/windows-voice-assistant.git
cd windows-voice-assistant
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

The installer builds a self-contained app, downloads the speech model once (SHA-256 verified), installs to `%LOCALAPPDATA%\Programs\VoiceAssistant`, adds a **Voice Assistant** Start menu shortcut, and starts it. Look for the microphone icon in the system tray.

| Option | Effect |
|---|---|
| `-StartAtSignIn` | Start automatically when you sign in (also a tray menu toggle) |
| `-Model ggml-base.en-q8_0.bin` | Faster, less accurate model (default: `ggml-small.en-q8_0.bin`) |
| `-NoLaunch` | Install without starting |

Uninstall: `.\scripts\uninstall.ps1` (add `-RemoveData` to delete models, settings, and logs).

## Use it

A small pill at the bottom of the screen shows what the assistant is doing:

| Pill | Meaning |
|---|---|
| 🔵 Listening for a command | Only the commands below do anything; other speech is ignored |
| 🔴 Typing | Everything you say is typed into the window you started typing in |
| ⚪ Paused | Microphone is off |

**Commands (blue):**

| Say | Does |
|---|---|
| “Open GitHub Copilot” / “Switch to Notepad” / “Open Outlook” | Opens or brings the app to the front (any Start menu app works) |
| “Open GitHub Copilot and start typing” | Both in one go |
| “Start typing” | Types into the focused window. In GitHub Copilot it finds the message box for you |
| “Pause listening” | Turns the microphone off |

**While typing (red)** — *pause briefly before each command*:

| Say | Does |
|---|---|
| “Send” / “Send it” / “Submit” | Presses the app’s send key (Enter in GitHub Copilot), then returns to commands |
| “Scratch that” | Deletes the last phrase you dictated |
| “Clear box” | Clears the whole message box |
| “New line” / “New paragraph” | Line break without sending |
| “Stop typing” | Back to commands; nothing is sent |

Commands must be spoken **on their own, after a short pause**. “Please send me the logs” is typed as text; a standalone “Send.” sends. If that’s ever ambiguous, prefix with **“Computer, …”** (e.g. “Computer, send”), or set `requireCommandPrefixWhileTyping` to `true` so only prefixed phrases count as commands.

**Keyboard/mouse fallback:** `Ctrl+Alt+Space` or a left-click on the tray icon toggles listening. The tray menu has “Voice commands…”, settings, and “Copy text that wasn't typed”.

## Safety behaviour

- **Bound to one window.** Dictation goes only to the window where you said “start typing”. If focus moves elsewhere, nothing is typed or sent; the text is kept in memory and can be copied from the tray menu.
- **Refuses** password fields and windows running as administrator.
- **Whole-utterance commands only** — no substring matching, so a sentence containing “send” never sends.
- **Low-confidence commands are ignored** rather than executed.
- Runs unelevated (`asInvoker`). Logs (`%LOCALAPPDATA%\VoiceAssistant\logs`, kept 7 days) contain only states and timings — never words.

## Settings

`%APPDATA%\VoiceAssistant\settings.json` (tray → *Open settings file*; restart to apply):

| Setting | Default | |
|---|---|---|
| `modelFile` | `ggml-small.en-q8_0.bin` | Must exist in `%LOCALAPPDATA%\VoiceAssistant\models` |
| `endSilenceMs` | `600` | Pause that ends a phrase. Raise if commands trigger mid-thought |
| `maxCommandUtteranceMs` | `6000` | While waiting for a command, longer speech (conversation, meeting audio) is skipped without transcribing |
| `hotkey` | `Ctrl+Alt+Space` | Toggle listening |
| `microphoneDevice` | `-1` | Windows default recording device |
| `listenOnStartup` | `true` | Start listening as soon as the app loads |
| `requireCommandPrefixWhileTyping` | `false` | Only “Computer, …” phrases are commands while typing |
| `returnToCommandModeAfterSend` | `true` | Stop typing after “send” |
| `minCommandConfidence` | `0.3` | Ignore commands Whisper is unsure about |
| `apps` | GitHub Copilot, Notepad | Per-app launch target, process name, `submitKeys`, `newLineKeys`, `focusComposer` |

## How it works

```text
Microphone (16 kHz) ─► UtteranceSegmenter (energy VAD, pause = end of phrase)
                     ─► WhisperRecognizer (local, ~0.5 s/phrase)
                     ─► CommandParser (whole-utterance match)
                     ─► InteractionController (command / typing state machine, window binding)
                     ─► WindowsDesktop (window activation, UI Automation, SendInput)
```

| Project | Purpose |
|---|---|
| `src/VoiceAssistant.Core` | Platform-independent logic: parser, app catalog, segmenter, controller (fully unit-tested) |
| `src/VoiceAssistant.Speech` | Whisper.net wrapper |
| `src/VoiceAssistant.App` | WPF tray app, overlay, microphone, Win32/UI Automation |
| `tests/VoiceAssistant.Core.Tests` | xUnit tests |
| `tools/SpeechBench` | Speech latency/accuracy benchmark and end-to-end pipeline check |

## Develop

```powershell
dotnet test tests/VoiceAssistant.Core.Tests
dotnet build src/VoiceAssistant.App -r win-arm64        # or win-x64
dotnet run --project tools/SpeechBench -c Release -r win-arm64 -- --pipeline
```

See [docs/BENCHMARKS.md](docs/BENCHMARKS.md) for measured numbers, [docs/PRD.md](docs/PRD.md) and [docs/TECHNICAL-SPEC.md](docs/TECHNICAL-SPEC.md) for the product plan.

## Known limitations (v0.1)

- English only. Accuracy depends on your microphone; a headset works best.
- Each phrase is transcribed after you pause, so text appears ~1 s after you stop speaking (not word-by-word).
- Whisper capitalises and punctuates each phrase, so pausing mid-sentence can produce “I want to. Build an app.”
- No speaker identification: anyone near the microphone can issue commands. Pause listening in meetings.
- Plain “Copilot” opens the Start menu app named *Copilot* (Microsoft Copilot). Say “GitHub Copilot”.
- Typing into Remote Desktop works when the RDP window is focused, but the message-box finder only works for local apps.
