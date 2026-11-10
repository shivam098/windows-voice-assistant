# Speech pipeline specification

How audio becomes text. Every platform should reproduce these parameters; deviations need a measured reason in
[docs/BENCHMARKS.md](../BENCHMARKS.md).

```text
Microphone 16 kHz mono ─┬─ push-to-talk held ──► buffer ──► Silero chunks ≤14 s ─► Whisper ─► format ─► insert
                        │                          └─► live preview every 0.6 s ─► pill
                        └─ listening ─► utterance segmenter ─► Silero (drop noise, split) ─► Whisper ─► parser ─► controller
```

## Capture

- 16 kHz, 16-bit, mono; 30 ms buffers. The default recording device unless configured.
- While push-to-talk records, audio goes to its buffer only (not to the voice-command segmenter).
- The microphone is open only while listening or recording.

## Utterance segmentation (voice commands and voice typing)

Energy-based, robust to fans and background music:

| Parameter | Value |
|---|---|
| Frame | 30 ms |
| Background level | Median of non-speech frames over the last 5 s (first 10 frames warm up) |
| Speech starts | 3 consecutive frames ≥ background + 10 dB, and ≥ −55 dBFS |
| Speech continues | Frames ≥ background + 6 dB |
| Utterance ends | ≥ 80 % of the trailing 600 ms (`endSilenceMs`) is quiet; while voice typing, also over the trailing 1,100 ms (`dictationEndSilenceMs`) |
| Pre-roll kept | 300 ms before the start |
| Minimum speech | 200 ms |
| Maximum utterance | 14 s, then a forced cut; the next piece is marked as a continuation (typed, never a command) |
| Background adapts during sound | Only for steady sound (10th–90th percentile range < 12 dB) |

## Voice-activity detection (Silero)

- Model: Silero VAD v6.2.0 (`ggml-silero-v6.2.0.bin`, SHA-256 `2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987`).
- Threshold 0.5; minimum speech 150 ms; minimum silence 250 ms; no padding.
- Voice utterances: drop if no speech; **split** at speech gaps ≥ 600 ms (≥ 1,100 ms while voice typing) (each piece padded 250 ms) so a command glued to
  earlier speech by background noise is handled on its own.
- Push-to-talk: chunk the recording into ≤ 14 s pieces at speech gaps (padded 200 ms); no speech → “I didn't hear any speech.”

## Recognition (Whisper)

| Setting | Value | Why |
|---|---|---|
| English model | `ggml-small.en-q8_0.bin` (252 MB, SHA-256 `67a179f608ea6114bd3fdb9060e762b588a3fb3bd00c4387971be4d177958067`) | Sub-second, robust on real voices |
| Hindi + English model | `ggml-hinglish-swift-q8_0.bin` (78 MB, SHA-256 `0cf6e7077971d9aa43963b04bf764a47600e873f7e0f3f0f9742323560171245`), dictation only | Roman-script Hinglish; see [dictation.md](dictation.md#hindi--english-hinglish) |
| Hindi + English, accurate | `ggml-hinglish-apex-q8_0.bin` (834 MB, SHA-256 `0b4324d2c1ad64f20883ee7fcd5d2bb0a8466287dc70d74bc47066200c28c719`), dictation only, when chosen | Final text only; the live preview uses the fast model |
| Engine | whisper.cpp (Windows: Whisper.net 1.9.1) | Runs everywhere, CPU and GPU back ends |
| Language | English (`en`) for both models | The Hinglish model was trained to emit Roman script with `en` |
| Threads | ⅔ of cores, 2–8 | |
| Audio context | 768 (15.36 s window) | ~3× faster than the default 30 s window; pieces are ≤ 14 s |
| Decoding | Single segment, no context carried between utterances, token probabilities on | Independent utterances; confidence for commands |
| Minimum input | Pad to 1.1 s | whisper.cpp rejects < 1 s, which would drop one-word commands |
| Hallucination filter | Drop “you”, “thanks for watching”, “thank you for watching”, “please subscribe” for audio < 2.5 s | Known Whisper outputs on near-silence |
| Command confidence | Average token probability < 0.3 → command ignored | |
| Command length cap | In Standby/Command, utterances > 6 s are skipped | Meetings and conversation |
| Queue | Serial; at most 6 utterances waiting | Order is preserved; overload drops, never reorders |

Models are downloaded on first use from their published locations and must match the pinned SHA-256 before loading;
a mismatch deletes the file.

## Latency budgets (Snapdragon X Elite, measured)

| Step | Target | Measured |
|---|---|---|
| Whisper small.en per phrase | < 700 ms | median 0.47–0.52 s |
| Hinglish Swift per phrase | < 400 ms | median 0.22 s |
| End of speech → command executed | < 1.5 s | ~0.6 s pause + ~0.5 s ASR + action |
| Live preview update | < 1 s | median 0.56 s, max 1.1 s |
| Accessibility scan (navigation) | < 300 ms | 26–70 ms for a browser tab strip or page |
| Model load (cold) | < 2 s | 0.9 s |

Platforms with GPU/NPU acceleration (Apple Metal / Core ML, Android NNAPI, Snapdragon NPU) should use it; the budgets
above are CPU-only on Windows ARM64.
