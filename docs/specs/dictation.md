# Dictation specification

Two ways to dictate. Both produce the same cleaned-up text.

| | Tap-to-dictate (keys) | Voice typing (“start typing”) |
|---|---|---|
| Start | Tap **Ctrl+Win** (press and let go); **Ctrl+Win+Space** does the same | Say “start typing” (Command mode) |
| Target | Wherever the cursor is when the text is inserted | The text box bound when typing started |
| Inserted | After Ctrl+Win is tapped again | After each pause (~1 s) |
| Stop | Tap **Ctrl+Win** again; **Esc** cancels; stops by itself after `maxDictationSeconds` | “stop typing” (nothing sent) or “send” |

## Tap-to-dictate (Windows reference)

There is no push-to-talk: the keys never have to be held.

- Pressing Ctrl+Win starts recording at once (tone). Releasing the keys does nothing, and holding them for a long time
  does not stop the recording.
- Tapping Ctrl+Win again finishes: when the keys are released, recording stops (tone), the pill shows “Transcribing…”,
  and the text is inserted. Finishing on release means no modifier is down while the text is typed.
- Ctrl+Win+Space is an alias for Ctrl+Win (the Space is swallowed), kept from when it toggled hands-free.
- Another key pressed together with the Ctrl+Win that started recording means a system shortcut (e.g. Ctrl+Win+D):
  that recording is discarded. A Ctrl+Win shortcut used while recording leaves the recording running.
- Esc while recording cancels (the Esc press is swallowed).
- A recording still going after `maxDictationSeconds` (default 600, i.e. 10 minutes; 0 = no limit) is finished and inserted by itself,
  so the microphone can't stay on by mistake.
- If a recording stops on its own (model still loading, microphone error, time limit) the next tap starts a new one.
- Works while listening is paused (the key press is explicit consent; the microphone opens only while recording).
- Releasing Win must not open the Start menu (inject an unassigned key first).
- The key logic lives in `TapToDictateKeys` (Core, unit-tested); the Windows hook only maps key events onto it.
- Platform mapping: macOS taps **Fn (Globe)**; Android taps a floating button or the keyboard's mic key (tap to start,
  tap again to finish); see [platform-porting.md](platform-porting.md).

## Status pill while dictating

- Recording: red level meter that moves with the microphone level (so it's clear the voice is being captured), the
  live preview below, and the hint “Ctrl+Win to finish · Esc to cancel”.
- After the second tap: “Transcribing…” with animated dots and a pulsing dot, shown before the preview is stopped and
  transcription starts. The last preview stays (dimmed) until the text is inserted, an error is shown, or “I didn't
  hear any speech.”
## Live preview

While recording, the pill shows the words as they are recognised:

- Every **600 ms**, re-transcribe the audio recorded so far (skip if < 0.5 s or < 0.3 s new audio).
- When more than **12 s** is unconfirmed, transcribe the oldest part once, cut at the latest pause between 3 s and 12 s
  (voice-activity regions; cut at the window edge if there's no pause), and keep its text, so each update covers at
  most 12 s of audio. Silence-only audio is dropped (keeping its last second).
- Show the formatted text (same clean-up as the final text); long text shows its latest ~150 characters with “…”.
- On finish, wait for an in-flight preview update (Whisper can't be cancelled mid-run), then run the final
  full-quality transcription. Utterances the voice-command listener completed during that wait are discarded.
- Setting: `livePreview` (default on).

## Clean-up (formatter)

Applied to every dictated phrase, in this order:

1. **Snippets:** a phrase equal to a snippet trigger (or “insert *trigger*”) is replaced by the snippet text, and
   nothing else applies.
2. **Filler words** (`removeFillerWords`), English and Hinglish:
   - sounds: “um”, “uh”, “uhm”, “erm”, “er”, “ah”, “hmm”, “mm” (stand-alone);
   - “basically” anywhere; “you know” / “I mean” between commas or opening a clause with a comma;
   - tags: “thik hai na” inside a sentence, as a sentence of its own, after a comma, or after a word that ends a
     clause (“… mein thik hai na?” → “… mein.”); kept when it is the question itself (“Papa ki tabiyat thik hai na?”,
     “Kya sab thik hai na?”) or all that was said; also “…, thik hai?”;
   - a run of **two or more** openers starting a sentence or clause: haan, so, ok/okay, achha, thik hai, matlab, chalo,
     alright, like, with “to/toh” allowed inside the run but not last (“Haan to like …”, “Okay so …”, but not
     “Ok to proceed”, “So okay to merge”, “So like I said”); “like” before “for example” / “jaise”;
   - the same 2–4 words said twice in a row (“for example, for example”); single doubled words (“dheere dheere”),
     spoken punctuation (“new line new line”) and digits (“1 2 1 2”) stay;
   - sentences that are only fillers (“Ok.”, “Thik hai.”, “Achha.”) when the dictation has other sentences
     (“Haan.” and “So.” stay: they can be answers).
   A single “to”, “so”, “haan”, “like” or “right” is kept, since it may carry meaning.
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
- With **no text box** in focus (desktop, taskbar, a list, a button), save Ctrl+Win dictation text on the idea board instead
  of discarding it ([idea-board.md](idea-board.md#dictation-with-nowhere-to-type)).

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
- Each Ctrl+Win dictation recording keeps the model it started with, even if the language is switched mid-recording.
- **Vocabulary hint:** the accurate model gets the user's word list (`dictationVocabulary`) as its Whisper prompt, which
  measurably fixes jargon (“vibe code”, “GitHub Copilot”). The fast model never gets it: with a prompt it outputs
  garbage (“while,”). Output that only repeats the word list is dropped (on noise and silence it wasn't seen in tests).
- **Phrase length while voice typing:** a phrase ends after ~1.1 s of quiet instead of 0.6 s, because Whisper punctuates
  each phrase as a complete sentence; shorter phrases meant stray full stops and capitals mid-sentence. For long
  passages, Ctrl+Win dictation (whole recording at once) gives the best punctuation.

## Paste last / untyped text

- **Alt+Shift+Z** pastes the last dictated text again into the focused field.
- Text that couldn't be typed (focus moved, unverifiable field) is kept in memory only; tray → “Copy text that wasn't
  typed” copies it and clears it.
