# Windows Voice Assistant - Technical Specification

Status: Draft v0.2 (Low-Latency Streaming & Two-Tier Automation)  
Version: 0.2  
Date: 2026-11-04  
Companion: PRD.md

## Implementation status (v0.2.0)

v0.2 adds Wispr-style dictation, the “Hey Jarvis” wake word, app actions, and fixes recognition in noisy rooms:

| Area | v0.2.0 implementation |
|---|---|
| Phrase detection | Energy VAD with a **median** background estimate and an 80%-quiet end window, then **Silero** (whisper.cpp VAD) on each utterance to drop non-speech and split merged phrases |
| Wake word | “Hey Jarvis” (+ common Whisper spellings). Standby ignores other speech silently; commands work for 8 s after the wake word, extended by each command |
| Dictate anywhere | Hold `Ctrl+Win` (low-level keyboard hook), `Ctrl+Win+Space` hands-free, `Esc` cancel, `Alt+Shift+Z` paste last. Long recordings are chunked at Silero pauses into ≤14 s pieces |
| Text insertion | Clipboard paste with Clipboard-History/cloud exclusion formats; previous clipboard restored unless it changed. `Type` (SendInput) remains available |
| Dictation clean-up | Rule-based: fillers, spoken punctuation, inline “scratch that”, replacements, snippets, capitalisation across phrases, no trailing full stop in search boxes |
| App actions | Per-app `textArea` (`Bottom`/`Largest`) + `textAreaName`; browser `searchKeys` (`Ctrl+L`); UIA “search” box lookup elsewhere; “search for …”; key presses and editing shortcuts |
| Not implemented | AI rewrites/tones/command mode (needs a language model), multi-language, “new chat” |

v0.3 (user feedback: strict keywords, 8 s window too short, “open Notepad” reused the open document):

| Area | v0.3 implementation |
|---|---|
| Natural phrasing | Polite padding stripped (“can you please … for me”); more verbs (open up/fire up/run/start/launch; switch to/go to/show me); phrase variants for typing, search box, shortcuts. Unprefixed dictation stays strict |
| App names | Exact → relaxed (vendor prefix, parenthetical) → prefix → Levenshtein ≤1–2; ties go to the most-used app |
| Learning | `PhraseMemory`: unrecognised phrase + successful rephrasing within 30 s → synonym, only if they share a distinctive word; app usage counts; persisted to `learned.json` |
| Open vs switch | “Open” uses `newWindowKeys` (Notepad Ctrl+Shift+N) when running and `newDocumentKeys` (Ctrl+N) after launch; “switch to” reuses the existing window |
| Wake window | 30 s; commands without the wake word show a hint |
| Browser typing | “Start typing” focuses the address/search bar unless a page text box has focus |
| Feedback | Tones for wake / typing start / stop; command outcomes logged without content |

## Implementation status (v0.1.0, Phase 1 MVP)

What shipped differs from the v0.2 draft below in these places; the draft is kept for the roadmap.

| Spec item | v0.1.0 implementation | Why |
|---|---|---|
| Silero/WebRTC VAD | Adaptive energy VAD (`UtteranceSegmenter`), 600 ms end-of-phrase pause | No extra native model; tunable via `endSilenceMs`. Revisit if noisy rooms cause false starts |
| 450 ms pause gate | Commands must be a whole utterance; utterances end after 600 ms silence | Same separation guarantee, simpler; 450 ms split mid-sentence too often |
| Streaming partial text in overlay | Overlay shows state + last heard phrase after transcription | Whisper is batch; per-phrase latency is ~0.5 s (see BENCHMARKS.md) |
| “New chat” command | **Not implemented** | GitHub Copilot’s new-chat shortcut is unverified; will add once confirmed |
| NAudio WasapiCapture | NAudio `WaveInEvent` at 16 kHz mono | Windows resamples; no resampler code needed |
| Copilot composer focus | UIA search for the lowest visible enabled Edit control, then `SetFocus` | Could not inspect the live composer (window minimised during development); falls back to “click it once” |
| Send via UIA Invoke | `Enter` keystroke to the verified, focused target window | Matches app behaviour; Send-button lookup deferred |
| Universal app open | Configured apps + all Start menu apps (shell AppsFolder), exact/vendor-less name match | Ambiguous names prompt instead of guessing |
| Confidence gate | Commands below average token probability 0.3 are ignored | Measured 0.49–1.0 on clean speech |


