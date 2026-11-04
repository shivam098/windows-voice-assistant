# Windows Voice Assistant - Product Requirements

Status: Draft v0.2 (Fluid UX & Universal-First Architecture)  
Version: 0.2  
Date: 2026-11-04  
Companion: TECHNICAL-SPEC.md

## 1. Product outcome

Build a fast, hands-free Windows voice assistant that lets the user open applications, dictate naturally into them in real time, and trigger workflow actions ("start typing", "send", "new chat") without touching the keyboard or mouse.

Phase 1 delivers a fluid end-to-end workflow in the GitHub Copilot desktop app while establishing a universal input core that immediately scales to other apps:

> "Open GitHub Copilot" -> "Start typing" -> speak naturally -> "Send" (or "Send it").

Here, the "composer" / "message box" means the prompt input field in GitHub Copilot.

## 2. Core design principles & defaults

### Confirmed product direction

- **Natural, low-friction voice commands:** Support pause-delimited natural commands (`[short pause] + "send"` / `"send it"` / `"stop typing"`) as the primary experience, with an optional prefix mode (`"Computer, ..."`) for noisy environments.
- **Iterative editing, not empty-box lockouts:** Default to appending at the active cursor when a message box already contains text, and provide voice commands to edit (`"scratch that"`, `"clear box"`).
- **Zero-touch app entry:** When opening Copilot, automatically focus the prompt composer or let the user say `"new chat"` if no composer is active.
- **Real-time responsiveness:** Stream or rapidly commit phrases (`< 1.0s` target latency after pause) so dictation feels like live typing.
- **Two-tier scaling model:**
  - **Tier 1 (Universal Core):** OS app launcher/switcher + active-cursor text injection + configurable submit shortcut (`Enter` / `Ctrl+Enter`). Works in almost any app out of the box.
  - **Tier 2 (Rich App Adapters):** Optional UI Automation (UIA) adapters (starting with GitHub Copilot) that auto-locate the composer when unfocused and verify state when available.

### Baseline defaults

| Topic | Default | Reason |
|---|---|---|
| Initial platform | Windows 11 x64, standard user privileges | Matches target laptop without requiring elevation |
| Speech processing | Local on-device streaming ASR + VAD | Privacy-first and low-latency |
| Command separation | Pause threshold (e.g., 450ms silence) + standalone/trailing command match | Lets users say `"now send"` naturally without false-triggering on `"please send me the logs"` |
| Existing text | Append at cursor (with smart leading space); `"clear box"` to wipe | Supports multi-turn drafting and corrections |
| Storage | No persistent audio/transcript logs on disk | Protects sensitive engineering prompts |

## 3. First-phase experience

### Happy path

1. **Open / Switch:** User says `"Open GitHub Copilot"` (or `"Switch to Copilot"`).
2. **Auto-Ready:** Assistant launches or brings Copilot to the foreground and automatically focuses the prompt composer (or starts a new chat if on a home screen without an active input).
3. **Dictate:** User says `"Start typing"` (or chains `"Open GitHub Copilot and start typing"`).
4. **Live Typing:** User speaks naturally. Confirmed words/phrases type directly into the composer at sub-second latency; live partial words display in a minimal cursor/tray pill.
5. **Quick Corrections:** If needed, user pauses briefly and says `"scratch that"` (undoes the last dictated phrase) or `"clear box"` (clears the draft).
6. **Send:** User pauses briefly (`~450ms`) and says `"send"` or `"send it"`.
7. **Submit:** Assistant flushes any remaining words and triggers the send action (via UIA Invoke or verified `Enter` keystroke), keeping the user ready for the next command.

### Command vocabulary (Phase 1)

| Mode | Spoken Phrase | Action |
|---|---|---|
| Command / Any | `"Open <App Name>"` / `"Switch to <App Name>"` | Launches or focuses the target app (Phase 1: GitHub Copilot) |
| Command | `"Start typing"` | Focuses composer (via Adapter if available, else active cursor) and enters Dictation mode |
| Command | `"New chat"` | Triggers new conversation (`Ctrl+N` / UIA button) and focuses composer |
| Dictation | `[pause] "send"` / `"send it"` / `"submit"` | Flushes pending text and submits the prompt |
| Dictation | `[pause] "scratch that"` / `"undo that"` | Removes the last inserted speech segment |
| Dictation | `[pause] "clear box"` / `"clear text"` | Selects and clears the current input box |
| Dictation | `[pause] "new line"` / `"new paragraph"` | Inserts `Shift+Enter` (line break without sending) |
| Dictation | `[pause] "stop typing"` / `"command mode"` | Exits Dictation mode back to Command mode without sending |
| Any | `"Pause listening"` / `"Go to sleep"` | Mutes microphone capture until woken via hotkey or wake phrase |

