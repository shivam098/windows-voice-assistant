# Voice Assistant specifications

These documents describe **what the assistant does**, independent of Windows. They are the contract for every
platform version (Windows today; macOS, Android, and iOS next). When a behaviour changes, update the spec in the same
pull request as the code.

| Spec | Covers |
|---|---|
| [product.md](product.md) | Principles, modes (paused / standby / command / typing), wake word, status pill, sounds, learning, settings |
| [dictation.md](dictation.md) | Push-to-talk, hands-free, live preview, text clean-up, insertion, Hindi + English |
| [voice-commands.md](voice-commands.md) | Command grammar: apps, typing, editing keys, strict vs. lenient matching |
| [navigation.md](navigation.md) | Tabs, clicking by name, numbers, confirmations, “start typing in X”, the current place |
| [idea-board.md](idea-board.md) | Ideas, brainstorm, to-do and reminder notes by voice; reminder times and pop-ups; the app window |
| [speech-pipeline.md](speech-pipeline.md) | Microphone → voice-activity detection → Whisper; models, parameters, latency budgets |
| [safety-and-privacy.md](safety-and-privacy.md) | What the assistant must never do, and what it may store or log |
| [platform-porting.md](platform-porting.md) | How each capability maps to Windows, macOS, Android and iOS APIs; what can't be ported |
| [acceptance-tests.md](acceptance-tests.md) | Scenarios every platform must pass, with expected results |

## How to use these for a new platform

1. Read [platform-porting.md](platform-porting.md) first: it lists which capabilities the platform allows.
2. Port the **platform-independent core** behaviour (parsing, matching, the state machine, text clean-up). On Windows it
   lives in `src/VoiceAssistant.Core` and is covered by `tests/VoiceAssistant.Core.Tests`. Those tests are the
   executable form of these specs: port them alongside the code, keeping the same inputs and expected outputs.
3. Implement the platform layer (the `IDesktop` operations in `src/VoiceAssistant.Core/IDesktop.cs`) with the
   platform's accessibility and input APIs.
4. Run the scenarios in [acceptance-tests.md](acceptance-tests.md) on real apps.

## Conventions

- **Must / should / may** are used in the RFC 2119 sense.
- “The user says X” means the speech recogniser returned X as one pause-delimited utterance. Matching ignores case,
  punctuation, and hyphens (“Co-Pilot.” = “copilot”).
- Quoted pill messages are the Windows wording; other platforms should keep the meaning, not necessarily the words.
- These specs describe **v0.7.0**. Version this folder with the app.

## Background documents

- [docs/PRD.md](../PRD.md) and [docs/TECHNICAL-SPEC.md](../TECHNICAL-SPEC.md): the original v0.1 product and technical
  plans (historical; these specs supersede them where they differ).
- [docs/design/](../design/): design notes behind features, with the evidence that shaped them.
- [docs/BENCHMARKS.md](../BENCHMARKS.md): measured speed and accuracy.
