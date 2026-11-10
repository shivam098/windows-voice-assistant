namespace VoiceAssistant.App.UI;

/// <summary>Everything the user can say, grouped. Shown on the Help page (searchable) and kept in one place.</summary>
internal static class HelpText
{
    public static readonly IReadOnlyList<(string Title, string[] Lines)> Sections =
    [
        ("Dictate anywhere", [
            "Hold Ctrl+Win, speak, release → the text is typed where your cursor is",
            "Ctrl+Win+Space → hands-free dictation; press again to finish",
            "Esc → cancel  ·  Alt+Shift+Z → paste the last dictation again",
            "Say “comma”, “question mark”, “new line”; “um/uh” are removed",
            "“…scratch that…” drops the phrase before it",
            "Alt+Shift+H → Hindi + English (typed in Roman letters); press again for English",
        ]),
        ("Idea board", [
            "Hold Ctrl+Win: “Add to my ideas, a voice game for kids” → filed on the board, not typed",
            "“Add renew passport to my to-do list” · “Add to brainstorming, pricing ideas”",
            "“Hey Jarvis, remind me to call mom tomorrow at 5 pm” → a reminder pops up then",
            "“Hey Jarvis, note down that the demo needs subtitles” → Ideas",
            "“Hey Jarvis, show my ideas / reminders / to-do list / board”",
            "“Hey Jarvis, mark renew passport as done”",
            "Times: in 10 minutes · in an hour · tonight · tomorrow at 9 · on Friday at 10 am · next Monday",
        ]),
        ("Voice commands (start with “Hey Jarvis”)", [
            "“Open Notepad” · “Open Chrome” · “Open GitHub Copilot” · “Switch to Chrome”",
            "“Start typing” (Notepad: the document; Copilot: the message box)",
            "“Go to the search box” · “Go to the address bar” · “Search for weather in Bangalore”",
            "“New tab”, “Refresh”, “Copy”, “Paste”, “Undo”, “Save”",
            "“Go back” / “Previous page” · “Go forward” / “Next page” · “Go up” (File Explorer)",
            "“Press enter / tab / escape / down …”",
            "“Pause listening”",
        ]),
        ("While typing (red pill): pause briefly before each command", [
            "“Send” / “Search” · “Scratch that” · “Clear box” · “New line” · “Stop typing”",
            "“Delete the last 3 words / sentence / line / paragraph”",
            "“Delete the word very” · “Replace the word Monday with Tuesday”",
            "“Hey Jarvis, add to my ideas, …” files a note and typing carries on",
        ]),
        ("Navigate (after “Hey Jarvis”)", [
            "“Go to the first tab” · “Go to the YouTube tab”",
            "“Click New on the left” · “Click Library” · “Select Bibhash Morang” (chat lists)",
            "“Start typing in YouTube” · “Start typing in the message box”",
            "“Show numbers” → say a number · “Hide numbers”",
            "Delete / merge / sign out / pay → say “yes” to confirm",
        ]),
    ];
}
