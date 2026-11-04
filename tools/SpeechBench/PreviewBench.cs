using System.Diagnostics;
using VoiceAssistant.Core;
using VoiceAssistant.Speech;

/// <summary>
/// Replays dictation clips in real time through <see cref="DictationPreview"/> the way the app does while
/// Ctrl+Win is held, printing each live update and how long the final transcription waits after release.
/// </summary>
internal static class PreviewBench
{
    private const int Rate = WhisperRecognizer.SampleRate;

    public static async Task RunAsync(string modelsDir, string audioDir, string model, int intervalMs = 600)
    {
        string[] clips = ["dict-refactor", "dict-send-logs", "dict-pr", "dict-long"];
        var audio = new List<float>();
        foreach (var clip in clips)
        {
            audio.AddRange(WavFile.ReadPcm16Mono16k(Path.Combine(audioDir, clip + ".wav")));
            audio.AddRange(new float[Rate * 6 / 10]);
        }

        var samples = audio.ToArray();
        using var recognizer = new WhisperRecognizer(Path.Combine(modelsDir, model), new RecognizerOptions { Threads = 8, AudioContextSize = 768 });
        using var detector = new SpeechDetector(Path.Combine(modelsDir, "ggml-silero-v6.2.0.bin"));
        await recognizer.TranscribeAsync(new float[Rate]);
        Console.WriteLine($"{samples.Length / Rate:F0} s of dictation, preview every {intervalMs} ms");

        var recorded = 0;
        var gate = new object();
        var clock = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource();
        var recorder = Task.Run(async () =>
        {
            while (true)
            {
                lock (gate)
                {
                    recorded = Math.Min(samples.Length, (int)(clock.Elapsed.TotalSeconds * Rate));
                    if (recorded == samples.Length)
                    {
                        return;
                    }
                }

                await Task.Delay(20);
            }
        });

        var preview = new DictationPreview(
            async a => TranscriptText.Clean((await recognizer.TranscribeAsync(a)).Text),
            detector.Detect,
            Rate);
        var latencies = new List<double>();
        var loop = Task.Run(async () =>
        {
            var seen = 0;
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(intervalMs);
                int now;
                lock (gate)
                {
                    now = recorded;
                }

                if (now - seen < Rate * 3 / 10)
                {
                    continue;
                }

                seen = now;
                var sw = Stopwatch.StartNew();
                var text = await preview.UpdateAsync(samples[preview.CommittedSamples..now]);
                latencies.Add(sw.Elapsed.TotalMilliseconds);
                Console.WriteLine($"  t={clock.Elapsed.TotalSeconds,5:F1}s heard={now / (double)Rate,5:F1}s update={sw.ElapsedMilliseconds,4} ms  \"{Tail(text)}\"");
            }
        });

        await recorder;
        var released = Stopwatch.StartNew();
        stop.Cancel();
        await loop;
        var waited = released.ElapsedMilliseconds;
        var parts = new List<string>();
        foreach (var chunk in SpeechSegments.Chunk(samples, detector.Detect(samples), Rate, 14000, 200))
        {
            parts.Add(TranscriptText.Clean((await recognizer.TranscribeAsync(chunk)).Text));
        }

        latencies.Sort();
        Console.WriteLine($"updates={latencies.Count} median={latencies[latencies.Count / 2]:F0} ms max={latencies[^1]:F0} ms");
        Console.WriteLine($"after release: waited {waited} ms for the in-flight preview, final transcription {released.ElapsedMilliseconds - waited} ms");
        Console.WriteLine($"final: \"{string.Join(" ", parts)}\"");
    }

    private static string Tail(string text) => text.Length <= 90 ? text : "…" + text[^90..];
}
