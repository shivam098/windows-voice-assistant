namespace VoiceAssistant.Core;

/// <summary>
/// What the brain is told: the tools, the rules and three worked examples, then the screen and the request. This is the
/// prompt measured in docs/design/ai-brain.md (tools/BrainSpike runs it): change it only with a new measurement.
/// </summary>
public static class BrainPrompt
{
    public const string System = """
You control a Windows PC. Turn the request into the FEWEST steps, using only these tools:
focus_window(target: a window title from the screen) · open_app(target: app name, only if no such window is open) · open_url(target: URL) ·
web_search(target: "youtube" or "google", text: query) · click(target: exact item name from the screen, without the role word) ·
type_text(text) · press(target: keys like "ctrl+w", "win+up", "m") · scroll(target: "up" or "down") · ask(text: question)
Rules: act on the window the request is about (focus it first if it isn't in front). If the item isn't on screen, web_search then click it.
Only press enter or click Send when the user asked to send. To delete, clear or remove, click the item that does it (the PC asks the user first).
Never click anything unrelated to the request. Questions and chat need no steps: just answer in "say".
If the request is unclear, use one ask step. "text" is "" when not used. "say" is one short sentence.
""";

    public static readonly IReadOnlyList<(string Screen, string Request, string Plan)> Examples =
    [
        ("""
Windows (front first):
1. "Inbox - Outlook" (Outlook), maximized, in front
2. "Spotify Premium" (Spotify), behind
Items in "Spotify Premium": button "Play"; button "Next"; link "Daily Mix 1"; link "Liked Songs"
""", "play daily mix 1 on spotify", """{"steps":[{"tool":"focus_window","target":"Spotify Premium","text":""},{"tool":"click","target":"Daily Mix 1","text":""}],"say":"Playing Daily Mix 1."}"""),
        ("""
Windows (front first):
1. "Untitled - Notepad" (Notepad), left half, in front
""", "find a pasta recipe on youtube", """{"steps":[{"tool":"web_search","target":"youtube","text":"pasta recipe"}],"say":"Searching YouTube for pasta recipes."}"""),
        ("""
Windows (front first):
1. "Untitled - Notepad" (Notepad), left half, in front
""", "how are you doing", """{"steps":[],"say":"All good, ready when you are."}"""),
    ];

    /// <summary>
    /// The plan's exact shape as a llama.cpp grammar: no whitespace and a fixed key order (measured faster than a JSON
    /// schema, which lets some models indent), at most 6 steps and 100 characters per field.
    /// </summary>
    public const string Grammar = """
root ::= "{\"steps\":[" (step ("," step){0,5})? "],\"say\":" str "}"
step ::= "{\"tool\":\"" tool "\",\"target\":" str ",\"text\":" str "}"
tool ::= "focus_window" | "open_app" | "open_url" | "web_search" | "click" | "type_text" | "press" | "scroll" | "ask"
str ::= "\"" char{0,100} "\""
char ::= [^"\\\x7F\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F]{4})
""";

    /// <summary>One request: <paramref name="screen"/> starts with “Windows (front first):”.</summary>
    public static string UserMessage(string screen, string request) =>
        $"Screen:\n{screen.TrimEnd()}\nRequest: \"{BrainSafety.CleanText(request, 300)}\"";

    /// <summary>The system prompt, the examples as earlier turns (their prefix stays in the model's cache), then the request.</summary>
    public static IReadOnlyList<ChatTurn> Messages(string screen, string request)
    {
        var messages = new List<ChatTurn> { new("system", System) };
        foreach (var (exampleScreen, exampleRequest, plan) in Examples)
        {
            messages.Add(new ChatTurn("user", UserMessage(exampleScreen, exampleRequest)));
            messages.Add(new ChatTurn("assistant", plan));
        }

        messages.Add(new ChatTurn("user", UserMessage(screen, request)));
        return messages;
    }
}
