using System.Diagnostics;
using System.Text.Json;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.AI.Foundry.Local.OpenAI;

/// <summary>
/// The planner prompt, the plan format and 20 realistic requests with screen summaries and checks. A plan passes if
/// its steps do what a person would expect from the request; small differences in wording don't matter.
/// </summary>
internal static class Bench
{
    public const string System = """
You control a Windows PC. Turn the request into the FEWEST steps, using only these tools:
focus_window(target: a window title from the screen) · open_app(target: app name, only if no such window is open) · open_url(target: URL) ·
web_search(target: "youtube" or "google", text: query) · click(target: exact item name from the screen, without the role word) ·
type_text(text) · press(target: keys like "ctrl+w", "win+up", "m") · scroll(target: "up" or "down") · ask(text: question)
Rules: act on the window the request is about (focus it first if it isn't in front). If the item isn't on screen, web_search then click it.
Only press enter or click Send/Delete when the user asked to send or delete. Questions and chat need no steps: just answer in "say".
If the request is unclear, use one ask step. "text" is "" when not used. "say" is one short sentence.
""";

    private static readonly (string User, string Plan)[] Examples =
    [
        ("""
Screen:
Windows (front first):
1. "Inbox - Outlook" (Outlook), maximized, in front
2. "Spotify Premium" (Spotify), behind
Items in "Spotify Premium": button "Play"; button "Next"; link "Daily Mix 1"; link "Liked Songs"
Request: "play daily mix 1 on spotify"
""", """{"steps":[{"tool":"focus_window","target":"Spotify Premium","text":""},{"tool":"click","target":"Daily Mix 1","text":""}],"say":"Playing Daily Mix 1."}"""),
        ("""
Screen:
Windows (front first):
1. "Untitled - Notepad" (Notepad), left half, in front
Request: "find a pasta recipe on youtube"
""", """{"steps":[{"tool":"web_search","target":"youtube","text":"pasta recipe"}],"say":"Searching YouTube for pasta recipes."}"""),
        ("""
Screen:
Windows (front first):
1. "Untitled - Notepad" (Notepad), left half, in front
Request: "how are you doing"
""", """{"steps":[],"say":"All good, ready when you are."}"""),
    ];

    public static List<Betalgo.Ranul.OpenAI.ObjectModels.RequestModels.ChatMessage> Messages(string user)
    {
        var m = new List<Betalgo.Ranul.OpenAI.ObjectModels.RequestModels.ChatMessage> { Betalgo.Ranul.OpenAI.ObjectModels.RequestModels.ChatMessage.FromSystem(System) };
        foreach (var (u, p) in Examples)
        {
            m.Add(Betalgo.Ranul.OpenAI.ObjectModels.RequestModels.ChatMessage.FromUser(u.Trim()));
            m.Add(Betalgo.Ranul.OpenAI.ObjectModels.RequestModels.ChatMessage.FromAssistant(p));
        }

        m.Add(Betalgo.Ranul.OpenAI.ObjectModels.RequestModels.ChatMessage.FromUser(user));
        return m;
    }
    // The runtime wants a JSON schema as a string; the SDK's typed JsonSchema is ignored, so the schema goes in through
    // a Lark grammar (llguidance's %json embeds it). Output is then always valid JSON in this exact shape.
    public const string Schema = """
{"type":"object","additionalProperties":false,"required":["steps","say"],"properties":{
 "steps":{"type":"array","maxItems":6,"items":{"type":"object","additionalProperties":false,"required":["tool","target","text"],"properties":{
   "tool":{"type":"string","enum":["focus_window","open_app","open_url","web_search","click","type_text","press","scroll","ask"]},
   "target":{"type":"string","maxLength":100},
   "text":{"type":"string","maxLength":100}}}},
 "say":{"type":"string","maxLength":100}}}
""";

    public static readonly ResponseFormatExtended Format = new() { Type = "lark_grammar", LarkGrammar = "start: %json " + Schema.Replace("\r", "").Replace("\n", "") };
    public record Step(string Tool, string Target, string Text);

    public record Case(string Request, string Screen, Func<List<Step>, string, bool> Check, int MaxSteps = 3);

    private static bool Has(List<Step> s, string tool, string contains = "") =>
        s.Any(x => x.Tool == tool && (x.Target + " " + x.Text).Contains(contains, StringComparison.OrdinalIgnoreCase));

    private static int Index(List<Step> s, string tool, string contains = "") =>
        s.FindIndex(x => x.Tool == tool && (x.Target + " " + x.Text).Contains(contains, StringComparison.OrdinalIgnoreCase));