## 1. Scope and architectural shift (v0.2)

To match the fluid behavior of Infina (`"Open GitHub Copilot" -> "Start typing" -> speak -> "Send"`), v0.2 replaces the rigid, high-latency v0.1 design with:

1. **Pause-Gated Natural Command Detection:** Users can say `"send"`, `"send it"`, `"scratch that"`, or `"stop typing"` naturally after a short pause (`>= 450ms`), without needing a mandatory `"Computer, ..."` prefix.
2. **Two-Tier Automation Engine:**
   - **Tier 1 (Universal Engine):** Uses OS process/window activation, `SendInput` Unicode text streaming at the current cursor, undo tracking (character-count backspace or `Ctrl+Z`), and per-app submit keybindings (`Enter` vs `Ctrl+Enter`). Works across virtually any desktop app immediately.
   - **Tier 2 (Optional App Adapters):** Lightweight UI Automation (UIA) enhancers for key apps (starting with GitHub Copilot) that locate and focus the prompt composer even when the cursor is elsewhere in the window.
3. **Sub-Second Dictation Pipeline:** Uses Silero VAD / WebRTC VAD with short-window local ASR (`whisper.cpp` tiny.en/base.en or Sherpa-ONNX streaming transducer) to commit phrases in `< 700ms–1.2s`.

## 2. Proposed stack

| Layer | Technology | Rationale |
|---|---|---|
| Host & Overlay | C# / .NET 8 WPF (`WS_EX_NOACTIVATE` floating pill + system tray) | Renders live partial speech and mode state near the cursor/screen edge without stealing focus |
| Audio Capture | NAudio `WasapiCapture` (16kHz mono PCM ring buffer) | Low-overhead microphone capture with device hot-plug handling |
| VAD & Endpointing | Silero VAD (via ONNX Runtime) or WebRTC VAD | Detects speech start, micro-pauses (`~450ms`), and phrase completion (`~600ms`) accurately |
| Speech Recognition | Local `Whisper.net` (`ggml-base.en` / `small.en`) or `Sherpa-ONNX` streaming | Fast on-device transcription; streaming partial hypotheses + accurate final segments |
| App Discovery & Launch | Windows Start Apps enumeration (`Get-StartApps` / Shell `AppsFolder` + process table + Fuzzy matcher) | Maps `"Open GitHub Copilot"` to the installed app or running `HWND` |
| Text & Key Injection | Win32 `SendInput` (`KEYEVENTF_UNICODE`) + fast clipboard paste option for long blocks | Reliable, app-agnostic typing at the cursor |
| Rich App Adapter | Windows UI Automation (`Interop.UIAutomationClient`) | Locates Copilot's composer edit control and Send button when not already focused |

## 3. Data flow and low-latency pipeline

```text
Mic PCM (16kHz)
  -> VAD Frame Classifier (30ms frames)
       |-- Speech active -> Stream partial preview to non-activating Overlay Pill
       |-- Pause >= 450ms -> Finalize audio chunk -> Local ASR
                                                        |
                                            Pause-Gated Intent Classifier
                                            /                           \
                          [Matches Command Grammar]             [Ordinary Dictation Text]
                                     |                                      |
                             ActionDispatcher                      TextCommitEngine
                         (Open / Focus / Undo / Send)         (Format spacing + SendInput
                                                               + push length to UndoStack)
```

## 4. Pause-Gated Command & Dictation Classifier

To allow bare commands like `"send"` without false triggers during normal sentences:

1. **Acoustic Pause Gate:** A command phrase during Dictation mode is only eligible if preceded by at least `450ms` of silence (or is a standalone utterance bounded by silence).
2. **Exact Utterance / Chained Suffix Rule:**
   - **Standalone utterance after pause:** `"send"`, `"send it"`, `"submit"`, `"scratch that"`, `"clear box"`, `"stop typing"`, `"new line"`.
   - **Chained opener in Command mode:** `"Open GitHub Copilot and start typing"` splits on `"and"` into `[OpenApp("GitHub Copilot"), StartTyping]`.
   - **Sentence protection:** Utterances like `"please send me the file"` or `"click send when ready"` contain non-command tokens without a pre-command pause boundary and are always typed as literal text.
3. **Optional Strict Prefix Mode:** Users in noisy rooms can toggle `"Require 'Computer' prefix during dictation"` in settings.

## 5. Two-Tier Automation & Copilot Adapter

### Tier 1: Universal App Controller (Works Everywhere)

