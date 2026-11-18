using System.Diagnostics;
using AionInstructPreview.Text;

// AionBench [-v]: BrainSpike's 20 cases on Aion Instruct Preview. The preview API has no JSON-constrained decoding,
// so the instructions and examples go in the system prompt and the reply is parsed leniently by Bench.
AionFramework.Load();
var verbose = args.Contains("-v");
var load = Stopwatch.StartNew();
Console.WriteLine("loading Aion Instruct Preview (the first run compiles the NPU cache and can take minutes)...");
LanguageModel model;
try { model = await LanguageModel.CreateAsync(); }
finally
{
    // Native initialization can replace the standard handles (as in Microsoft's sample); reopen them.
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
    Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
}
using var modelScope = model;
Console.WriteLine($"== aion: loaded in {load.ElapsedMilliseconds} ms");
var options = new LanguageModelOptions { Temperature = 0.1f, TopP = 0.9f, TopK = 1 };

async Task<string> Generate(List<(string Role, string Content)> msgs)
{
    using var context = model.CreateContext(Bench.SystemWithExamples);
    var result = await model.GenerateResponseAsync(context, msgs[^1].Content, options);
    return result.Status == LanguageModelResponseStatus.Complete ? result.Text : $"ERROR {result.Status} {result.Text}";
}

// Warm-up so the timed cases measure steady state rather than first-call setup.
var warm = Stopwatch.StartNew();
await Generate([("user", "Request: \"open notepad\"")]);
Console.WriteLine($"== aion: warm-up {warm.ElapsedMilliseconds} ms");
var (passed, median, max) = await Bench.RunAsync(Generate, verbose);
Console.WriteLine($"== aion: {passed}/{Bench.Cases.Length} passed, median {median:F0} ms, slowest {max:F0} ms");