    private const string Desk = """
Windows (front first):
1. "Windows PowerShell" (PowerShell), right half, in front
2. "Home - YouTube - Google Chrome" (Chrome), left half
3. "WhatsApp" (WhatsApp), minimized
""";

    public static readonly Case[] Cases =
    [
        new("open the MrBeast video on YouTube", Desk + """
Items in "Home - YouTube - Google Chrome":
link "Home"; link "Shorts"; link "Subscriptions"; button "Search"; edit "Search";
link "I Survived 50 Hours In Antarctica - MrBeast - 98M views";
link "Why Is This Bridge Twisted? - Veritasium - 4.1M views";
link "Lofi hip hop radio - beats to relax/study to - Lofi Girl";
link "How I Made My First App - Fireship - 1.2M views"
Items in "Windows PowerShell": edit "Command line"
""", (s, _) => Has(s, "click", "MrBeast") && Index(s, "focus_window", "YouTube") is var f && f >= 0 && f < Index(s, "click", "MrBeast")),

        new("play the second video", """
Windows (front first):
1. "lofi - YouTube - Google Chrome" (Chrome), maximized, in front
Items in "lofi - YouTube - Google Chrome":
edit "Search"; button "Search";
link "lofi hip hop radio 📚 - beats to relax/study to - Lofi Girl";
link "Chill Lofi Mix [chill lo-fi hip hop beats] - Chillhop Music";
link "1 A.M Study Session 📚 - [lofi hip hop/chill beats] - Lofi Girl"
""", (s, _) => Has(s, "click", "Chill Lofi Mix")),

        new("search YouTube for lofi music", Desk + """
Items in "Home - YouTube - Google Chrome": edit "Search"; button "Search"; link "Home"
""", (s, _) => Has(s, "web_search", "lofi") || (Has(s, "type_text", "lofi") && Has(s, "click", "Search"))),

        new("reply thanks to Rahul on WhatsApp", """
Windows (front first):
1. "Inbox - Outlook" (Outlook), maximized, in front
2. "WhatsApp" (WhatsApp), behind
Items in "WhatsApp":
edit "Search or start a new chat"; listitem "Rahul Sharma, Are we still on for tomorrow?, 10:42";
listitem "Mom, Call me, 09:15"; listitem "Project Team, Priya: deck is ready, Yesterday"; edit "Type a message"
""", (s, _) => Has(s, "focus_window", "WhatsApp") && Has(s, "click", "Rahul") && Has(s, "type_text", "thank")),

        new("open notepad and write buy milk", Desk, (s, _) => Has(s, "open_app", "notepad") && Has(s, "type_text", "buy milk")),

        new("close this tab", """
Windows (front first):
1. "Pull requests · shivam098/thumbfree - Google Chrome" (Chrome), maximized, in front
Items in "Pull requests · shivam098/thumbfree - Google Chrome": tab "Pull requests · shivam098/thumbfree"; tab "Gmail"; button "Close"
""", (s, _) => Has(s, "press", "ctrl+w") || Has(s, "press", "ctrl + w")),

        new("go to the GitHub tab", """
Windows (front first):
1. "Gmail - Inbox - Google Chrome" (Chrome), maximized, in front
Items in "Gmail - Inbox - Google Chrome": tab "Gmail - Inbox"; tab "Pull requests · shivam098/thumbfree"; tab "YouTube"; button "Compose"
""", (s, _) => Has(s, "click", "Pull requests")),

        new("scroll down", Desk, (s, _) => Has(s, "scroll", "down")),

        new("what's the weather in Bangalore", Desk, (s, _) => Has(s, "web_search", "weather") || Has(s, "open_url", "weather")),

        new("mute the video", """
Windows (front first):
1. "I Survived 50 Hours In Antarctica - YouTube - Google Chrome" (Chrome), maximized, in front
Items in "I Survived 50 Hours In Antarctica - YouTube - Google Chrome":
button "Pause (k)"; button "Mute (m)"; button "Next (SHIFT+n)"; button "Full screen (f)"; button "Subscribe"; button "Like"
""", (s, _) => Has(s, "click", "Mute") || (Has(s, "press", "m") && s.Count == 1)),

        new("open the latest pull request", """
Windows (front first):
1. "Pull requests · shivam098/thumbfree - Google Chrome" (Chrome), maximized, in front
Items in "Pull requests · shivam098/thumbfree - Google Chrome":
link "Android: History #5 opened 2 days ago"; link "Android: live typing #4 opened 3 days ago"; link "Tap to start, tap to finish #3 opened 4 days ago"; button "New pull request"
""", (s, _) => Has(s, "click", "History")),

        new("switch to PowerShell", """
Windows (front first):
1. "Home - YouTube - Google Chrome" (Chrome), maximized, in front
2. "Windows PowerShell" (PowerShell), behind
""", (s, _) => Has(s, "focus_window", "PowerShell") && !Has(s, "open_app")),

        new("play next", """
Windows (front first):
1. "I Survived 50 Hours In Antarctica - YouTube - Google Chrome" (Chrome), maximized, in front
Items in "I Survived 50 Hours In Antarctica - YouTube - Google Chrome": button "Pause (k)"; button "Mute (m)"; button "Next (SHIFT+n)"
""", (s, _) => Has(s, "click", "Next") || Has(s, "press", "shift+n")),

        new("delete this email", """
Windows (front first):
1. "Weekly newsletter - Outlook" (Outlook), maximized, in front
Items in "Weekly newsletter - Outlook": button "Reply"; button "Forward"; button "Delete"; button "Archive"
""", (s, _) => Has(s, "click", "Delete")),

        new("open report.docx from downloads", """
Windows (front first):
1. "Downloads - File Explorer" (Explorer), maximized, in front
Items in "Downloads - File Explorer": listitem "report.docx"; listitem "invoice-oct.pdf"; listitem "photo.jpg"; button "New"
""", (s, _) => Has(s, "click", "report.docx")),

        new("tell me a joke", Desk, (s, say) => s.Count == 0 && say.Length > 10),

        new("maximize this window", Desk, (s, _) => Has(s, "press", "win+up")),

        new("open the Veritasium video about black holes", """
Windows (front first):
1. "Home - YouTube - Google Chrome" (Chrome), maximized, in front
Items in "Home - YouTube - Google Chrome": edit "Search"; button "Search"; link "I Survived 50 Hours In Antarctica - MrBeast"; link "Lofi hip hop radio - Lofi Girl"
""", (s, _) => Has(s, "web_search", "Veritasium") || (Has(s, "type_text", "Veritasium") && Index(s, "type_text", "Veritasium") < Math.Max(Index(s, "click", "Veritasium"), 99))),

        new("send it", """
Windows (front first):
1. "WhatsApp" (WhatsApp), maximized, in front
Items in "WhatsApp": listitem "Rahul Sharma, Are we still on for tomorrow?"; edit "Type a message"; button "Send"
""", (s, _) => Has(s, "click", "Send") || Has(s, "press", "enter")),

        new("do the thing", Desk, (s, _) => Has(s, "ask") && s.Count == 1),
    ];

