using System.Collections.Concurrent;
using NAudio.Wave;
using VoiceAssistant.Core;
using VoiceAssistant.Speech;

/// <summary>
/// Captures the real microphone for N seconds and prints, per utterance, the segmenter's view
/// (length, levels, noise floor), the transcript, and how the parser would route it.
/// Transcripts are printed to the console only — never written to disk.
/// </summary>
internal static class LiveDiagnostics
{
    public static async Task RunAsync(int seconds, string model, string? savePath = null)
    {
        var modelsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAssistant", "models");
        using var recognizer = new WhisperRecognizer(Path.Combine(modelsDir, model), new RecognizerOptions { Threads = 8, AudioContextSize = 768 });
        var segmenter = new UtteranceSegmenter();
        var utterances = new BlockingCollection<(Utterance U, double Floor)>();
        var levels = new List<double>();
        segmenter.UtteranceReady += u => utterances.Add((u, segmenter.NoiseFloorDb));
        using var writer = savePath is null ? null : new WaveFileWriter(savePath, new WaveFormat(16000, 16, 1));

        using var waveIn = new WaveInEvent { DeviceNumber = -1, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 30 };
        waveIn.DataAvailable += (_, e) =>
        {
            writer?.Write(e.Buffer, 0, e.BytesRecorded);
            var samples = new float[e.BytesRecorded / 2];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            }

            lock (levels)
            {
                levels.Add(UtteranceSegmenter.LevelDbfs(samples));
            }

            segmenter.Push(samples);
        };