## 4. Phase 1 functional requirements

| ID | Requirement |
|---|---|
| P1-01 | Provide an unobtrusive floating status pill and tray icon showing state (`Listening - Command`, `Dictating`, `Paused`) |
| P1-02 | Support chained and standalone voice commands (`"Open GitHub Copilot"`, `"Start typing"`, `"New chat"`) |
| P1-03 | Separate commands from dictation using pause-duration gating + phrase matching so embedded words (`"send the file"`) are typed as text |
| P1-04 | Auto-focus the Copilot composer on `"Start typing"` using the Copilot UIA adapter, falling back cleanly to active focused control |
| P1-05 | Append dictated text at the cursor with automatic spacing and capitalization, supporting nonempty boxes |
| P1-06 | Support voice undo (`"scratch that"`) for the last inserted segment and `"clear box"` for full draft reset |
| P1-07 | Execute `"send"` / `"send it"` with `< 500ms` execution delay after utterance end |
| P1-08 | Detect focus theft mid-dictation: if the target window loses foreground focus while dictating, pause insertion and hold buffered text |
| P1-09 | Exclude password fields (`IsPassword` UIA property), UAC/secure desktop, and lock screen |
| P1-10 | Keep all audio and transcript buffers strictly in memory by default |

## 5. Phase 1 non-goals

- Clicking arbitrary unnamed UI coordinates via vision models.
- Automating login/MFA flows or bypassing Copilot tool/permission approval prompts.
- Cloud dependency for core speech-to-text or command execution.
- Operating inside elevated (Run as Administrator) windows from an unelevated process.

## 6. Quality and acceptance targets

| Area | Phase 1 target |
|---|---|
| End-to-end flow | >= 90% (27/30) natural open -> dictate -> send runs succeed hands-free without manual cursor clicking |
| Multi-turn & editing | Appending to existing text, `"scratch that"`, and `"clear box"` succeed across 20 consecutive test prompts |
| Command separation | Zero false sends across 50 sentences containing the word `"send"` mid-sentence; >= 95% recognition of pause-delimited `"send"` |
| Dictation latency | Median `< 700ms`, p95 `< 1.2s` from speech segment pause to text appearing in the composer |
| Command latency | `< 500ms` from end of `"send"` utterance to prompt submission |
| Privacy | 100% offline operation after model download; zero prompt/audio leakage to disk logs |

## 7. Delivery phases (Universal-First Roadmap)

| Phase | Deliverable | Key capability unlocked |
|---|---|---|
| **1A: Spike & Copilot Prototype** | Inspect Copilot UIA tree, benchmark streaming local ASR + VAD on laptop, validate pause-gated `"send"` | Sub-second local dictation + reliable Copilot focus/send |
| **1B: Phase 1 MVP (Copilot + Universal Core)** | Floating pill UI, app launcher, Copilot adapter, live cursor append, `"scratch that"`, `"new chat"`, `"send"` | Complete Infina-like hands-free loop in GitHub Copilot |
| **2: Universal Multi-App Scaling** | App registry (`"Open VS Code"`, `"Open Slack"`, `"Open Chrome"`, `"Open Notepad"`) + per-app send profile (`Enter` vs `Ctrl+Enter`) | Works immediately across editors, chat apps, and browsers without custom code per app |
| **3: Rich App Adapters & Context** | Optional UIA adapters for high-value apps (VS Code, Teams/Slack, Outlook) to auto-focus search/chat boxes when unfocused | Hands-free box targeting even when the cursor isn't already in the text field |
| **4: Smart Formatting & Custom Vocabulary** | Engineering dictionary (repo names, CLI flags, camelCase/snake_case), optional local small-LLM rewrite/cleanup | High accuracy for technical/code dictation |

## 8. Immediate next steps to start Phase 1A

1. Inspect the running GitHub Copilot desktop app window and accessibility tree on this laptop.
2. Scaffold the lightweight Windows prototype (.NET 8 WPF + NAudio + local Whisper/Sherpa-ONNX streaming ASR + UIA/SendInput engine).
3. Test the live `"Open GitHub Copilot" -> "Start typing" -> dictate -> "Send"` loop.
