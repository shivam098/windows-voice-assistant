# Dictation specification

Two ways to dictate. Both produce the same cleaned-up text.

| | Push-to-talk / hands-free (keys) | Voice typing (“start typing”) |
|---|---|---|
| Start | Hold **Ctrl+Win**; or **Ctrl+Win+Space** for hands-free | Say “start typing” (Command mode) |
| Target | Wherever the cursor is when the text is inserted | The text box bound when typing started |
| Inserted | When the keys are released / pressed again | After each pause (~1 s) |
| Stop | Release keys / Ctrl+Win+Space again; **Esc** cancels | “stop typing” (nothing sent) or “send” |

## Push-to-talk (Windows reference)

- Holding Ctrl+Win starts recording at once (tone). Releasing with no other key pressed transcribes and inserts.
- Any other key pressed while Ctrl+Win is held means a system shortcut (e.g. Ctrl+Win+D): recording is discarded.
- Ctrl+Win+Space while holding switches to hands-free; Ctrl+Win+Space again finishes.
- Esc while recording cancels (the Esc press is swallowed).
- Works while listening is paused (the key press is explicit consent; the microphone opens only while recording).
- Releasing Win must not open the Start menu (inject an unassigned key first).
- Platform mapping: macOS should use **Fn (Globe)** hold, as Wispr Flow does; see [platform-porting.md](platform-porting.md).

## Live preview

While recording, the pill shows the words as they are recognised:

- Every **600 ms**, re-transcribe the audio recorded so far (skip if < 0.5 s or < 0.3 s new audio).
- When more than **12 s** is unconfirmed, transcribe the oldest part once, cut at the latest pause between 3 s and 12 s
  (voice-activity regions; cut at the window edge if there's no pause), and keep its text, so each update covers at
  most 12 s of audio. Silence-only audio is dropped (keeping its last second).
- Show the formatted text (same clean-up as the final text); long text shows its latest ~150 characters with “…”.
- On release, wait for an in-flight preview update (Whisper can't be cancelled mid-run), then run the final
  full-quality transcription. Utterances the voice-command listener completed during that wait are discarded.
- Setting: `livePreview` (default on).

## Clean-up (formatter)

Applied to every dictated phrase, in this order:

1. **Snippets:** a phrase equal to a snippet trigger (or “insert *trigger*”) is replaced by the snippet text, and
   nothing else applies.
2. **Filler words:** “um”, “uh”, “uhm”, “erm”, “er”, “ah”, “hmm”, “mm” (stand-alone) are removed (`removeFillerWords`).
3. **Scratch that:** “…call John, scratch that, email Sarah” drops the clause before “scratch that”, back to the
   previous punctuation mark.
4. **Spoken punctuation** (`spokenPunctuation`): “comma” `,` · “full stop” `.` · “period” `.` (only at the end) ·
   “question mark” `?` · “exclamation mark/point” `!` · “colon” `:` · “semicolon” `;` · “new line” → line break ·
   “new paragraph” → blank line.
5. **Replacements:** whole-word, case-insensitive fixes from settings (“git hub” → “GitHub”), longest first.
6. **Tidy:** collapse spaces, no space before punctuation, no doubled punctuation, no leading comma/full stop,
   capitalise the start of each sentence and line.
7. **Capitalisation of the first word:** capitalised, unless the previous phrase didn't end a sentence and the word is a
   continuation word (a, and, but, or, so, because, the, to, …), which stays lowercase.
8. **Search boxes:** a trailing full stop is removed.

No full stop is added automatically; Whisper usually punctuates sentences itself.

Voice typing tracks each typed phrase so “scratch that” (as a command) removes exactly the last phrase, and adds a
leading space when the box already ends in a non-space character.

## Insertion

- Default: **paste** via the clipboard (reliable in every app). The temporary clipboard item must be excluded from
  clipboard history and cloud sync, and the user's previous clipboard restored ~350 ms later, only if nothing else
  changed the clipboard meanwhile.
- Alternative: synthesised keystrokes (`insertMethod: Type`); some apps drop characters.
- Line breaks use the app's new-line keys (Shift+Enter in chat apps so they don't send; Enter in editors).
- Wait for the user to release modifier keys before inserting.
- Refuse password fields and windows running with higher privileges (see [safety-and-privacy.md](safety-and-privacy.md)).

## Hindi + English (Hinglish)

- Toggle: **Alt+Shift+H** or the tray menu. Persists across restarts. The pill shows **HI + EN**.
- Dictation uses a second model (Oriserve Whisper-Hindi2Hinglish-Swift) that writes Hindi in Roman letters:
  “kal meeting reschedule kar do”.
- **Accurate option** (tray › Dictation language › *Hindi + English — accurate*): text is typed by the larger Oriserve
  Whisper-Hindi2Hinglish-Apex (834 MB, ~2.5 s per phrase on a Snapdragon X Elite vs 0.22 s; fewer mistakes on Hindi:
  19.1 % vs 23.5 % skeleton WER in [BENCHMARKS.md](../BENCHMARKS.md#hindi--english)). The live preview keeps using the
  fast model (no preview until it is loaded). Until Apex is loaded, dictation uses the fast model and says so. The pill
  shows **HI + EN · accurate**. Alt+Shift+H switches between English and whichever Hindi + English mode was chosen last.
- The model downloads (~80 MB, SHA-256 pinned) in the background the first time it's switched on; voice commands keep
  working meanwhile. A dictation started before it's ready uses English and says so. The accurate model downloads
  (with progress every 10 %) the first time that option is chosen.
- **Commands always use the English model.** In voice typing, each phrase is first parsed with the English model;
  only if it's not a command is the Hindi model asked for the text to type.
- Each push-to-talk recording keeps the model it started with, even if the language is switched mid-recording.

## Paste last / untyped text

- **Alt+Shift+Z** pastes the last dictated text again into the focused field.
- Text that couldn't be typed (focus moved, unverifiable field) is kept in memory only; tray → “Copy text that wasn't
  typed” copies it and clears it.