    public static async Task<(int Passed, double MedianMs, double MaxMs)> RunAsync(IModel model, bool verbose)
    {
        var chat = await model.GetChatClientAsync();
        chat.Settings.Temperature = 0f;
        chat.Settings.MaxTokens = 400;
        chat.Settings.ResponseFormat = Format;
        chat.Settings.ToolChoice = ToolChoice.None; // a JSON schema is only enforced when no tool call is allowed
        var times = new List<double>();
        var passed = 0;
        var clean = 0;
        foreach (var c in Cases)
        {
            var user = $"Screen:\n{c.Screen}\nRequest: \"{c.Request}\"";
            var sw = Stopwatch.StartNew();
            var r = await chat.CompleteChatAsync(Messages(user));
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            var raw = r.Successful ? r.Choices![0].Message.Content ?? "" : "ERROR " + r.Error?.Message;
            List<Step> steps = [];
            var say = "";
            var ok = false;
            try
            {
                var a = raw.IndexOf('{'); var b = raw.LastIndexOf('}');
                using var doc = JsonDocument.Parse(a >= 0 && b > a ? raw[a..(b + 1)] : raw);
                foreach (var e in doc.RootElement.GetProperty("steps").EnumerateArray())
                    steps.Add(new(e.GetProperty("tool").GetString()!, e.GetProperty("target").GetString() ?? "", e.GetProperty("text").GetString() ?? ""));
                say = doc.RootElement.GetProperty("say").GetString() ?? "";
                ok = c.Check(steps, say);
            }
            catch (Exception e)
            {
                raw = "BAD JSON (" + e.Message + "): " + raw;
            }

            if (ok) passed++;
            if (ok && steps.Count <= c.MaxSteps) clean++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {sw.ElapsedMilliseconds,6} ms  {c.Request}");
            if (verbose || !ok)
                Console.WriteLine("        " + (steps.Count > 0 || say.Length > 0 ? string.Join(" → ", steps.Select(x => $"{x.Tool}({x.Target}{(x.Text.Length > 0 ? ", " + x.Text : "")})")) + $"  say: {say}" : raw));
        }

        times.Sort();
        Console.WriteLine($"  clean (correct and no extra steps): {clean}/{Cases.Length}");
        return (passed, times[times.Count / 2], times[^1]);
    }
}




