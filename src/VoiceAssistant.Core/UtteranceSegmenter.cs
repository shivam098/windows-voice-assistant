namespace VoiceAssistant.Core;

public sealed record SegmenterOptions
{
    public int SampleRate { get; init; } = 16000;

    public int FrameMs { get; init; } = 30;

    /// <summary>Pause that ends an utterance. Commands must be separated from dictation by at least this pause.</summary>
    public int EndSilenceMs { get; init; } = 600;

    /// <summary>
    /// Fraction of frames in the trailing <see cref="EndSilenceMs"/> window that must be quiet to end an utterance.
    /// Below 1.0 so a burst of background music or a click during the pause doesn't keep the utterance open.
    /// </summary>
    public double EndQuietFraction { get; init; } = 0.8;

    public int PreRollMs { get; init; } = 300;

    public int MinSpeechMs { get; init; } = 200;

    /// <summary>Hard cap; must stay below the recognizer's audio context (768 tokens = 15.36 s).</summary>
    public int MaxUtteranceMs { get; init; } = 14000;

    /// <summary>Consecutive loud frames required to start an utterance.</summary>
    public int StartFrames { get; init; } = 3;

    /// <summary>Recent non-speech audio used to estimate the background level.</summary>
    public int BackgroundWindowMs { get; init; } = 5000;

    /// <summary>Speech must exceed the background level by this much to start an utterance.</summary>
    public double StartAboveBackgroundDb { get; init; } = 10;

    /// <summary>Frames this far above the background count as speech while an utterance is open.</summary>
    public double ContinueAboveBackgroundDb { get; init; } = 6;

    /// <summary>Absolute minimum level for speech, so near-silent rooms do not trigger on hiss.</summary>
    public double MinSpeechDbfs { get; init; } = -55;
}

/// <param name="Samples">16 kHz mono audio.</param>
/// <param name="FollowsForcedCut">
/// True when the previous utterance was cut at the length cap mid-speech, so this one is the
/// continuation of a sentence and must never be interpreted as a standalone command.
/// </param>
public sealed record Utterance(float[] Samples, bool FollowsForcedCut);

/// <summary>
/// Energy-based voice activity detector that splits a 16 kHz mono stream into pause-delimited utterances.
/// The background level is the median of recent non-speech frames, which stays meaningful with fans,
/// music, or meeting audio in the room (a minimum-tracking floor sits far below such noise and makes
/// every pause look like speech). Not thread-safe: feed from a single capture thread.
/// </summary>
public sealed class UtteranceSegmenter
{
    private const double DefaultBackgroundDb = -60;
    private const int WarmUpFrames = 10;
    private const double SteadySoundRangeDb = 12;

    private readonly SegmenterOptions _o;
    private readonly int _frameSamples;
    private readonly float[] _frame;
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float> _utterance = [];
    private readonly List<double> _utteranceLevels = [];
    private readonly double[] _background;
    private readonly double[] _sortScratch;
    private readonly bool[] _endWindow;
    private int _backgroundCount;
    private int _backgroundNext;
    private double _backgroundDb = DefaultBackgroundDb;
    private int _endWindowNext;
    private int _endWindowQuiet;
    private int _frameFill;
    private bool _inSpeech;
    private bool _afterForcedCut;
    private int _quietFrames;
    private int _loudRun;
    private int _speechFrames;
    private int _longPauseMs;
    private readonly List<bool> _quietFlags = [];

    public UtteranceSegmenter(SegmenterOptions? options = null)
    {
        _o = options ?? new SegmenterOptions();
        _frameSamples = _o.SampleRate * _o.FrameMs / 1000;
        _frame = new float[_frameSamples];
        _background = new double[Math.Max(1, _o.BackgroundWindowMs / _o.FrameMs)];
        _sortScratch = new double[_background.Length];
        _endWindow = new bool[Math.Max(1, _o.EndSilenceMs / _o.FrameMs)];
    }

    public event Action<Utterance>? UtteranceReady;

    public event Action? SpeechStarted;

    public bool InSpeech => _inSpeech;

    /// <summary>
    /// While typing, a longer pause ends a phrase: the same quiet fraction must hold over this many ms (when longer
    /// than <see cref="SegmenterOptions.EndSilenceMs"/>), so thinking pauses mid-sentence don't split it into
    /// separately punctuated pieces. 0 = use EndSilenceMs. Safe to set from another thread.
    /// </summary>
    public int LongPauseMs
    {
        get => Volatile.Read(ref _longPauseMs);
        set => Volatile.Write(ref _longPauseMs, Math.Max(0, value));
    }

    /// <summary>Current background (noise) level estimate in dBFS.</summary>
    public double NoiseFloorDb => _backgroundDb;

