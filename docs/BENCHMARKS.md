# Benchmarks

Measured 2026-11-04 on the development laptop: Snapdragon X Elite X1E80100 (12 cores), 64 GB RAM, Windows 11 26300 ARM64, Whisper.net 1.9.1 CPU runtime (win-arm64).

## Method

`tools/SpeechBench` transcribes 14 clips (10 commands, 4 dictation sentences of 1.3–10.2 s) generated with Windows text-to-speech at 16 kHz mono. Times are warm per-phrase transcription only (excludes the end-of-phrase pause).

**Caveat:** synthetic speech is far cleaner than a real microphone. These numbers bound latency; they do not prove real-world accuracy.

## Results

| Model | Threads | Audio context | Median | Max | Exact transcripts |
|---|---|---|---|---|---|
| base.en-q8_0 | 4 | 1500 (default) | 663 ms | 732 ms | 14/14 |
| base.en-q8_0 | 8 | 1500 | 482 ms | 556 ms | 14/14 |
| **base.en-q8_0** | 8 | 768 | **198 ms** | 248 ms | 14/14 |
| base.en (f16) | 8 | 768 | 429 ms | 481 ms | 14/14 |
| small.en-q8_0 | 8 | 1500 | 1479 ms | 1690 ms | 14/14 |
| **small.en-q8_0** (default) | 8 | 768 | **517 ms** | 675 ms | 14/14 |

An audio context of 768 tokens (15.36 s) is ~3× faster than the default 30 s window; utterances are capped at 14 s to stay inside it. `small.en-q8_0` is the default because it is still sub-second and is more robust on real voices; `base.en-q8_0` is the low-latency option.

## End-to-end pipeline

`SpeechBench --pipeline` streams 7 clips separated by 0.9 s pauses through the segmenter, recognizer, and parser:

```
[Command  ] CMD OpenApp(github copilot)
[Command  ] CMD StartTyping
[Dictation] TYPE "Refactor the authentication module to use dependency injection and add unit tests."
[Dictation] TYPE "Please send me the latest build logs."      <- contains "send", correctly typed
[Dictation] CMD ScratchThat
[Dictation] TYPE "Can you review the pull request and summarize the main risks before we merge it?"
[Dictation] CMD Send
```

7 clips → 7 utterances, all routed correctly. Token probabilities ranged 0.49–1.00, which informed the 0.3 command-confidence default.

## Installed app

Cold start to listening: ~2.3 s (model load 0.95 s). Resident memory with `small.en-q8_0`: ~510 MB.

## Noisy room (v0.2)

Live microphone tests on this laptop found the v0.1 energy detector never saw the pauses: room noise and music sat around −50 dBFS while its noise floor tracked the quietest moments (−60 to −63 dBFS), so “Open GitHub Copilot” became an 8.6 s utterance merged with whatever followed (“not a command I know”).

`SpeechBench --mix` overlays the clean clips onto 45 s of the real recorded room noise (with music) at −22 and −32 dBFS, ~1 s apart:

| Version | Utterances routed correctly (10 scripted) |
|---|---|
| v0.1 energy VAD | 1/10 — commands merged into 7–14 s chunks |
| v0.2 median-background VAD | 8/10 — dictation and “Scratch that” merged during loud music (would be typed) |
| v0.2 + Silero split/drop | **10/10** at both levels; music-only audio dropped before Whisper |

## Hindi + English

Measured 2026-11-05 on the same laptop with the app's settings (8 threads, audio context 768, single segment, q8_0). Oriserve's models were converted with `tools/ModelConvert` from the official weights; on the sample clips from their model card the converted Swift model reproduces the card's outputs.

Test sets: the 14 English clips above; 25 clips (≤14 s) of read Hindi from Google FLEURS (`hi_in` dev); 11 sample clips from Oriserve's model card (noisy calls and Common Voice, no references). FLEURS references are Devanagari while Oriserve writes Roman script, so Hindi is scored with an approximate **skeleton WER**: both scripts reduced to consonant skeletons per word (“uchchaaran” and “उच्चारण” → “ccrn”).

| Model | Size | Median per phrase | English WER (exact) | Hindi skeleton WER | Hindi written as |
|---|---|---|---|---|---|
| small.en (default, commands) | 252 MB | 0.47 s | 2.3% (13/14) | 98.9% (no Hindi) | — |
| **Oriserve Swift** (Hindi + English mode) | 78 MB | **0.22 s** | 8.0% (9/14) | 23.5% | Roman |
| Oriserve Apex | 834 MB | 2.4 s | 10.2% (7/14) | 19.1% | Roman |
| large-v3-turbo, auto language | 874 MB | 9.5 s | 0.0% (14/14) | 30.1% | Devanagari, sometimes Urdu script or another language |
| large-v3-turbo, Hindi fixed | 874 MB | 3.0 s | 43.2% (6/14) | 18.2% | Devanagari |

Swift was chosen for Hindi + English dictation: fastest, small, and it kept short commands such as “Send” intact (Apex heard “Sand”). Commands always use small.en. Apex is offered as the optional *Hindi + English — accurate* mode (since 0.5.2) for people who prefer fewer Hindi mistakes over speed. Caveats: the English clips are a synthetic US voice (Oriserve trained on ~550 h of Indian-accented speech), FLEURS is formal read Hindi rather than mixed speech, and skeleton WER is approximate.

## Desktop self-test (v0.2)

`VoiceAssistant.exe --self-test` against the real apps on this machine:

- Notepad: wake word → open → new tab → start typing → dictation with filler, “comma”, “scratch that”, “new line”, “question mark” → exact expected text; clipboard fingerprint identical before and after.
- Chrome: new tab → “go to the search box” → “weather in Bangalore” → “search” → Google results page.
- GitHub Copilot: “start typing” focuses the empty **Message** box (not the side-panel editor).
- Synthesized Unicode keystrokes into Windows 11 Notepad intermittently dropped or garbled characters at every pacing tried; pasting was exact, so `Paste` is the default insert method.
