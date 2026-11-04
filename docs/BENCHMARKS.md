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
