using System.Diagnostics;
using VoiceAssistant.Core;
using VoiceAssistant.Speech;

// Usage: SpeechBench [modelsDir] [audioDir] [model-file ...]
//        SpeechBench --pipeline [model-file]   (stream clips through segmenter + recognizer + parser)
if (args.Length > 0 && args[0] == "--pipeline")
{
    await RunPipelineAsync(args.Length > 1 ? args[1] : "ggml-small.en-q8_0.bin");
    return;
}

if (args.Length > 0 && args[0] == "--live")
{
    await LiveDiagnostics.RunAsync(int.Parse(args.Length > 1 ? args[1] : "30"), "ggml-small.en-q8_0.bin", args.Length > 2 ? args[2] : null);
    return;
}

if (args.Length > 1 && args[0] == "--replay")
{
    await LiveDiagnostics.ReplayAsync(args[1], args.Length > 2 ? args[2] : "ggml-small.en-q8_0.bin");
    return;
}

// --mix <noise.wav> [speechDbfs]: overlay clean clips onto recorded room noise and replay.
if (args.Length > 1 && args[0] == "--mix")
{
    await LiveDiagnostics.MixAsync(args[1], DefaultAudioDir(), args.Length > 2 ? double.Parse(args[2]) : -22, "ggml-small.en-q8_0.bin");
    return;
}

var modelsDir = args.Length > 0 ? args[0] : DefaultModelsDir();
var audioDir = args.Length > 1 ? args[1] : DefaultAudioDir();
var models = args.Length > 2 ? args[2..] : Directory.GetFiles(modelsDir, "*.bin").Select(f => Path.GetFileName(f)).ToArray();

var expected = File.ReadAllLines(Path.Combine(audioDir, "expected.tsv"))
    .Select(l => l.Split('\t'))
    .ToDictionary(p => p[0], p => p[1]);
var clips = expected.Keys.ToDictionary(k => k, k => WavFile.ReadPcm16Mono16k(Path.Combine(audioDir, k + ".wav")));

var configs = new (string Name, RecognizerOptions Options)[]
{
    ("t4-full", new RecognizerOptions { Threads = 4 }),
    ("t8-full", new RecognizerOptions { Threads = 8 }),
    ("t8-ctx768", new RecognizerOptions { Threads = 8, AudioContextSize = 768 }),
};

foreach (var model in models)
{
    foreach (var (name, options) in configs)
    {
        var loadSw = Stopwatch.StartNew();
        using var recognizer = new WhisperRecognizer(Path.Combine(modelsDir, model), options);
        await recognizer.TranscribeAsync(clips.First().Value);
        Console.WriteLine($"== {model} {name} (load+warmup {loadSw.ElapsedMilliseconds} ms)");

        var times = new List<double>();
        var exact = 0;
        foreach (var (key, samples) in clips)
        {
            var result = await recognizer.TranscribeAsync(samples);
            times.Add(result.Elapsed.TotalMilliseconds);
            var ok = Normalize(result.Text) == Normalize(expected[key]);
            if (ok)
            {
                exact++;
            }

            Console.WriteLine($"   {result.Elapsed.TotalMilliseconds,6:F0} ms  {samples.Length / 16000.0,4:F1}s  {(ok ? "OK " : "DIFF")} {key}: \"{result.Text}\"");
        }

        times.Sort();
        Console.WriteLine($"   median {times[times.Count / 2]:F0} ms, max {times[^1]:F0} ms, exact {exact}/{clips.Count}");
    }
}

static string Normalize(string s) => new string(s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();

static string DefaultModelsDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAssistant", "models");

static string DefaultAudioDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAssistant", "bench-audio");

static async Task RunPipelineAsync(string model)
{
    // Simulates: "open github copilot" ... "start typing" ... dictation ... "send" with natural pauses,
    // plus a mid-sentence "send" that must stay text.
    string[] script = ["cmd-open-github-copilot", "cmd-start-typing", "dict-refactor", "dict-send-logs", "cmd-scratch-that", "dict-pr", "cmd-send"];
    var stream = new List<float>();
    var random = new Random(7);
    void Pause(int ms) => stream.AddRange(Enumerable.Range(0, 16 * ms).Select(_ => (float)((random.NextDouble() * 2 - 1) * 0.001)));
    Pause(1200);
    foreach (var clip in script)
    {
        stream.AddRange(WavFile.ReadPcm16Mono16k(Path.Combine(DefaultAudioDir(), clip + ".wav")));
        Pause(900);
    }

    using var recognizer = new WhisperRecognizer(Path.Combine(DefaultModelsDir(), model), new RecognizerOptions { Threads = 8, AudioContextSize = 768 });
    var segmenter = new UtteranceSegmenter();
    var utterances = new List<float[]>();
    segmenter.UtteranceReady += u => utterances.Add(u.Samples);
    var samples = stream.ToArray();
    for (var i = 0; i < samples.Length; i += 480)
    {
        segmenter.Push(samples.AsSpan(i, Math.Min(480, samples.Length - i)));
    }

    var parser = new CommandParser();
    var mode = InteractionMode.Command;
    Console.WriteLine($"{script.Length} clips -> {utterances.Count} utterances");
    foreach (var utterance in utterances)
    {
        var result = await recognizer.TranscribeAsync(utterance);
        var parsed = parser.Parse(result.Text, mode);
        var outcome = parsed.DictationText is not null
            ? $"TYPE \"{parsed.DictationText}\""
            : parsed.Commands.Count > 0 ? "CMD " + string.Join(" + ", parsed.Commands.Select(c => c.Kind + (c.Argument is null ? string.Empty : $"({c.Argument})"))) : "IGNORED";
        Console.WriteLine($"  [{mode,-9}] {utterance.Length / 16:D5} ms audio, {result.Elapsed.TotalMilliseconds,4:F0} ms asr, p={result.Probability:F2}: {outcome}");
        if (parsed.Commands.Any(c => c.Kind == CommandKind.StartTyping))
        {
            mode = InteractionMode.Dictation;
        }
        else if (parsed.Commands.Any(c => c.Kind is CommandKind.Send or CommandKind.StopTyping))
        {
            mode = InteractionMode.Command;
        }
    }
}
