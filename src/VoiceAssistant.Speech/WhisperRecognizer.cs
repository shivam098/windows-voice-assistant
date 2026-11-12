using System.Diagnostics;
using Whisper.net;

namespace VoiceAssistant.Speech;

public sealed record RecognizerOptions
{
    public int Threads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>Whisper encoder context (0 = model default, 1500 = 30 s). Smaller is faster but caps utterance length.</summary>
    public int AudioContextSize { get; init; }

    /// <summary>Optional vocabulary hint. Keep short: Whisper can echo prompts on near-silent input.</summary>
    public string? Prompt { get; init; }

    /// <summary>Stop after this many tokens (0 = no limit). The standby wake-word check only needs the first few words.</summary>
    public int MaxTokens { get; init; }

    /// <summary>
    /// Re-decode at higher temperatures when the first attempt looks unreliable (whisper.cpp's default; up to five extra
    /// passes). Off for the wake-word check: unclear background speech is exactly where retries cost most and help least.
    /// </summary>
    public bool TemperatureFallback { get; init; } = true;
}

public sealed record TranscriptionResult(string Text, float Probability, TimeSpan Elapsed);

public sealed class WhisperRecognizer : IDisposable
{
    public const int SampleRate = 16000;

    // whisper.cpp refuses input shorter than 1000 ms, which would drop one-word commands like "send".
    private const int MinSamples = SampleRate * 11 / 10;

    private readonly WhisperFactory _factory;
    private readonly bool _ownsFactory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WhisperRecognizer(string modelPath, RecognizerOptions? options = null)
        : this(LoadFactory(modelPath), ownsFactory: true, options ?? new RecognizerOptions())
    {
    }

    private WhisperRecognizer(WhisperFactory factory, bool ownsFactory, RecognizerOptions options)
    {
        _factory = factory;
        _ownsFactory = ownsFactory;
        var builder = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(options.Threads)
            .WithNoContext()
            .WithSingleSegment()
            .WithProbabilities();

        if (options.AudioContextSize > 0)
        {
            builder = builder.WithAudioContextSize(options.AudioContextSize);
        }

        if (!string.IsNullOrWhiteSpace(options.Prompt))
        {
            builder = builder.WithPrompt(options.Prompt);
        }

        if (options.MaxTokens > 0)
        {
            builder = builder.WithMaxTokensPerSegment(options.MaxTokens);
        }

        if (!options.TemperatureFallback)
        {
            builder = builder.WithTemperatureInc(0);
        }

        _processor = builder.Build();
    }

    /// <summary>
    /// Another recognizer on the same loaded model with different settings (no second copy of the model in memory).
    /// Dispose it before this one.
    /// </summary>
    public WhisperRecognizer WithOptions(RecognizerOptions options) => new(_factory, ownsFactory: false, options);

    private static WhisperFactory LoadFactory(string modelPath) =>
        File.Exists(modelPath) ? WhisperFactory.FromPath(modelPath) : throw new FileNotFoundException("Speech model not found.", modelPath);

    public async Task<TranscriptionResult> TranscribeAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var input = samples;
        if (input.Length < MinSamples)
        {
            input = new float[MinSamples];
            Array.Copy(samples, input, samples.Length);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var parts = new List<string>();
            float probability = 0;
            var count = 0;
            await foreach (var segment in _processor.ProcessAsync(input, cancellationToken).ConfigureAwait(false))
            {
                parts.Add(segment.Text);
                probability += segment.Probability;
                count++;
            }

            return new TranscriptionResult(string.Concat(parts).Trim(), count == 0 ? 0 : probability / count, sw.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // Whisper.net returns from a cancelled ProcessAsync while native work is still running, and its
        // synchronous Dispose throws in that state. DisposeAsync waits for the native work; run it off the
        // caller's thread (it may be the UI thread) and give up after a timeout rather than hang or crash.
        if (!_gate.Wait(TimeSpan.FromSeconds(5)))
        {
            return;
        }

        try
        {
            var disposed = Task.Run(async () => await _processor.DisposeAsync().ConfigureAwait(false)).Wait(TimeSpan.FromSeconds(5));
            if (disposed && _ownsFactory)
            {
                _factory.Dispose();
            }
        }
        catch (AggregateException)
        {
            // Native resources are reclaimed at process exit.
        }
    }
}