        Console.WriteLine($"Device: {WaveInEvent.GetCapabilities(0).ProductName} (default mapper). Listening {seconds}s…");
        waveIn.StartRecording();
        var parser = new CommandParser();
        var mode = InteractionMode.Command;
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end || utterances.Count > 0)
        {
            if (!utterances.TryTake(out var item, 250))
            {
                continue;
            }

            var u = item.U;
            var frameDb = Enumerable.Range(0, u.Samples.Length / 480).Select(i => UtteranceSegmenter.LevelDbfs(u.Samples.AsSpan(i * 480, 480))).OrderBy(d => d).ToList();
            var result = await recognizer.TranscribeAsync(u.Samples);
            var parsed = parser.Parse(result.Text, mode);
            var outcome = parsed.DictationText is not null ? "TYPE"
                : parsed.Commands.Count > 0 ? "CMD " + string.Join("+", parsed.Commands.Select(c => c.Kind + (c.Argument is null ? string.Empty : $"({c.Argument})")))
                : parsed.Unrecognized is not null ? $"UNRECOGNIZED norm=\"{parsed.Unrecognized}\"" : "IGNORED";
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {u.Samples.Length / 16,5}ms floor={item.Floor,6:F1}dB frames p10={frameDb[frameDb.Count / 10],6:F1} p50={frameDb[frameDb.Count / 2],6:F1} p90={frameDb[frameDb.Count * 9 / 10],6:F1} cont={u.FollowsForcedCut} p={result.Probability:F2}");
            Console.WriteLine($"         raw=\"{result.Text}\" -> {outcome}");
            if (parsed.Commands.Any(c => c.Kind == CommandKind.StartTyping)) mode = InteractionMode.Dictation;
            if (parsed.Commands.Any(c => c.Kind is CommandKind.Send or CommandKind.StopTyping)) mode = InteractionMode.Command;
        }

        waveIn.StopRecording();
        writer?.Flush();
        List<double> all;
        lock (levels)
        {
            all = levels.OrderBy(d => d).ToList();
        }

        if (all.Count > 0)
        {
            Console.WriteLine($"Whole session frame levels: p10={all[all.Count / 10]:F1} p50={all[all.Count / 2]:F1} p90={all[all.Count * 9 / 10]:F1} max={all[^1]:F1} dBFS; final floor={segmenter.NoiseFloorDb:F1}");
        }
    }

    /// <summary>Runs a recorded 16 kHz mono WAV through the segmenter, recognizer and parser in real-time-sized chunks.</summary>
    public static Task ReplayAsync(string wavPath, string model) => ReplaySamplesAsync(WavFile.ReadPcm16Mono16k(wavPath), model);

    /// <summary>
    /// Overlays clean command/dictation clips (scaled to a typical close-talk level) onto recorded room noise
    /// with ~1 s pauses, then replays the mix. Prints the expected script for comparison.
    /// </summary>
    public static Task MixAsync(string noiseWav, string audioDir, double speechDb, string model)
    {
        string[] script =
        [
            "wake-hey-jarvis", "cmd-open-github-copilot", "cmd-start-typing", "dict-send-logs", "dict-refactor", "cmd-send",
            "wake-open-notepad-type", "dict-pr", "cmd-scratch-that", "cmd-stop-typing",
        ];
        var noise = WavFile.ReadPcm16Mono16k(noiseWav);
        var mix = (float[])noise.Clone();
        var position = 16000 * 2;
        var random = new Random(3);
        Console.WriteLine("Script:");
        foreach (var name in script)
        {
            var clip = WavFile.ReadPcm16Mono16k(Path.Combine(audioDir, name + ".wav"));
            var gain = Math.Pow(10, (speechDb - LoudLevel(clip)) / 20);
            if (position + clip.Length >= mix.Length)
            {
                break;
            }

            for (var i = 0; i < clip.Length; i++)
            {
                mix[position + i] = Math.Clamp(mix[position + i] + (float)(clip[i] * gain), -1f, 1f);
            }

            Console.WriteLine($"  {position / 16000.0,5:F1}s-{(position + clip.Length) / 16000.0,5:F1}s {name}");
            position += clip.Length + 16000 * (900 + random.Next(0, 500)) / 1000;
        }

        return ReplaySamplesAsync(mix, model);
    }

    private static double LoudLevel(float[] clip)
    {
        var frames = Enumerable.Range(0, clip.Length / 480).Select(i => UtteranceSegmenter.LevelDbfs(clip.AsSpan(i * 480, 480))).OrderBy(d => d).ToList();
        return frames[frames.Count * 9 / 10];
    }

    private static async Task ReplaySamplesAsync(float[] samples, string model)
    {
        var modelsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAssistant", "models");
        using var recognizer = new WhisperRecognizer(Path.Combine(modelsDir, model), new RecognizerOptions { Threads = 8, AudioContextSize = 768 });
        var segmenter = new UtteranceSegmenter();
        var found = new List<(Utterance U, double Floor, double AtSec)>();
        var pushed = 0;
        segmenter.UtteranceReady += u => found.Add((u, segmenter.NoiseFloorDb, pushed / 16000.0));
        for (var i = 0; i < samples.Length; i += 480)
        {
            var n = Math.Min(480, samples.Length - i);
            pushed += n;
            segmenter.Push(samples.AsSpan(i, n));
        }

        var parser = new CommandParser();
        var mode = InteractionMode.Command;
        using var detector = new SpeechDetector(Path.Combine(modelsDir, "ggml-silero-v6.2.0.bin"));
        Console.WriteLine("Segmented:");
        foreach (var (whole, floor, at) in found)
        {
            var pieces = SpeechSegments.Split(whole.Samples, detector.Detect(whole.Samples), 16000, 600, 250);
            if (pieces.Count == 0)
            {
                Console.WriteLine($"  ends@{at,5:F1}s {whole.Samples.Length / 16,5}ms -> no speech (dropped by VAD)");
                continue;
            }

            foreach (var piece in pieces)
            {
                var u = whole with { Samples = piece };
                var result = await recognizer.TranscribeAsync(u.Samples);
                var parsed = parser.Parse(result.Text, mode);
                var outcome = parsed.DictationText is not null ? "TYPE"
                    : parsed.Commands.Count > 0 ? "CMD " + string.Join("+", parsed.Commands.Select(c => c.Kind + (c.Argument is null ? string.Empty : $"({c.Argument})")))
                    : parsed.Unrecognized is not null ? $"UNRECOGNIZED \"{parsed.Unrecognized}\"" : "IGNORED";
                Console.WriteLine($"  ends@{at,5:F1}s {u.Samples.Length / 16,5}ms{(pieces.Count > 1 ? " (split)" : string.Empty)} \"{result.Text}\" -> {outcome}");
                if (parsed.Commands.Any(c => c.Kind == CommandKind.StartTyping)) mode = InteractionMode.Dictation;
                if (parsed.Commands.Any(c => c.Kind is CommandKind.Send or CommandKind.StopTyping)) mode = InteractionMode.Command;
            }
        }
    }
}
