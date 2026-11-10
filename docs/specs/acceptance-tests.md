# Acceptance tests

Scenarios every platform must pass on real apps. “Expected” is what the user sees; the pill wording may differ per
platform. Windows automation: `VoiceAssistant.exe --self-test <script> <out>` (see `src/VoiceAssistant.App/SelfTest.cs`);
navigation scenarios run with `tests/NavigationLab/run.ps1`.

## Modes and wake word

| # | Steps | Expected |
|---|---|---|
| M1 | Standby; say “open notepad” | Nothing opens; pill: “Say ‘Hey Jarvis’ first” |
| M2 | Say “Hey Jarvis” | Tone; pill blue “Listening for a command” |
| M3 | “Hey Jarvis, open Notepad” | A **new** Notepad window opens (an existing document is untouched) |
| M4 | Wait 30 s after the last command | Back to Standby |
| M5 | Say something random in Standby | Nothing happens, no message |
| M6 | “Hey Jarvis, pause listening” | Microphone off; pill grey |

## Dictation

| # | Steps | Expected |
|---|---|---|
| D1 | Cursor in a text box; hold push-to-talk, say “um so the demo is ready comma can you check it question mark”, release | “So the demo is ready, can you check it?” inserted |
| D2 | Hold push-to-talk and speak for 20 s | Words appear in the pill while speaking; full text inserted on release |
| D3 | Hold push-to-talk, press another key (system shortcut) | Recording discarded |
| D4 | Hold push-to-talk, press Esc | Cancelled; nothing inserted |
| D5 | Clipboard holds X; dictate with push-to-talk | Text inserted; clipboard is X again; item not in clipboard history |
| D6 | Switch on Hindi + English; dictate “kal ki meeting reschedule kar do” | Roman-script Hinglish inserted; pill shows HI + EN |
| D7 | Password field focused; dictate | Refused with a message; nothing typed |
| D8 | Tray › Dictation language › Hindi + English — accurate; dictate the same phrase | Live preview still updates; text typed by the accurate model (log `model=hinglish-accurate`); pill shows HI + EN · accurate; Alt+Shift+H toggles English ↔ accurate |
| D9 | While typing: “delete the last two words”, “replace the word Monday with Tuesday”, “delete the word noon”, “delete the last sentence” | Exactly that text changes; typing continues at the end (`tests/EditingLab`) |

## Voice typing

| # | Steps | Expected |
|---|---|---|
| T1 | “Hey Jarvis, open GitHub Copilot and start typing”; “fix the failing test”; “send” | Typed into the message box; Enter pressed; back to Command mode |
| T2 | While typing: “please send me the logs” | Typed, not sent |
| T3 | While typing: “scratch that” | Last phrase removed |
| T4 | While typing, click another window, keep talking | Nothing typed there; pill says focus moved; tray offers the text |
| T5 | “Hey Jarvis, open Chrome”; “go to the search box”; “weather in Bangalore”; “search” | Search results for the query |

## Navigation (tests/NavigationLab: videos.html, lab.html, mail.html in a new browser window, in that order)

| # | Steps | Expected |
|---|---|---|
| N1 | “Hey Jarvis”; “go to the second tab” | Lab tab selected; pill “📍 Google Chrome › Nav Lab” |
| N2 | “click New on the left side” | Page shows “clicked New left”; button outlined briefly |
| N3 | “click New”; then “two” | Two number badges; after “two”, page shows “clicked New right”; badges gone |
| N4 | “go to library” | “clicked Library left” (no app or tab called Library) |
| N5 | “click delete account” | Not clicked; pill asks for “yes”. Then “yes” → “clicked Delete account right” |
| N6 | “start typing in the message box”; “hello from voice” | Focus in Message; text typed |
| N7 | “go to the tab where YouTube is open”; “start typing”; “lofi music” | Videos tab; focus in its Search box (site profile); text typed |
| N8 | “go to the last tab”; “start typing” | Two badges (To, Subject); the address bar is not offered. “two” → focus in Subject |
| N9 | “show numbers”; “hide numbers” | Badges on every button, link, tab and text box; then hidden |
| N10 | Same site open in two browser windows; “go to the YouTube tab” | The front window's tab is selected |
| N11 | In a real app (e.g. GitHub Copilot) “click New on the left”; “start typing” | The sidebar New is clicked (not another New); then the message box is focused |
| N12 | While typing: “Hey Jarvis, go to the first tab”; keep talking | Typing stops before switching; nothing is typed into the new tab |
| N13 | While typing: “Hey Jarvis, next tab”; then “send” | Tab switches; “send” does nothing (“Say ‘start typing’ first”) |
| N14 | “click delete account”; say something unrelated; “yes” | Nothing is clicked |
| N15 | Standby (no wake word): say “yes”, “no”, “two” | No reaction at all |
| N16 | In the browser, after opening two pages: “go back”, “go to the next page”, “go to the previous page” | Back, forward, back (`tests/NavigationLab`) |
| N17 | File Explorer in a sub-folder: “go up one level”, “go back”, “go forward” | Parent, sub-folder, parent (`tests/NavigationLab/run-explorer.ps1`) |

## App window and idea board

| # | Steps | Expected |
|---|---|---|
| B1 | Start the app from the Start menu | Window opens on Home; tray icon present |
| B2 | Close the window; say “Hey Jarvis, open Notepad” | Notepad opens (the assistant kept running); a one-time tray balloon explains it |
| B3 | Start the app again from the Start menu while it runs | The running copy's window comes to the front; no second copy, no message box |
| B4 | Sign out and in with “Start when I sign in” on | Tray icon only; no window |
| B5 | Notepad focused; hold push-to-talk: “add to my ideas, a voice game for kids” | Nothing typed in Notepad; the note is under Ideas; pill confirms |
| B6 | Hold push-to-talk: “please remind me to send the slides” | Typed as dictation, not filed |
| B7 | “Hey Jarvis, remind me to stretch in 1 minute” | Reminders shows it with a time; after ~1 min a card appears bottom-right with a sound; typing in another app isn't interrupted |
| B8 | On the card, press Snooze 10 min; then Done when it returns | It comes back 10 min later; Done ticks it off |
| B9 | “Hey Jarvis, show my reminders” | Window opens on the board with the Reminders column outlined |
| B10 | “Hey Jarvis, mark voice game as done” | That note is ticked off |
| B11 | “Hey Jarvis, tick the checkbox” on a page with one | Not a board request (no “couldn't find” warning on the board) |
| B12 | Add a reminder for 1 minute, exit, wait 2 minutes, start the app | The missed reminder pops up at start |
| B13 | Settings: change “Pause that ends a phrase” to 1500, Save and restart | App restarts; the value is in `settings.json` |

B5–B6 and B10–B11 are also covered by unit tests (`tests/VoiceAssistant.Board.Tests`, `BoardVoiceTests`).

The Windows runner uses `stoponfail`: if the test window isn't in front (Windows blocks background apps from taking
focus while someone is typing), the run stops instead of typing elsewhere. Run it while the PC is idle.

Latest Windows run (v0.5.0, Snapdragon X Elite, Chrome): N1–N9 **14/14 checks passed**; N11 checked with numbers
(badges on New, Pull requests, Issues in GitHub Copilot) and Microsoft Copilot (“click Library”, “click Home”).
N10 and N12–N15 are covered by unit tests.
