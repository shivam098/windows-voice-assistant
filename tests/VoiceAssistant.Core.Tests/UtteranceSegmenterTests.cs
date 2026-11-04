using VoiceAssistant.Core;

namespace VoiceAssistant.Core.Tests;

public class UtteranceSegmenterTests
{
    private const int Rate = 16000;

    private static float[] Silence(int ms, float level = 0.0005f)
    {
        var random = new Random(ms);
        return Enumerable.Range(0, Rate * ms / 1000).Select(_ => (float)((random.NextDouble() * 2 - 1) * level)).ToArray();
    }

    private static float[] Tone(int ms, float amplitude = 0.2f) =>
        Enumerable.Range(0, Rate * ms / 1000).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 220 * i / Rate))).ToArray();

    private static List<float[]> Run(UtteranceSegmenter segmenter, params float[][] chunks)
    {
        var output = new List<float[]>();
        segmenter.UtteranceReady += u => output.Add(u.Samples);
        foreach (var chunk in chunks)
        {
            // Feed in odd-sized pieces to exercise frame reassembly.
            for (var i = 0; i < chunk.Length; i += 777)
            {
                segmenter.Push(chunk.AsSpan(i, Math.Min(777, chunk.Length - i)));
            }
        }

        return output;
    }

    [Fact]
    public void Speech_followed_by_pause_emits_one_utterance()
    {
        var output = Run(new UtteranceSegmenter(), Silence(1000), Tone(800), Silence(900));
        var utterance = Assert.Single(output);
        Assert.InRange(utterance.Length, Rate * 800 / 1000, Rate * 1800 / 1000);
    }

    [Fact]
    public void Two_phrases_separated_by_a_long_pause_are_split()
    {
        var output = Run(new UtteranceSegmenter(), Silence(1000), Tone(1500), Silence(800), Tone(400), Silence(800));
        Assert.Equal(2, output.Count);
    }

    [Fact]
    public void Short_pause_inside_a_sentence_does_not_split()
    {
        var output = Run(new UtteranceSegmenter(), Silence(1000), Tone(700), Silence(300), Tone(700), Silence(900));
        Assert.Single(output);
    }

    [Fact]
    public void Clicks_shorter_than_minimum_speech_are_dropped()
    {
        Assert.Empty(Run(new UtteranceSegmenter(), Silence(1000), Tone(120), Silence(900)));
    }

    [Fact]
    public void Continuous_speech_is_capped()
    {
        var options = new SegmenterOptions { MaxUtteranceMs = 3000 };
        var output = Run(new UtteranceSegmenter(options), Silence(500), Tone(7000), Silence(900));
        Assert.True(output.Count >= 2);
        Assert.All(output, u => Assert.True(u.Length <= Rate * 3.1));
    }

    [Fact]
    public void Pre_roll_keeps_the_start_of_speech()
    {
        var output = Run(new UtteranceSegmenter(), Silence(1000), Tone(500), Silence(900));
        var first = Assert.Single(output);
        Assert.True(UtteranceSegmenter.LevelDbfs(first.AsSpan(0, 480)) < -40, "utterance should begin with pre-roll silence");
    }

    [Fact]
    public void Steady_background_noise_does_not_trigger()
    {
        Assert.Empty(Run(new UtteranceSegmenter(), Silence(5000, level: 0.003f)));
    }

    [Fact]
    public void Utterance_after_a_forced_cut_is_flagged_as_continuation()
    {
        var segmenter = new UtteranceSegmenter(new SegmenterOptions { MaxUtteranceMs = 3000 });
        var output = new List<Utterance>();
        segmenter.UtteranceReady += output.Add;
        segmenter.Push(Silence(500));
        segmenter.Push(Tone(4000));
        segmenter.Push(Silence(900));
        segmenter.Push(Tone(500));
        segmenter.Push(Silence(900));

        Assert.Equal([false, true, false], output.Select(u => u.FollowsForcedCut));
    }

    [Fact]
    public void Real_pause_after_a_forced_cut_clears_the_continuation_flag()
    {
        // Speech that ends just as the cap is hit, then a long pause, then "send".
        var segmenter = new UtteranceSegmenter(new SegmenterOptions { MaxUtteranceMs = 3000 });
        var output = new List<Utterance>();
        segmenter.UtteranceReady += output.Add;
        segmenter.Push(Silence(500));
        segmenter.Push(Tone(2750));
        segmenter.Push(Silence(2000));
        segmenter.Push(Tone(500));
        segmenter.Push(Silence(900));

        Assert.Equal(2, output.Count);
        Assert.False(output[1].FollowsForcedCut);
    }

    [Fact]
    public void Reset_discards_partial_speech()
    {
        var segmenter = new UtteranceSegmenter();
        var output = new List<float[]>();
        segmenter.UtteranceReady += u => output.Add(u.Samples);
        segmenter.Push(Silence(1000));
        segmenter.Push(Tone(600));
        segmenter.Reset();
        segmenter.Push(Silence(1000));
        Assert.Empty(output);
    }
}
