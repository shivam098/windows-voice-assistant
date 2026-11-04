# Safety and privacy specification

These rules apply on every platform. A port that can't meet one must disable the affected feature, not relax the rule.

## Never

| Rule | Why | Windows mechanism |
|---|---|---|
| Never send audio, transcripts, screen contents or element names off the device | Core promise | No network use except model downloads |
| Never type into a password field | Credentials | `IsPassword` checked before every insert |
| Never type, click or send into a window other than the verified target | A wrong-window send can't be undone | Window identity (handle + process id) re-checked before each action; text that couldn't be typed is kept in memory for the tray |
| Never treat a word inside a sentence as a command | “Please send me the logs” must not send | Whole-utterance matching; strict list while typing |
| Never click a destructive control without a spoken “yes” | Delete/merge/pay can't be undone | Name-based check; “yes” must be the next utterance within 30 s; same window and same element name re-checked |
| Never click where an element used to be | Pages re-render; something else may be there now | Vanished or renamed elements are refused; real clicks only at points owned by the target window; timed-out work is cancelled |
| Never keep typing after moving elsewhere | Dictation would land in another tab or app | Navigation commands said while typing stop typing; switching tab/app or clicking forgets the typing target |
| Never act on windows running with higher privileges | The OS blocks it; trying would mislead | Process elevation check |
| Never write words, titles, element names or clipboard contents to logs or test output | Logs get shared for support | Logs: states, kinds, counts, timings; self-test prints PASS/FAIL and hashes |
| Never keep the user's clipboard changed | Paste-based insertion borrows it | Restore after ~350 ms if unchanged; mark the item “exclude from history / cloud” |
| Never leave the microphone open after push-to-talk ends while listening is paused | Expectation of privacy | Microphone closed when the capture that opened it ends |

## Always

- Show what is happening (pill) and where (place path), and why something was refused.
- Require the wake word before acting on speech in Standby; stop after 30 s without commands.
- Verify downloaded models against pinned SHA-256 hashes; delete on mismatch.
- Keep learned phrases and usage counts local, with a one-click “forget”.
- Run unelevated; request only the permissions needed (microphone, accessibility, input).

## Known limitations (state them to users)

- No speaker identification: anyone near the microphone can say “Hey Jarvis”. Users should pause listening in meetings.
- Accessibility names are supplied by apps; an app can mislabel a control. Destructive confirmation is name-based.
- Push-to-talk inserts into whatever has focus when the keys are released (the key press is the consent); only password
  and elevated targets are refused there.
