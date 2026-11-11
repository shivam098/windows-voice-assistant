<p align="center"><img src="assets/logo.png" width="96" alt="Voice Assistant logo"></p>

# Windows Voice Assistant

Talk to your Windows PC: dictate into any app like [Wispr Flow](https://wisprflow.ai), control apps hands-free with “Hey Jarvis”, and capture ideas, to-dos and reminders on a voice **idea board**.

Everything runs **on this PC**. Speech is transcribed locally with [whisper.cpp](https://github.com/ggerganov/whisper.cpp) (via [Whisper.net](https://github.com/sandrohanea/whisper.net)) and filtered with the Silero voice-activity model; audio and transcripts are never written to disk or sent anywhere.

## Install

Requirements: Windows 10/11 (ARM64 or x64), a microphone, ~700 MB disk. No administrator rights.

```powershell
git clone https://github.com/shivam098/windows-voice-assistant.git
cd windows-voice-assistant
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

The installer builds a self-contained app, downloads the speech and voice-activity models once (SHA-256 verified), installs to `%LOCALAPPDATA%\Programs\VoiceAssistant`, adds a **Voice Assistant** Start menu shortcut, and starts it.

The **Voice Assistant window** opens with four pages: **Home** (status, pause/start, dictation language, upcoming reminders), **Idea board**, **Settings** and **What you can say**. Closing the window keeps the assistant running in the system tray (the purple microphone) with its status pill at the bottom of the screen. Click the tray icon or start the app again to reopen the window; tray › **Exit** quits.

| Option | Effect |
|---|---|
| `-StartAtSignIn` | Start in the tray when you sign in, window closed (also in Settings and the tray menu) |
| `-Model ggml-base.en-q8_0.bin` | Faster, less accurate model (default: `ggml-small.en-q8_0.bin`) |
| `-NoLaunch` | Install without starting |

Uninstall: `.\scripts\uninstall.ps1` (add `-RemoveData` to delete models, settings, and logs).

## Share it (portable zip)

Friends don't need to install anything:

```powershell
.\scripts\package.ps1                  # dist\VoiceAssistant-<version>-win-x64.zip and -win-arm64.zip (~70 MB each)
.\scripts\package.ps1 -IncludeModels   # also bundles the speech models (~340 MB) for offline use
```

They unzip it, double-click `VoiceAssistant.exe` (the app isn't code-signed, so Windows SmartScreen asks once: *More info → Run anyway*), and the speech models download on first run (~265 MB, SHA-256 verified). `README-FIRST.txt` in the zip explains the shortcuts. Use **win-x64** for Intel/AMD PCs and **win-arm64** for Snapdragon/ARM PCs. Ready-made zips are attached to the [GitHub releases](https://github.com/shivam098/windows-voice-assistant/releases).

The speech engine needs the Microsoft Visual C++ runtime. Release zips bundle it (built by `.github/workflows/release.yml` on a clean Windows runner whenever a `v*` tag is pushed), so friends need nothing else. Locally, `package.ps1` bundles it when Visual Studio or Build Tools with the C++ workload is installed, and warns otherwise.

## 1. Dictate anywhere (Wispr-style)

Put the cursor in any text box, then:

| Do | Result |
|---|---|
| **Hold `Ctrl`+`Win`**, speak, release | Your words show live in the overlay while you speak, then are inserted at the cursor |
| **`Ctrl`+`Win`+`Space`** | Hands-free dictation; press again to insert |
| **`Esc`** while recording | Cancel |
| **`Alt`+`Shift`+`Z`** | Paste the last dictation again |
| **`Alt`+`Shift`+`H`** | Switch dictation between English and **Hindi + English** (see below) |

Dictation is cleaned up automatically:

- **Filler words** are removed: “um”, “uh”, “basically”, “you know”, Hinglish tags like “thik hai na?”, openers like “Haan to like …” / “Okay so …”, a phrase said twice (“for example, for example”), and stray “Ok.” sentences. Words that may carry meaning (“to”, “so”, “haan”, “like” on their own) are kept.
- **Spoken punctuation**: “comma”, “question mark”, “exclamation mark”, “colon”, “new line”, “new paragraph”, and “period” at the end.
- **Inline correction**: “…call John, scratch that, email Sarah” keeps only what follows “scratch that” (it drops the phrase just before it).
- **Personal dictionary**: `replacements` fixes words and names you use (“git hub” → “GitHub”, “shivum” → “Shivam”).
- **Snippets**: say a trigger (or “insert *trigger*”) to type saved text, e.g. a signature.
- Text is **pasted** (reliable in every app). It is kept out of Clipboard History and cloud clipboard, and your previous clipboard is restored.

### Hindi + English (Hinglish)

Press **`Alt`+`Shift`+`H`** (or tray menu › *Dictation language* › *Hindi + English*) to dictate in Hindi, English, or a mix of both. Hindi is typed in Roman letters (“kal meeting reschedule kar do”), the way most people type Hinglish. A **HI + EN** badge on the pill shows it's on; press again for English.

- Uses [Oriserve's Whisper-Hindi2Hinglish-Swift](https://huggingface.co/Oriserve/Whisper-Hindi2Hinglish-Swift) model (Apache-2.0, see [third-party notices](docs/THIRD-PARTY-NOTICES.md)), still fully on this PC. It downloads once (~80 MB, SHA-256 verified) the first time you switch it on.
- Voice commands (“Hey Jarvis…”, “send”, “stop typing”) keep using the English model, so they work the same in both modes.
- **More accurate, but slower:** tray › *Dictation language* › *Hindi + English — accurate* uses Oriserve's larger [Whisper-Hindi2Hinglish-Apex](https://huggingface.co/Oriserve/Whisper-Hindi2Hinglish-Apex) for the typed text (about 2.5 s per phrase on a Snapdragon X Elite, slower on older PCs; one-time 830 MB download). Words still show live while you speak (from the fast model); the accurate text is typed when you release. Alt+Shift+H then switches between English and the accurate mode.
- English accuracy is lower in this mode (it misheard “pull request” as “poll request” in testing), so switch back for English-only writing. See [BENCHMARKS.md](docs/BENCHMARKS.md#hindi--english).

## 2. Voice control — “Hey Jarvis”

The assistant waits for **“Hey Jarvis”**. After the wake word, commands work without it for **30 seconds** (each command extends this). If you say a command without the wake word, the pill reminds you to say “Hey Jarvis” first; other speech is ignored silently. Short tones play when it wakes and when typing starts/stops.

Speak naturally — polite padding is understood: *“Hey Jarvis, can you open Notepad?”*, *“Could you please open Chrome for me”*, *“I want to start typing”*, *“fire up Chrome”*. Misheard app names are tolerated (“notpad”, “crome”, “outlook” → “Outlook (new)”).

**It learns from you:** if it doesn't understand something and you rephrase it within 30 seconds, it remembers the first phrase for next time (*“Got it — next time ‘get notepad going’ will do that too.”*). It only learns when both phrases share a meaningful word (like the app name), and prefers the apps you open most when a name is ambiguous. Learned phrases stay on this PC in `%APPDATA%\VoiceAssistant\learned.json`; Settings › *Forget what I've learned* clears them.

| Say | Does |
|---|---|
| “Open Notepad” / “Open Chrome” / “Open GitHub Copilot” | Opens the app. **Notepad opens a fresh window** (never types into your open document) |
| “Switch to Notepad” / “Go back to Chrome” | Brings the existing window to the front |
| “Start typing” | Starts dictation in the app’s text area — Notepad’s document, Copilot’s message box, a browser’s address/search bar (or the page text box you clicked) |
| “Hey Jarvis, open Notepad and start typing” | Both at once |
| “Go to the search box” | Browsers: the address/search bar. Other apps: the box labelled “search”. Then dictate your query |
| “Go to the address bar” | Browsers |
| “Search for weather in Bangalore” | Focuses search, types, presses Enter |
| “New tab”, “Close tab”, “Refresh”, “Scroll down” | Browser/app shortcuts |
| “Go back” / “previous page”, “go forward” / “next page” | Back and forward in the browser or File Explorer (Alt+Left / Alt+Right). For a page's own “Next” link (search results), say “click next” |
| “Go up” / “parent folder” | File Explorer: the folder that contains this one (Alt+Up) |
| “Copy”, “Paste”, “Cut”, “Undo”, “Redo”, “Select all”, “Save” | Editing shortcuts |
| “Press enter / tab / escape / up / down / page down …” | Any of those keys |
| “Pause listening” | Turns the microphone off |

**While typing** (red pill), everything you say is typed. Pause briefly, then say a command on its own:

| Say | Does |
|---|---|
| “Send” / “Search” / “Submit” | Presses the app’s send key (Enter), then back to commands |
| “Scratch that” | Deletes the last phrase |
| “Delete the last three words” / “delete the last sentence” / “delete the last line” | Deletes exactly that much before the cursor |
| “Delete the word very” / “replace the word Monday with Tuesday” | Finds the last match before the cursor and removes or replaces it (after “Hey Jarvis”: just “replace Monday with Tuesday”) |
| “Clear box” | Clears the text box |
| “New line” / “New paragraph” | Line break without sending |
| “Press tab” (any key) | Presses it |
| “Stop typing” | Back to commands; nothing is sent |

“Please send me the logs” is typed; a standalone “Send.” sends. Start with “Hey Jarvis, …” to force a command while typing.

**Example flows**

```text
"Hey Jarvis, open Notepad"  →  "Start typing"  →  "Dear team comma …"  →  "Stop typing"
"Hey Jarvis, open Chrome"   →  "New tab"  →  "Go to the search box"  →  "weather in Bangalore"  →  "Search"
"Hey Jarvis, open GitHub Copilot and start typing"  →  "Fix the failing test"  →  "Send"
```

**Status pill:** light blue = waiting for “Hey Jarvis”, blue = listening for a command, red = typing/dictating, grey = paused. `Ctrl+Alt+Space` (or the Home page's Pause button) pauses/resumes listening; push-to-talk works even while paused. Click **–** on the pill to hide it while idle: it then appears only for “Hey Jarvis”, `Ctrl+Win`, typing, and messages. Tray menu › **Always show status pill** brings it back.

## 3. Navigate by voice: tabs, buttons, text boxes

After “Hey Jarvis”, move around like you'd tell a person. Each command builds on where you are, and the pill shows it: **📍 Google Chrome › YouTube › Search**.

| Say | Does |
|---|---|
| “Go to the first tab” / “second tab” / “last tab” / “tab 3” | Selects that tab in the browser window in front |
| “Go to the YouTube tab” / “go to the tab where YouTube is open” | Finds the tab by its title in any Chrome, Edge or Firefox window (the front window wins ties) |
| “Click New on the left” / “click Library” / “select Pull requests” / “expand Sessions” / “select Family” (WhatsApp Web chat) | Clicks it by name; sound-alike names (“Vivash” for “Bibhash”) still match. “on the left/right/top/bottom”, “the second …” narrow it down |
| “Go to Issues” | Clicks a button called Issues when no app or tab has that name |
| “Start typing” | Puts the cursor in the right box: the app's message box, the site's box (YouTube/Google search, WhatsApp message…), the page's only box, or asks which |
| “Start typing in YouTube” / “… in the message box” / “… in the first tab” | Goes there first, then starts typing |
| “Show numbers” → “7” / “Hide numbers” | Numbers everything clickable; say a number to click it (text boxes start typing) |
| “Click New” (two of them) → “two” | When a name matches several things, numbers appear on just those |
| “Click Delete account” → “yes” | Anything like delete, merge, sign out, pay asks for “yes” first |
| While typing: “Hey Jarvis, go to the first tab” | Stops typing, then switches |

Every click is outlined briefly so you can see what happened. It works with apps that describe their buttons to Windows accessibility (most apps and all major browsers). Full behaviour: [docs/specs/navigation.md](docs/specs/navigation.md).

```text
"Hey Jarvis, open Chrome"  →  "Go to the first tab"  →  "Start typing"
"Go to the tab where YouTube is open"  →  "Start typing"  →  "lofi music"  →  "Search"
"Switch to GitHub Copilot"  →  "Click New on the left"  →  "Start typing"  →  "Fix the login test"  →  "Send"
```

## 4. Idea board — ideas, brainstorm, to-do, reminders

Say it and it's filed as a sticky note in the app window (**Idea board** page), in one of four lists:

| Say | Result |
|---|---|
| Hold `Ctrl`+`Win`: “Add to my ideas, a voice game for kids” | Filed under Ideas (not typed) |
| “Add renew passport to my to-do list” · “Add to brainstorming, pricing ideas” | To-do · Brainstorm |
| “Hey Jarvis, remind me to call mom tomorrow at 5 pm” | A reminder that pops up at 5 pm tomorrow |
| “Hey Jarvis, note down that the demo needs subtitles” | Ideas |
| “Hey Jarvis, show my reminders” / “…show my board” | Opens the window on that list / the whole board |
| “Hey Jarvis, mark renew passport as done” / “…tick off renew passport” | Ticks it off |

- Requests that **name a list** (“add … to my to-do list”) work with push-to-talk; looser ones (“remind me…”, “note down…”) need “Hey Jarvis”, so dictating “please remind me to send the slides” to a colleague still types it.
- **Nowhere to type?** If you hold `Ctrl`+`Win` with no text box in focus (desktop, taskbar, a file or chat list, a button), what you say is saved on the board instead of lost: a task with a future time (“call the bank tomorrow at 11”) becomes a reminder, a task (“I have to …”, “buy milk”) a to-do, anything else an idea.
- Reminder times: “in 10 minutes”, “at 5:30 pm”, “tonight”, “tomorrow morning”, “on Friday at 10 am”, “next Monday”. When one is due, a card appears in the bottom-right corner (without taking keyboard focus) with **Done**, **Snooze 10 min** and **Open board**. Reminders that came due while the app was closed appear when it starts.
- In the window: type a note in a column's box and press Enter; tick the checkbox when done; double-click to edit; right-click to move, snooze or delete.
- Notes are saved in `%APPDATA%\VoiceAssistant\board.json`, on this PC only.

The board lives in its own project (`src/VoiceAssistant.Board`, no Windows code) so it can become a separate app later. Full behaviour: [docs/specs/idea-board.md](docs/specs/idea-board.md).

## Safety and privacy

- **Bound to one window.** Voice dictation goes only to the window where you said “start typing”. If focus moves, nothing is typed or sent; the text is kept in memory (tray → *Copy text that wasn't typed*).
- **Refuses** password fields and windows running as administrator. Voice dictation also refuses fields it can't verify; push-to-talk (an explicit key press) allows them.
- **Whole-utterance commands only**: a sentence containing “send” never sends. Low-confidence commands are ignored.
- **Clicks are checked**: destructive buttons (delete, merge, sign out, pay…) need a spoken “yes”, and nothing is clicked if another window came to the front.
- **Music and noise are dropped** by Silero before transcription; long speech while waiting for a command (meetings) is skipped.
- Runs unelevated. Logs (`%LOCALAPPDATA%\VoiceAssistant\logs`, kept 7 days) contain only states and timings — never words.
- No speaker identification: anyone near the microphone can say “Hey Jarvis”. Pause listening in meetings.

## Settings

Most settings are on the window's **Settings** page (*Save and restart* applies them). Everything is stored in `%APPDATA%\VoiceAssistant\settings.json` (Settings › *Open settings file* for the rest, such as `apps`; restart to apply):

| Setting | Default | |
|---|---|---|
| `wakeWordEnabled` / `wakeWords` / `awakeSeconds` | `true` / “hey jarvis”… / `30` | Wake word behaviour |
| `learnFromUsage` | `true` | Learn rephrasings and app usage (`learned.json`) |
| `soundFeedback` | `true` | Tones on wake, typing start/stop, push-to-talk |
| `pushToTalk` | `true` | `Ctrl+Win` dictation keys |
| `livePreview` | `true` | Show words in the overlay while `Ctrl+Win` dictation records (re-transcribes every ~0.6 s; uses extra CPU while recording) |
| `pasteLastHotkey` | `Alt+Shift+Z` | |
| `dictationLanguageHotkey` | `Alt+Shift+H` | Switch dictation between English and Hindi + English |
| `hinglishModelFile` | `ggml-hinglish-swift-q8_0.bin` | Hindi + English dictation model (downloaded on first use) |
| `hinglishAccurateModelFile` | `ggml-hinglish-apex-q8_0.bin` | Model for *Hindi + English — accurate* (downloaded when first chosen) |
| `insertMethod` | `Paste` | `Type` sends keystrokes instead (some apps drop characters) |
| `removeFillerWords` / `spokenPunctuation` | `true` | Dictation clean-up |
| `replacements` | git hub → GitHub, … | Whole-word fixes |
| `snippets` | `{}` | e.g. `{ "my signature": "Thanks,\nShivam" }` |
| `modelFile` | `ggml-small.en-q8_0.bin` | In `%LOCALAPPDATA%\VoiceAssistant\models` |
| `endSilenceMs` | `600` | Pause that ends a phrase |
| `dictationEndSilenceMs` | `1100` | While voice typing, the pause that ends a phrase (longer, so a thinking pause doesn't split a sentence into separately punctuated pieces) |
| `dictationVocabulary` | `["GitHub Copilot", "vibe coding", "iterate", …]` | Words you use often: a spelling hint for the accurate Hindi + English model (“vibe coding”, not “wife coding”) |
| `maxCommandUtteranceMs` | `6000` | Longer speech while waiting for a command is skipped |
| `hotkey` | `Ctrl+Alt+Space` | Pause/resume listening |
| `apps` | Copilot, Notepad, Chrome, Edge, Firefox | Per-app launch, `submitKeys`, `newLineKeys`, `textArea` (`Bottom`/`Largest`), `textAreaName`, `searchKeys`, `newWindowKeys`, `newDocumentKeys` |

## How it works

```text
Microphone ─┬─ (Ctrl+Win held) ─────────────► capture ─► Silero chunks ─► Whisper ─► format ─► paste at cursor
            │                                   └─► live preview (Whisper every ~0.6 s) ─► overlay
            │                      (Whisper = small.en, or the Hindi + English model when switched on)
            └─ UtteranceSegmenter (pauses) ─► Silero (drop noise, split) ─► Whisper ─► CommandParser
                                                                                    ─► InteractionController
                                                                                    ─► Win32 + UI Automation
```

| Project | Purpose |
|---|---|
| `src/VoiceAssistant.Core` | Parser, wake word, formatter, segmenter, controller — platform-independent, unit-tested |
| `src/VoiceAssistant.Board` | Idea board: notes, lists, reminder times, voice grammar, JSON store — platform-independent, unit-tested |
| `src/VoiceAssistant.Speech` | Whisper and Silero wrappers |
| `src/VoiceAssistant.App` | App window (Home, Idea board, Settings, Help), tray, reminder pop-ups, overlay, microphone, key hook, clipboard paste, UI Automation |
| `tests/VoiceAssistant.Core.Tests` | xUnit tests (the executable form of [docs/specs](docs/specs/README.md)) |
| `tests/VoiceAssistant.Board.Tests` | xUnit tests for the idea board |
| `tests/NavigationLab` | Test pages and a self-test script for voice navigation on real browsers |
| `tools/SpeechBench` | Benchmarks, live microphone diagnostics, noisy-room replay |
| `tools/ModelConvert` | Converts Hugging Face Whisper checkpoints to ggml (how the Hindi + English model is built) |

## Develop

```powershell
dotnet test VoiceAssistant.slnx
dotnet build src/VoiceAssistant.App -r win-arm64        # or win-x64

# Desktop self-test: runs scripted transcripts against real apps (no microphone)
src\VoiceAssistant.App\bin\Debug\net10.0-windows\win-arm64\VoiceAssistant.exe --self-test script.txt out.txt

# Speech tools
dotnet run --project tools/SpeechBench -c Release -r win-arm64 -- --live 30            # live mic diagnostics
dotnet run --project tools/SpeechBench -c Release -r win-arm64 -- --mix noise.wav -22  # commands over room noise

# Voice navigation acceptance test (opens its own Chrome window of test pages)
.\tests\NavigationLab\run.ps1 -Exe src\VoiceAssistant.App\bin\Release\net10.0-windows\win-arm64\VoiceAssistant.exe
```

**Specifications** for every platform (Windows now; macOS, Android, iOS next) are in **[docs/specs](docs/specs/README.md)**: product, dictation, voice commands, navigation, idea board, speech pipeline, safety and privacy, a porting guide, and acceptance tests. Change the spec in the same pull request as the behaviour.

Regenerate the logo with `powershell -File assets\make-logo.ps1`. See the **[step-by-step guide](docs/GUIDE.md)** (install, use, share, develop, contribute), [docs/BENCHMARKS.md](docs/BENCHMARKS.md), and the design notes in [docs/design](docs/design).

## Known limitations

- English, plus Hindi + English dictation (Alt+Shift+H); voice commands are English only. A headset works best in noisy rooms.
- Clicking by name needs apps that expose accessibility names (most apps and all major browsers do). Apps drawn entirely on a canvas (some games, remote desktops) can't be clicked yet; there's no OCR or mouse grid.
- Inline “scratch that” is rule-based: it removes the phrase before it, not a single corrected word (“meet at 2, scratch that, 3” keeps only “3”). For precise fixes say “delete the last two words” or “replace the word 2 with 3”. Spoken edits follow fixed phrasings (see [voice-commands.md](docs/specs/voice-commands.md#editing-what-you-typed)); Wispr's AI rewrites and tones need a language model and aren't included.
- Voice dictation inserts text after each pause (~1 s), not word by word. Push-to-talk inserts when you release the keys.
- `Ctrl+Win` combinations used by Windows (e.g. `Ctrl+Win+D`) still work; the recording is discarded.
- Plain “Copilot” opens the Start menu app named *Copilot* (Microsoft Copilot). Say “GitHub Copilot”.
