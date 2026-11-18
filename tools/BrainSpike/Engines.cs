using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.AI.Foundry.Local.OpenAI;

internal static class Engines
{
    /// <summary>Foundry Local. The runtime ignores the SDK's typed JSON schema, so it goes in through a Lark grammar.</summary>
    public static async Task<Func<List<(string Role, string Content)>, Task<string>>> FoundryAsync(IModel model)
    {
        var chat = await model.GetChatClientAsync();
        chat.Settings.Temperature = 0f;
        chat.Settings.MaxTokens = 400;
        chat.Settings.ResponseFormat = new ResponseFormatExtended { Type = "lark_grammar", LarkGrammar = "start: %json " + Bench.CompactSchema };
        chat.Settings.ToolChoice = ToolChoice.None;
        return async msgs =>
        {
            var r = await chat.CompleteChatAsync(msgs.Select(m => new ChatMessage { Role = m.Role, Content = m.Content }));
            return r.Successful ? r.Choices![0].Message.Content ?? "" : "ERROR " + r.Error?.Message;
        };
    }

    /// <summary>
    /// The plan schema as a llama.cpp grammar with no whitespace and a fixed key order. llama.cpp's JSON-schema grammar
    /// allows indentation, and Gemma 4 E4B uses it, which triples its output tokens and time.
    /// </summary>
    public const string CompactGrammar = """
root ::= "{\"steps\":[" (step ("," step){0,5})? "],\"say\":" str "}"
step ::= "{\"tool\":\"" tool "\",\"target\":" str ",\"text\":" str "}"
tool ::= "focus_window" | "open_app" | "open_url" | "web_search" | "click" | "type_text" | "press" | "scroll" | "ask"
str ::= "\"" char{0,100} "\""
char ::= [^"\\\x7F\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F]{4})
""";

    /// <summary>
    /// llama.cpp's llama-server (OpenAI-compatible). The plan shape is enforced by its grammar sampler (the compact
    /// grammar, or the JSON schema when <paramref name="schema"/> is set), and the shared prefix (instructions and
    /// examples) stays in its prompt cache between requests. Prints prompt vs generation time.
    /// </summary>
    public static Func<List<(string Role, string Content)>, Task<string>> LlamaServer(string url, bool thinking = true, bool schema = false)
    {
        var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(3) };
        var jsonSchema = JsonNode.Parse(Bench.CompactSchema);
        return async msgs =>
        {
            var body = new JsonObject
            {
                ["messages"] = new JsonArray(msgs.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
                ["temperature"] = 0,
                ["max_tokens"] = 300,
                ["cache_prompt"] = true,
                // Gemma 4 and Qwen3 templates think before answering unless this is off.
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = thinking },
            };
            if (schema)
                body["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "plan", ["schema"] = jsonSchema!.DeepClone() } };
            else
                body["grammar"] = CompactGrammar.Replace("\r", "");
            var resp = await http.PostAsJsonAsync("/v1/chat/completions", body);
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
            if (!resp.IsSuccessStatusCode) return "ERROR " + json.ToJsonString();
            var t = json["timings"];
            if (t != null)
                Console.WriteLine($"        prompt {t["prompt_n"]} tok in {t["prompt_ms"]:0} ms (cached {t["cache_n"]}), output {t["predicted_n"]} tok in {(double?)t["predicted_ms"] ?? 0:0} ms");
            return json["choices"]![0]!["message"]!["content"]!.GetValue<string>();
        };
    }
}
