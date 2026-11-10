# Voice Assistant — Step-by-step guide

Everything you need to install, use, share, and change the app. For the full command and settings reference see the [README](../README.md).

---

## 1. Install on your own PC (from source)

1. Clone the repo:
   ```powershell
   git clone https://github.com/shivam098/windows-voice-assistant.git
   cd windows-voice-assistant
   ```
2. Run the installer (no admin rights needed):
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
   ```
   It installs the .NET 10 SDK into your user profile if missing, builds the app for your PC (x64 or ARM64), downloads and verifies the speech models (~265 MB), installs to `%LOCALAPPDATA%\Programs\VoiceAssistant`, adds a Start menu shortcut, and starts it.
3. The **Voice Assistant** window opens (Home, Idea board, Settings, What you can say). Closing it keeps the assistant running: look for the **purple microphone** in the system tray (click it to reopen the window) and the status pill at the bottom of the screen. Click **–** on the pill to hide it until it's needed (it reappears for “Hey Jarvis”, `Ctrl` + `Win`, and messages); tray menu › **Always show status pill** brings it back.
4. Optional: Settings (or the tray menu) → **Start when I sign in** starts it in the tray, window closed (or install with `-StartAtSignIn`).

To update later: `git pull`, then run `install.ps1` again (it replaces the old version and keeps your settings, models, and learned phrases).

To uninstall: `.\scripts\uninstall.ps1` (add `-RemoveData` to also delete models, settings, logs, and learned phrases).

---

## 2. Use it

### Dictate anywhere

1. Click into any text box (email, chat, document, browser).
2. **Hold `Ctrl` + `Win`** and speak. Your words appear live in the overlay at the bottom of the screen; **release** and the text appears at the cursor.
3. For longer dictation press **`Ctrl` + `Win` + `Space`**, talk, and press it again to insert.
4. `Esc` cancels; `Alt` + `Shift` + `Z` pastes the last dictation again.
5. **Hindi + English:** press `Alt` + `Shift` + `H` (or tray › *Dictation language*) to dictate in Hindi, English, or a mix; Hindi is typed in Roman letters. The pill shows **HI + EN** while it's on. The first time, it downloads its model (~80 MB). Press again for English, which is more accurate for English-only text. Voice commands stay in English. If Hindi words come out wrong too often, choose tray › *Dictation language* › *Hindi + English — accurate*: fewer mistakes, but a few seconds per phrase (one-time 830 MB download). Add words you use often (names, jargon) to `dictationVocabulary` in the settings file so the accurate model spells them right. For long passages, hold Ctrl+Win and speak the whole thing: one recording gets better punctuation than many short voice-typing phrases.

Say punctuation (“comma”, “question mark”, “new line”); “um/uh” are removed automatically.

### Control apps by voice

1. Say **“Hey Jarvis”** (you hear a short tone; the pill turns blue). Commands then work for 30 seconds without repeating it.
2. Examples:
   ```text
   "Hey Jarvis, open Notepad"   → "Start typing" → speak → "Stop typing"
   "Hey Jarvis, open Chrome"    → "Go to the search box" → "weather in Bangalore" → "Search"
   "Hey Jarvis, can you open GitHub Copilot and start typing" → speak → "Send"
   ```
3. While typing (red pill), pause briefly before a command: “send”, “scratch that”, “clear box”, “new line”, “stop typing”.
   To fix part of what you typed: “delete the last two words”, “delete the last sentence”, “delete the last line”, “delete the word very”, “replace the word Monday with Tuesday”.
4. Pause the microphone with `Ctrl` + `Alt` + `Space`, the **Pause** button on the Home page, or “Hey Jarvis, pause listening”.

Speak naturally — “can you please open Notepad for me” works. If it doesn't understand something, say it another way within 30 seconds and it remembers the first phrase next time (Settings › **Forget what I've learned** to reset).

### Navigate: tabs, buttons, text boxes

1. **Tabs:** “go to the first tab”, “last tab”, “go to the YouTube tab”, “go to the tab where WhatsApp is open”.
2. **Click by name:** “click New on the left”, “click Library”, “select Pull requests”, “expand Sessions”. Chat lists work too: in WhatsApp Web say “click Family” or “select Bibhash Morang” (names that sound alike, like “Vivash”, still match). If two things have the same name, yellow numbers appear: say the number.
3. **Type in the right place:** “start typing” picks the app's or site's box (or asks which); “start typing in YouTube”, “start typing in the message box”.
4. **Show numbers:** “show numbers”, then say a number to click it; “hide numbers”.
5. Destructive buttons (delete, merge, sign out, pay…) wait for you to say **“yes”**.
6. The pill shows where you are: **📍 Google Chrome › YouTube › Search**.

### Capture ideas, to-dos and reminders

1. Hold `Ctrl` + `Win` and say **“Add to my ideas, a voice game for kids”**. It's filed on the window's **Idea board** page instead of being typed. Name any list: ideas, brainstorm(ing), to-do, reminders (“add renew passport to my to-do list”).
2. **“Hey Jarvis, remind me to call mom tomorrow at 5 pm.”** At 5 pm a card pops up in the bottom-right corner: **Done**, **Snooze 10 min**, or **Open board**. It doesn't take keyboard focus, so it won't interrupt your typing.
3. **“Hey Jarvis, note down that the demo needs subtitles”** adds to Ideas; **“Hey Jarvis, show my reminders”** opens the board; **“Hey Jarvis, mark renew passport as done”** ticks it off.
4. In the window you can also type a note in a column's box (Enter adds it), tick notes off, double-click to edit, and right-click to move, snooze or delete.

Full grammar and time phrases: [specs/idea-board.md](specs/idea-board.md).

---

## 3. Share with friends (portable zip)

**Easiest:** send the link to the latest [GitHub release](https://github.com/shivam098/windows-voice-assistant/releases). Release zips are built automatically on a clean Windows machine and include everything the app needs (.NET and the Visual C++ runtime).

To build zips yourself:

1. Build the zips:
   ```powershell
   .\scripts\package.ps1                  # dist\VoiceAssistant-<version>-win-x64.zip and -win-arm64.zip (~70 MB)
   .\scripts\package.ps1 -IncludeModels   # adds the models (~340 MB) so it works offline
   ```
   The Visual C++ runtime is bundled only if Visual Studio or Build Tools (C++ workload) is installed; otherwise the script warns and the zip needs the [Visual C++ Redistributable](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist) on the friend's PC.
2. Send the right zip — **win-x64** for Intel/AMD PCs (most), **win-arm64** for Snapdragon/ARM PCs — via OneDrive, Google Drive, WhatsApp, or a GitHub release.
3. Your friend:
   1. Unzips the folder anywhere and double-clicks `VoiceAssistant.exe`.
   2. If Windows shows **“Windows protected your PC”** (the app isn't code-signed): **More info → Run anyway**.
   3. Waits ~3 minutes on first run while the speech model downloads (progress shows in the pill).
   4. Allows microphone access if asked (Settings → Privacy & security → Microphone → *Let desktop apps access your microphone*).
   5. Reads `README-FIRST.txt` in the zip for the shortcuts.

Not supported: 32-bit Windows.

---

## 4. Change the code

1. Build and test:
   ```powershell
   dotnet test tests/VoiceAssistant.Core.Tests
   dotnet build src/VoiceAssistant.App -r win-arm64     # or win-x64
   ```
2. Check real desktop flows without a microphone (writes only PASS/FAIL and fingerprints, never your text):
   ```powershell
   src\VoiceAssistant.App\bin\Debug\net10.0-windows\win-arm64\VoiceAssistant.exe --self-test script.txt out.txt
   ```
   Script lines: `listen`, `say: <phrase>`, `ptt: <phrase>`, `wait <ms>`, `fg`, `read`, `expect: <text>`, `clipboard`.
3. Check speech recognition:
   ```powershell
   dotnet run --project tools/SpeechBench -c Release -r win-arm64 -- --pipeline        # scripted clips
   dotnet run --project tools/SpeechBench -c Release -r win-arm64 -- --live 30         # your microphone
   dotnet run --project tools/SpeechBench -c Release -r win-arm64 -- --preview         # live Ctrl+Win preview timing
   ```
4. Install your build: `.\scripts\install.ps1`.
5. Logs (timings and states only, never words): `%LOCALAPPDATA%\VoiceAssistant\logs`.

---

## 5. Contribute changes (branch → PR → merge)

1. Create a branch: `git checkout -b feature/<name>`.
2. Commit with tests passing; push: `git push -u origin feature/<name>`.
3. Open a pull request on GitHub against `main`, describe what changed and how it was tested.
4. Review, then merge (merge commit), and update your local copy:
   ```powershell
   git checkout main
   git pull
   ```
5. Re-run `.\scripts\install.ps1` to use the merged version.
6. To publish a release for friends: bump `<Version>` in `src/VoiceAssistant.App/VoiceAssistant.App.csproj`, merge, then tag and push:
   ```powershell
   git tag v0.3.1
   git push origin v0.3.1
   ```
   GitHub Actions builds both zips (with the Visual C++ runtime) and attaches them to the release.

---

## 6. Troubleshooting

| Symptom | Fix |
|---|---|
| Pill says “Say ‘Hey Jarvis’ first” | The 30-second command window ended — start with “Hey Jarvis” again |
| “Not a command I know” | Rephrase; it learns the first phrase. Check the window's *What you can say* page |
| Nothing happens when speaking | Is the pill grey (paused)? Press `Ctrl` + `Alt` + `Space`. Check microphone privacy settings |
| Text appears in the wrong place | Click into the text box, then say “start typing” (or use `Ctrl` + `Win`) |
| Won't type into an app | Apps running as administrator and password fields are refused by design |
| First run stuck downloading | Needs internet once; restart the app to retry, or use the `-IncludeModels` zip |
| Slow on an older PC | Install the faster model: `.\scripts\install.ps1 -Model ggml-base.en-q8_0.bin` |