- **Open / Focus (`OpenOrActivate`):**
  1. Search running top-level windows by process name and title match.
  2. If running, restore if minimized (`ShowWindow(SW_RESTORE)`) and bring to foreground (`SetForegroundWindow` with foreground-thread attachment fallback).
  3. If not running, launch via `shell:AppsFolder\<AppUserModelID>` or registered executable path, then wait up to 5s for the main window handle.
- **Type (`InsertSegment`):**
  - Check that foreground `HWND` still matches the active target window and focused element is not a password box (`UIA_IsPasswordPropertyId`).
  - Prepend a space if the previous segment did not end with whitespace/newline.
  - Inject characters via batched `SendInput` Unicode events, and push the exact character count onto `InsertedSegmentHistory`.
- **Undo (`ScratchThat`):**
  - Pop the last segment length $N$ from `InsertedSegmentHistory` and send $N$ `VK_BACK` keystrokes via `SendInput`.
- **Clear (`ClearBox`):**
  - Send `Ctrl+A` followed by `VK_BACK` to the focused editor, and clear `InsertedSegmentHistory`.
- **Send (`Submit`):**
  - Execute the app profile's submit action (default: `Enter`; configurable per app to `Ctrl+Enter` or `Alt+S`).

### Tier 2: GitHub Copilot UIA Enhancer

Because GitHub Copilot Desktop is an Electron/WebView-based window, when `"Start typing"` is spoken:

1. Verify the foreground window belongs to the GitHub Copilot app process.
2. Check if the currently focused UIA element is already an editable composer (`ControlType == Edit` or `Document` with `IsKeyboardFocusable`).
3. If not focused, query the window's UIA subtree for the prompt composer (matching editable control in the main chat pane) and call `SetFocus()` (or click its center bounding rect if WebView focus requires pointer activation).
4. If no active conversation composer exists (e.g., on an empty landing screen), invoke `"New chat"` (`Ctrl+N` or the New Chat UIA button) and focus the resulting composer.
5. On `"send"`, prefer invoking the composer's adjacent enabled Send button via UIA `InvokePattern` (or send verified `Enter` when the composer has focus).

## 6. Interaction State Machine

| State | Behavior | Key Transitions |
|---|---|---|
| `Paused` | Mic capture stopped | Hotkey or tray toggle -> `Command` |
| `Command` | Listens for app launch, window switch, `"start typing"`, `"new chat"` | `"Start typing"` -> focuses target composer -> `Dictation` |
| `Dictation` | Streams speech to target input; evaluates pause-gated commands | `"stop typing"` -> `Command`; `"send"` -> flushes pending audio -> submits -> stays in `Dictation` (or `Command` per user preference) |
| `SuspendedFocusLost` | Entered automatically if user Alt-Tabs or clicks another window mid-dictation | Saying `"resume typing"` or `"start typing"` re-binds to the current window |

*Note on post-send state:* After saying `"send"`, keeping the assistant ready in `Command` (or optionally staying in `Dictation` for follow-up messages) is configurable; default is returning to `Command` mode with a subtle audio chime so stray background sighs aren't typed while Copilot is generating a response.

## 7. Security, Privacy, and Guardrails

- **Password & Secure Desktop Guard:** Before every `SendInput` batch, query `UIA_IsPasswordPropertyId` on the focused element. If true, or if Windows is on the lock screen / UAC desktop, block insertion and alert via the overlay pill.
- **Foreground Lock:** Bind dictation to the active target `HWND`. If foreground window changes unexpectedly while an audio chunk is transcribing, hold the transcribed text in memory rather than typing into the wrong window.
- **Zero Disk Retention:** Audio ring buffers and `InsertedSegmentHistory` live solely in RAM. Operational logs record only timing metrics and state transitions.

## 8. Verification & Phase 1A Execution Plan

1. **Step 1 (Copilot Window & UIA Inspection):** Run a quick PowerShell/UIA inspection script on this machine to capture the exact process name, window class, and composer UIA properties of the GitHub Copilot app.
2. **Step 2 (Audio & ASR Latency Benchmark):** Measure local VAD + `Whisper.net` (`base.en`) transcription latency on this laptop's CPU/GPU.
3. **Step 3 (End-to-End Prototype):** Build the C#/.NET 8 app implementing the tray/overlay pill, `"Open GitHub Copilot"`, `"Start typing"`, live dictation, `"scratch that"`, and `"send"`.