    /// <summary>Clears any in-progress utterance. The background estimate is kept: it describes the room, not the utterance.</summary>
    public void Reset()
    {
        _preRoll.Clear();
        _utterance.Clear();
        _utteranceLevels.Clear();
        _quietFlags.Clear();
        _frameFill = 0;
        _inSpeech = false;
        _afterForcedCut = false;
        _quietFrames = 0;
        _loudRun = 0;
        _speechFrames = 0;
        ClearEndWindow();
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
        var startThreshold = Math.Max(_backgroundDb + _o.StartAboveBackgroundDb, _o.MinSpeechDbfs);
        var continueThreshold = Math.Max(_backgroundDb + _o.ContinueAboveBackgroundDb, _o.MinSpeechDbfs - 4);

        if (!_inSpeech)
        {
            _preRoll.Enqueue(frame);
            while (_preRoll.Count > _o.PreRollMs / _o.FrameMs)
            {
                _preRoll.Dequeue();
            }

            // Learn the room for the first ~300 ms before allowing speech, so a loud room isn't one long utterance.
            if (_backgroundCount < WarmUpFrames)
            {
                AddBackground(db);
                return;
            }

            if (db >= startThreshold)
            {
                _loudRun++;
                if (_loudRun >= _o.StartFrames)
                {
                    StartUtterance();
                }

                return;
            }

            _loudRun = 0;
            AddBackground(db);
            if (_afterForcedCut && ++_quietFrames * _o.FrameMs >= _o.EndSilenceMs)
            {
                // A real pause after a length-capped cut means the next phrase is a fresh utterance again.
                _afterForcedCut = false;
            }

            return;
        }

        _utterance.AddRange(frame);
        _utteranceLevels.Add(db);
        var quiet = db < continueThreshold;
        if (!quiet)
        {
            _speechFrames++;
        }

        PushEndWindow(quiet);
        _quietFlags.Add(quiet);
        var windowFull = _utteranceLevels.Count >= _endWindow.Length;
        var durationMs = _utterance.Count * 1000 / _o.SampleRate;
        if (windowFull && _endWindowQuiet >= Math.Ceiling(_endWindow.Length * _o.EndQuietFraction) && LongPauseHeld())
        {
            Emit(forcedCut: false);
        }
        else if (durationMs >= _o.MaxUtteranceMs)
        {
            // No pause for the whole cap: either a long sentence or steady background (fan, hum) that is louder
            // than the estimate. Steady sound has little dynamic range; speech has syllables and gaps. Only for
            // steady sound, adopt its level as background so it stops looking like endless speech.
            var sorted = _utteranceLevels.Order().ToList();
            var p10 = sorted[sorted.Count / 10];
            var p90 = sorted[sorted.Count * 9 / 10];
            if (p90 - p10 < SteadySoundRangeDb)
            {
                var level = sorted[sorted.Count / 2];
                for (var i = 0; i <= _background.Length / 2; i++)
                {
                    AddBackground(level);
                }
            }

            Emit(forcedCut: true);
        }
    }

    private void StartUtterance()
    {
        _inSpeech = true;
        _speechFrames = _loudRun;
        ClearEndWindow();
        foreach (var f in _preRoll)
        {
            _utterance.AddRange(f);
        }

        _preRoll.Clear();
        SpeechStarted?.Invoke();
    }

    private void Emit(bool forcedCut)
    {
        var enoughSpeech = _speechFrames * _o.FrameMs >= _o.MinSpeechMs;
        var utterance = new Utterance(_utterance.ToArray(), _afterForcedCut);
        // Quiet frames already at the end of a capped utterance count toward the pause that clears the flag.
        _afterForcedCut = forcedCut && _endWindowQuiet * _o.FrameMs < _o.EndSilenceMs;
        _quietFrames = _endWindowQuiet;
        _utterance.Clear();
        _utteranceLevels.Clear();
        _quietFlags.Clear();
        _inSpeech = false;
        _loudRun = 0;
        _speechFrames = 0;
        ClearEndWindow();
        if (enoughSpeech)
        {
            UtteranceReady?.Invoke(utterance);
        }
    }

    /// <summary>With <see cref="LongPauseMs"/> set, the same quiet fraction must hold over that longer stretch.</summary>
    private bool LongPauseHeld()
    {
        var frames = LongPauseMs / _o.FrameMs;
        if (frames <= _endWindow.Length)
        {
            return true;
        }

        if (_quietFlags.Count < frames)
        {
            return false;
        }

        var quiet = 0;
        for (var i = _quietFlags.Count - frames; i < _quietFlags.Count; i++)
        {
            if (_quietFlags[i])
            {
                quiet++;
            }
        }

        return quiet >= Math.Ceiling(frames * _o.EndQuietFraction);
    }

    private void AddBackground(double db)
    {
        _background[_backgroundNext] = db;
        _backgroundNext = (_backgroundNext + 1) % _background.Length;
        _backgroundCount = Math.Min(_backgroundCount + 1, _background.Length);
        Array.Copy(_background, _sortScratch, _backgroundCount);
        Array.Sort(_sortScratch, 0, _backgroundCount);
        _backgroundDb = _sortScratch[_backgroundCount / 2];
    }

    private void PushEndWindow(bool quiet)
    {
        if (_endWindow[_endWindowNext])
        {
            _endWindowQuiet--;
        }

        _endWindow[_endWindowNext] = quiet;
        if (quiet)
        {
            _endWindowQuiet++;
        }

        _endWindowNext = (_endWindowNext + 1) % _endWindow.Length;
    }

    private void ClearEndWindow()
    {
        Array.Clear(_endWindow);
        _endWindowNext = 0;
        _endWindowQuiet = 0;
    }
}
