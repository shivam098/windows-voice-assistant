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
| Never leave the microphone open after Ctrl+Win dictation ends while listening is paused | Expectation of privacy | Microphone closed when the capture that opened it ends |
| Never let the AI brain type or press keys where text runs as a command | Screen text (a web page) could steer the model into running commands | No typing or keys in terminals, PowerShell, cmd, the Run box, File Explorer and the desktop (its address bar starts programs, its menus delete), Task Manager, PowerToys Run, the Start menu's search or the Registry Editor, nor typing into terminals inside apps; in browsers, typing only inside the page (never the address bar, recognised by structure in any language); no clipboard or menu keys (pasting and menu letters would get round the checks); Win shortcuts other than arranging windows (Win+R, Win+X…) and Ctrl+Esc refused |
| Never let the AI brain send, delete, buy, close, run a program or type a lot without a spoken “yes” | The model can pick the wrong thing | Destructive and send-like buttons, program files (.exe, .ps1…), files in File Explorer, any click or send key in a window after it typed a message there, Enter in any other box that isn't a search box, closing and menu keys (Alt+F4, Delete, Alt+letter…) and over 200 characters typed in all wait for “yes”; same window re-checked |
| Never let the AI brain act on speech not addressed to it | Conversation near the microphone mustn't become actions | Only after the wake word (or right after “Hey Jarvis” alone, or with the wake word off), and above the command confidence threshold |
| Never let the AI brain open anything but web pages | A link could be a file or a script | Only http/https addresses with a real host name; files, scripts, settings links, local and numeric hosts refused |
| Never let the AI brain run unbounded | A confused plan must not keep acting | At most 8 steps and 30 seconds per request; Esc stops it at once |
| Never send the AI brain's input or output off the device | Core promise | The model runs in llama.cpp on 127.0.0.1 (random port, random key); requests and replies aren't saved or logged |

## Always

- Show what is happening (pill) and where (place path), and why something was refused.
- Require the wake word before acting on speech in Standby; stop after 30 s without commands.
- Verify downloaded models against pinned SHA-256 hashes; delete on mismatch.
- Keep learned phrases and usage counts local, with a one-click “forget”.
- Run unelevated; request only the permissions needed (microphone, accessibility, input).

## Known limitations (state them to users)

- No speaker identification: anyone near the microphone can say “Hey Jarvis”. Users should pause listening in meetings.
- Accessibility names are supplied by apps; an app can mislabel a control. Destructive confirmation is name-based.
- Ctrl+Win dictation inserts into whatever has focus when Ctrl+Win is tapped to finish (the key press is the consent); only password
  and elevated targets are refused there.
- The AI brain sees the window titles and the names of buttons, links and boxes in up to three windows, which can include
  page text an attacker controls. Its limits above are enforced in code, not left to the model; it still can choose a
  wrong but harmless step (clicking the wrong link, searching instead of acting).
