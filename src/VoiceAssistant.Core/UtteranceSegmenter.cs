namespace VoiceAssistant.Core;

public sealed record SegmenterOptions
{
    public int SampleRate { get; init; } = 16000;

    public int FrameMs { get; init; } = 30;

    /// <summary>Silence that ends an utterance. Commands must be separated from dictation by at least this pause.</summary>
    public int EndSilenceMs { get; init; } = 600;

    public int PreRollMs { get; init; } = 300;

    public int MinSpeechMs { get; init; } = 200;

    /// <summary>Hard cap; must stay below the recognizer's audio context (768 tokens = 15.36 s).</summary>
    public int MaxUtteranceMs { get; init; } = 14000;

    /// <summary>Consecutive loud frames required to start an utterance.</summary>
    public int StartFrames { get; init; } = 3;

    /// <summary>Speech must exceed the adaptive noise floor by this much.</summary>
    public double StartAboveFloorDb { get; init; } = 12;

    /// <summary>Absolute minimum level for speech, so near-silent rooms do not trigger on hiss.</summary>
    public double MinSpeechDbfs { get; init; } = -48;
}

/// <param name="Samples">16 kHz mono audio.</param>
/// <param name="FollowsForcedCut">
/// True when the previous utterance was cut at the length cap mid-speech, so this one is the
/// continuation of a sentence and must never be interpreted as a standalone command.
/// </param>
public sealed record Utterance(float[] Samples, bool FollowsForcedCut);

/// <summary>
/// Energy-based voice activity detector that splits a 16 kHz mono stream into pause-delimited utterances.
/// Not thread-safe: feed from a single capture thread.
/// </summary>
public sealed class UtteranceSegmenter
{
    private readonly SegmenterOptions _o;
    private readonly int _frameSamples;
    private readonly float[] _frame;
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float> _utterance = [];
    private int _frameFill;
    private bool _inSpeech;
    private bool _afterForcedCut;
    private int _quietFrames;
    private int _loudRun;
    private int _silentFrames;
    private int _speechFrames;
    private double _noiseFloorDb = -60;

    public UtteranceSegmenter(SegmenterOptions? options = null)
    {
        _o = options ?? new SegmenterOptions();
        _frameSamples = _o.SampleRate * _o.FrameMs / 1000;
        _frame = new float[_frameSamples];
    }

    public event Action<Utterance>? UtteranceReady;

    public event Action? SpeechStarted;

    public bool InSpeech => _inSpeech;

    public double NoiseFloorDb => _noiseFloorDb;

    public void Reset()
    {
        _preRoll.Clear();
        _utterance.Clear();
        _frameFill = 0;
        _inSpeech = false;
        _afterForcedCut = false;
        _quietFrames = 0;
        _loudRun = 0;
        _silentFrames = 0;
        _speechFrames = 0;
    }

    public void Push(ReadOnlySpan<float> samples)
    {
        while (samples.Length > 0)
        {
            var take = Math.Min(_frameSamples - _frameFill, samples.Length);
            samples[..take].CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            samples = samples[take..];
            if (_frameFill == _frameSamples)
            {
                ProcessFrame((float[])_frame.Clone());
                _frameFill = 0;
            }
        }
    }

    /// <summary>Emits any in-progress utterance (used when listening stops).</summary>
    public void Flush()
    {
        if (_inSpeech)
        {
            Emit(forcedCut: false);
        }

        Reset();
    }

    public static double LevelDbfs(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (var s in frame)
        {
            sum += s * s;
        }

        var rms = Math.Sqrt(sum / Math.Max(1, frame.Length));
        return 20 * Math.Log10(Math.Max(rms, 1e-9));
    }

    private void ProcessFrame(float[] frame)
    {
        var db = LevelDbfs(frame);
        var startThreshold = Math.Max(_noiseFloorDb + _o.StartAboveFloorDb, _o.MinSpeechDbfs);
        var continueThreshold = Math.Max(_noiseFloorDb + _o.StartAboveFloorDb / 2, _o.MinSpeechDbfs - 6);

        if (!_inSpeech)
        {
            _preRoll.Enqueue(frame);
            while (_preRoll.Count > _o.PreRollMs / _o.FrameMs)
            {
                _preRoll.Dequeue();
            }

            if (db >= startThreshold)
            {
                _loudRun++;
                if (_loudRun >= _o.StartFrames)
                {
                    _inSpeech = true;
                    _silentFrames = 0;
                    _speechFrames = _loudRun;
                    foreach (var f in _preRoll)
                    {
                        _utterance.AddRange(f);
                    }

                    _preRoll.Clear();
                    SpeechStarted?.Invoke();
                }
            }
            else
            {
                _loudRun = 0;
                // Track the floor quickly downward and slowly upward so speech does not raise it.
                var rate = db < _noiseFloorDb ? 0.2 : 0.02;
                _noiseFloorDb += (db - _noiseFloorDb) * rate;

                // A real pause after a length-capped cut means the next phrase is a fresh utterance again.
                if (_afterForcedCut && ++_quietFrames * _o.FrameMs >= _o.EndSilenceMs)
                {
                    _afterForcedCut = false;
                }
            }

            return;
        }

        _utterance.AddRange(frame);
        if (db >= continueThreshold)
        {
            _silentFrames = 0;
            _speechFrames++;
        }
        else
        {
            _silentFrames++;
        }

        var durationMs = _utterance.Count * 1000 / _o.SampleRate;
        if (_silentFrames * _o.FrameMs >= _o.EndSilenceMs)
        {
            Emit(forcedCut: false);
        }
        else if (durationMs >= _o.MaxUtteranceMs)
        {
            Emit(forcedCut: true);
        }
    }

    private void Emit(bool forcedCut)
    {
        var enoughSpeech = _speechFrames * _o.FrameMs >= _o.MinSpeechMs;
        var utterance = new Utterance(_utterance.ToArray(), _afterForcedCut);
        // Silence already at the end of a capped utterance counts toward the pause that clears the flag.
        _afterForcedCut = forcedCut && _silentFrames * _o.FrameMs < _o.EndSilenceMs;
        _quietFrames = _silentFrames;
        _utterance.Clear();
        _inSpeech = false;
        _loudRun = 0;
        _silentFrames = 0;
        _speechFrames = 0;
        if (enoughSpeech)
        {
            UtteranceReady?.Invoke(utterance);
        }
    }
}
