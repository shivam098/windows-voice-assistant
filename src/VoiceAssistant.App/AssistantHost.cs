using System.Threading.Channels;
using System.Windows.Threading;
using VoiceAssistant.App.Platform;
using VoiceAssistant.Core;
using VoiceAssistant.Speech;

namespace VoiceAssistant.App;

/// <summary>
/// Wires microphone -> utterance segmenter -> Silero VAD -> Whisper -> interaction controller, plus
/// tap-to-dictate (Ctrl+Win to start, Ctrl+Win again to finish). All controller work runs on one serial queue so
/// utterances are handled strictly in order.
/// </summary>
internal sealed class AssistantHost : IDisposable
{
    private static readonly HashSet<string> KnownHallucinations =
        ["you", "thanks for watching", "thank you for watching", "please subscribe"];

    private const int MaxQueuedUtterances = 6;
    private const int SampleRate = WhisperRecognizer.SampleRate;
    private const int PreviewIntervalMs = 600;
    private const int LevelIntervalMs = 50;

    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _segmenterGate = new();
    private readonly object _captureGate = new();
    private readonly UtteranceSegmenter _segmenter;
    private readonly MicrophoneCapture _microphone;
    private readonly AppCatalog _catalog;
    private readonly InteractionController _controller;
    private readonly DispatcherTimer _tick;
    private readonly List<float> _capture = [];
    private readonly PhraseMemory? _memory;
    private readonly SoundFeedback _sounds;
    private readonly DictationFormatter _formatter;
    private readonly Brain.LlamaBrain? _brain;
    private CancellationTokenSource? _brainDownload;
    private (long Received, long Total) _brainProgress;
    private long _brainProgressPosted;
    private string? _brainError;
    private WhisperRecognizer? _recognizer;

    // Standby hears everything near the microphone (meetings, videos, people talking). Transcribing all of it in full
    // kept one core ~15-30% busy all day. The wake-word check transcribes only the start of each phrase, with a small
    // audio window, a few tokens, two threads and no retries: ~10x less CPU, and the full pass runs only when it may
    // hold the wake word or a command (docs/BENCHMARKS.md, "Standby cost").
    private const int WakeCheckThreads = 2;
    private const int WakeCheckAudioContext = 256; // 5.1 s window
    private const int WakeCheckMaxTokens = 8;
    private const int WakeCheckSeconds = 3;
    private WhisperRecognizer? _wakeCheck;
    private volatile WhisperRecognizer? _hinglish;
    private volatile WhisperRecognizer? _hinglishAccurate;
    private readonly object _hinglishGate = new();
    private Task? _hinglishLoad;
    private Task? _hinglishAccurateLoad;
    private CancellationTokenSource? _accurateLoadCts;
    private volatile bool _hinglishDictation;
    private volatile bool _hinglishAccurateMode;
    private WhisperRecognizer? _captureRecognizer;
    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private SpeechDetector? _detector;
    private Task? _queueTask;
    private CancellationTokenSource _actionCts = new();
    private int _generation;
    private int _queuedUtterances;

    // The AI brain's BrainChanges when the current speech began (written on the audio thread).
    private int _brainChangesAtSpeech;
    private bool _captureStartedMicrophone;
    private volatile bool _capturing;

    // From the start of a Ctrl+Win recording until its text is typed (or it is cancelled): the pill belongs to the
    // dictation, so voice listening must not replace its Listening / Transcribing states.
    private volatile bool _dictationBusy;
    private volatile int _captureId;
    private long _lastLevelTicks;
    private bool _limitReached;
    private volatile bool _pausePending = true;
    private volatile InteractionMode _mode = InteractionMode.Paused;

    public AssistantHost(AppSettings settings, Dispatcher ui, IDesktop? desktop = null, bool persistMemory = true, VoiceAssistant.Board.IBoard? board = null)
    {
        _settings = settings;
        _ui = ui;
        _hinglishDictation = persistMemory && File.Exists(AppPaths.HinglishDictationFlag);
        _hinglishAccurateMode = persistMemory && File.Exists(AppPaths.HinglishAccurateFlag);
        WindowsDesktop.InsertMethod = settings.InsertMethod;
        _sounds = new SoundFeedback(settings.SoundFeedback && persistMemory);
        if (settings.LearnFromUsage)
        {
            _memory = new PhraseMemory();
            if (persistMemory)
            {
                LearnedStore.Load(_memory);
            }
        }

        _catalog = new AppCatalog(settings.Apps, _memory);
        _formatter = new DictationFormatter(new DictationOptions
        {
            RemoveFillerWords = settings.RemoveFillerWords,
            SpokenPunctuation = settings.SpokenPunctuation,
            Replacements = settings.Replacements,
            Snippets = settings.Snippets,
        });
        _controller = new InteractionController(
            desktop ?? new WindowsDesktop(),
            _catalog,
            new CommandParser(settings.CommandPrefix, settings.RequireCommandPrefixWhileTyping, settings.WakeWords, _memory),
            new ControllerOptions
            {
                ReturnToCommandModeAfterSend = settings.ReturnToCommandModeAfterSend,
                MinCommandConfidence = settings.MinCommandConfidence,
                WakeWordEnabled = settings.WakeWordEnabled,
                AwakeTimeout = TimeSpan.FromSeconds(Math.Max(2, settings.AwakeSeconds)),
            },
            _formatter,
            _memory);
        _controller.Status += update =>
        {
            PlayTransitionCue(_mode, update.Mode);
            _mode = update.Mode;

            // Typing tolerates longer thinking pauses before a phrase ends; commands stay snappy.
            if (_segmenter is { } segmenter)
            {
                segmenter.LongPauseMs = update.Mode == InteractionMode.Dictation ? _settings.DictationEndSilenceMs : 0;
            }
            Post(() => Status?.Invoke(update));
        };
        _controller.PauseRequested += () =>
        {
            _pausePending = true;
            StopMicrophone();
        };
        _controller.Trace += message => Log.Info(message);
        _controller.ChoicesChanged += badges => Post(() => ChoicesChanged?.Invoke(badges));
        _controller.Highlight += rect => Post(() => Highlight?.Invoke(rect));
        _controller.Board = board;
        _controller.ShowBoardRequested += list => Post(() => ShowBoardRequested?.Invoke(list));
        if (settings.BrainEnabled)
        {
            _brain = new Brain.LlamaBrain(
                new Brain.BrainFiles(settings.BrainFolder, settings.BrainServerPath, settings.BrainModelPath),
                TimeSpan.FromMinutes(Math.Max(0, settings.BrainKeepLoadedMinutes)));
            _brain.AvailabilityChanged += () => Post(() => BrainChanged?.Invoke());
            _controller.Brain = _brain;
        }

        if (persistMemory && _memory is not null)
        {
            _controller.MemoryChanged += () => LearnedStore.Save(_memory);
        }

        _segmenter = new UtteranceSegmenter(new SegmenterOptions { EndSilenceMs = settings.EndSilenceMs });
        _segmenter.SpeechStarted += () =>
        {
            // What the AI brain was doing as this speech began: a "yes" said before it asked must never answer it.
            Volatile.Write(ref _brainChangesAtSpeech, _controller.BrainChanges);
            if (_mode != InteractionMode.Standby)
            {
                PostVoiceActivity("Hearing you…");
            }
        };
        _segmenter.UtteranceReady += OnUtterance;

        _microphone = new MicrophoneCapture(settings.MicrophoneDevice);
        _microphone.Samples += OnSamples;
        _microphone.Failed += ex =>
        {
            Log.Error($"microphone failed: {ex.GetType().Name}");
            CancelPendingWork();
            Enqueue(async () =>
            {
                var wasCapturing = _capturing;
                await StopPreviewAsync().ConfigureAwait(false);
                StopMicrophone();
                _capturing = false;
                _dictationBusy = false;
                if (wasCapturing)
                {
                    ReportCaptureStopped(_captureId);
                }

                PostActivity(null);
                _controller.SetListening(false);
                Report(StatusKind.Error, $"The microphone stopped. Press {_settings.Hotkey} to try again.");
            });
        };

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Enqueue(() =>
        {
            _controller.Tick();
            return Task.CompletedTask;
        }), ui);
    }

    /// <summary>Raised on the UI thread.</summary>
    public event Action<StatusUpdate>? Status;

    /// <summary>
    /// Raised on the UI thread with the id passed to <see cref="BeginDictationCapture"/> when that recording couldn't
    /// start or stopped on its own (model still loading, microphone error, time limit), so the keys stop acting as if
    /// recording.
    /// </summary>
    public event Action<int>? CaptureStopped;

    /// <summary>
    /// Raised on the UI thread with a transient activity label (null clears it); Ctrl+Win dictation uses
    /// <see cref="DictationLabels.Listening"/> then <see cref="DictationLabels.Transcribing"/>.
    /// </summary>
    public event Action<InteractionMode, string?>? Activity;

    /// <summary>Raised on the UI thread about 20 times a second while Ctrl+Win dictation records, with the microphone level (0 to 1).</summary>
    public event Action<float>? InputLevel;

    /// <summary>Raised on the UI thread with the live transcript while dictation records (null clears it).</summary>
    public event Action<string?>? DictationPreview;

    /// <summary>
    /// Raised on the UI thread when the dictation language changes: Hindi + English on/off, and whether it uses the
    /// accurate (slower) model.
    /// </summary>
    public event Action<bool, bool>? DictationLanguageChanged;

    /// <summary>Raised on the UI thread with numbers to draw on screen (null hides them).</summary>
    public event Action<IReadOnlyList<ChoiceBadge>?>? ChoicesChanged;

    /// <summary>Raised on the UI thread with something to outline briefly (just clicked, selected, or focused).</summary>
    public event Action<ScreenRect>? Highlight;

    /// <summary>“Show my ideas” and similar: open the window on the idea board (raised on the UI thread).</summary>
    public event Action<VoiceAssistant.Board.BoardList?>? ShowBoardRequested;

    /// <summary>The AI brain loaded, unloaded, failed, or its download moved on (raised on the UI thread).</summary>
    public event Action? BrainChanged;

    /// <summary>The AI brain, or null when it is switched off in Settings.</summary>
    public Brain.LlamaBrain? AiBrain => _brain;

    /// <summary>Bytes so far and in all while the AI brain downloads; null otherwise.</summary>
    public (long Received, long Total)? BrainDownloadProgress => Volatile.Read(ref _brainDownload) is null ? null : _brainProgress;

    /// <summary>Why the last download of the AI brain failed (null after a success or a new start).</summary>
    public string? BrainError => _brainError;

    /// <summary>Downloads the AI brain's files in the background (resuming a partial download), then loads it.</summary>
    public void StartBrainDownload()
    {
        if (_brain is not { } brain)
        {
            return;
        }

        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (Interlocked.CompareExchange(ref _brainDownload, cancel, null) is not null)
        {
            cancel.Dispose();
            return;
        }

        _brainError = null;
        _brainProgress = (0, brain.Files.MissingBytes);
        Post(() => BrainChanged?.Invoke());
        _ = Task.Run(async () =>
        {
            Log.Info($"brain: downloading {brain.Files.MissingBytes / (1 << 20)} MB");
            try
            {
                await brain.Files.DownloadAsync((received, total) =>
                {
                    _brainProgress = (received, total);
                    if (received - Interlocked.Read(ref _brainProgressPosted) >= 8 << 20 || received == total)
                    {
                        Interlocked.Exchange(ref _brainProgressPosted, received);
                        Post(() => BrainChanged?.Invoke());
                    }
                }, cancel.Token).ConfigureAwait(false);
                Log.Info("brain: downloaded and verified");
                brain.Refresh();
                brain.WarmUp();
                Report(StatusKind.Success, "The AI brain is ready. Ask for anything, like “open the MrBeast video on YouTube”.");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                Log.Info("brain: download paused");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Error($"brain: download failed: {ex.GetType().Name}");
                _brainError = ex is InvalidDataException ? "The download was damaged and was deleted. Try again." : "The download stopped. Check the internet connection and try again (it carries on where it stopped).";
            }
            finally
            {
                Interlocked.Exchange(ref _brainDownload, null);
                Interlocked.Exchange(ref _brainProgressPosted, 0);
                cancel.Dispose();
                Post(() => BrainChanged?.Invoke());
            }
        });
    }

    /// <summary>Stops a download of the AI brain; the next one carries on where it stopped.</summary>
    public void CancelBrainDownload()
    {
        try
        {
            Volatile.Read(ref _brainDownload)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Esc while the AI brain plans or acts: stops it. Listening carries on.</summary>
    public void CancelBrainAction()
    {
        if (_controller.IsBrainRunning)
        {
            Log.Info("brain: stopped with Esc");
            Interlocked.Exchange(ref _actionCts, new CancellationTokenSource()).Cancel();
        }
    }

    /// <summary>True when dictation uses the Hindi + English model.</summary>
    public bool HinglishDictation => _hinglishDictation;

    /// <summary>True when Hindi + English dictation uses the accurate (larger, slower) model.</summary>
    public bool HinglishAccurate => _hinglishAccurateMode;

    public InteractionMode Mode => _mode;

    /// <summary>The app's controller, for scripted self-tests (call only from <see cref="RunOnQueueAsync"/>).</summary>
    internal InteractionController Controller => _controller;

    public void Start()
    {
        _queueTask = Task.Run(ProcessQueueAsync);
        Enqueue(LoadAsync);
        _tick.Start();
        DiscoverApps();
    }

    /// <summary>Starts only the queue (no microphone or models) for scripted self-tests.</summary>
    internal void StartForSelfTest()
    {
        _queueTask = Task.Run(ProcessQueueAsync);
        DiscoverApps().Wait(TimeSpan.FromSeconds(25));
    }

    internal Task RunOnQueueAsync(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>Hotkey/tray toggle. Pausing takes effect immediately, before any queued speech is handled.</summary>
    public void ToggleListening()
    {
        if (_mode != InteractionMode.Paused && !_pausePending)
        {
            CancelPendingWork();
            Enqueue(() =>
            {
                StopMicrophone();
                _controller.SetListening(false);
                return Task.CompletedTask;
            });
            return;
        }

        Enqueue(() =>
        {
            if (_controller.Mode == InteractionMode.Paused)
            {
                StartListening();
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Starts recording for Ctrl+Win dictation (works even while paused). <paramref name="id"/> identifies this
    /// recording in <see cref="CaptureStopped"/>.
    /// </summary>
    public void BeginDictationCapture(int id) => Enqueue(() =>
    {
        if (_capturing)
        {
            return Task.CompletedTask;
        }

        _captureId = id;
        if (_recognizer is null)
        {
            Report(StatusKind.Warning, "Still loading the speech model…");
            ReportCaptureStopped(id);
            return Task.CompletedTask;
        }

        // A recording that ended early (cancelled, silence) may have kept a model that's no longer chosen.
        ReleaseAccurateIfUnused();
        lock (_captureGate)
        {
            _capture.Clear();
        }

        if (!_microphone.IsRunning)
        {
            try
            {
                _microphone.Start();
                _captureStartedMicrophone = true;
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
            {
                Log.Error($"microphone start failed: {ex.GetType().Name}");
                Report(StatusKind.Error, "Couldn't open the microphone. Check Settings › Privacy › Microphone.");
                ReportCaptureStopped(id);
                return Task.CompletedTask;
            }
        }

        _limitReached = false;
        _lastLevelTicks = 0;
        _dictationBusy = true;

        // Phrases the voice listener finished before the key press (still queued) are dropped, not run mid-recording.
        Interlocked.Increment(ref _generation);
        _capturing = true;
        _captureRecognizer = _recognizer;
        WhisperRecognizer? previewRecognizer = _recognizer;
        if (_hinglishDictation)
        {
            var final = HinglishForTyping();
            if (_hinglishAccurateMode && _hinglishAccurate is null)
            {
                // Retries after a failed download or load, too.
                EnsureHinglishLoading();
            }

            if (final is not null)
            {
                _captureRecognizer = final;

                // The accurate model takes seconds per pass: the live preview uses the fast one (or is skipped).
                previewRecognizer = final == _hinglishAccurate ? _hinglish : final;
                if (_hinglishAccurateMode && final != _hinglishAccurate)
                {
                    Report(StatusKind.Warning, "The accurate Hindi + English model is still loading; this dictation uses the fast one.");
                }
            }
            else
            {
                EnsureHinglishLoading();
                Report(StatusKind.Warning, "The Hindi + English model isn't ready yet; this dictation uses English.");
            }
        }

        _sounds.Play(SoundFeedback.Cue.StartTyping);
        PostActivity(DictationLabels.Listening);
        if (_settings.LivePreview && previewRecognizer is not null)
        {
            _previewCts = new CancellationTokenSource();
            var token = _previewCts.Token;
            var recognizer = previewRecognizer;
            _previewTask = Task.Run(() => RunPreviewAsync(recognizer, token));
        }

        return Task.CompletedTask;
    });

    /// <summary>Stops Ctrl+Win dictation; transcribes and types unless cancelled.</summary>
    public void EndDictationCapture(bool cancel) => Enqueue(() => EndCaptureAsync(cancel));

    /// <param name="cancel">Throw the recording away instead of typing it.</param>
    /// <param name="onlyId">Stop only if this recording is still the one in progress (time limit).</param>
    private async Task EndCaptureAsync(bool cancel, int? onlyId = null)
    {
        if (!_capturing || (onlyId is { } id && id != _captureId))
        {
            return;
        }

        _capturing = false;

        // Show “Transcribing…” straight away: stopping the live preview may wait for a pass that is still running.
        PostActivity(cancel ? null : DictationLabels.Transcribing);
        try
        {
            await StopPreviewAsync().ConfigureAwait(false);
            float[] audio;
            lock (_captureGate)
            {
                audio = [.. _capture];
                _capture.Clear();
            }

            if (_captureStartedMicrophone || _pausePending || _controller.Mode == InteractionMode.Paused)
            {
                // Close the mic if dictation opened it, or if listening was paused while recording.
                _captureStartedMicrophone = false;
                StopMicrophone();
            }
            else
            {
                // Voice listening continues; drop any half-heard phrase from before the key was pressed, and any
                // phrase the segmenter finished while the live preview was being stopped.
                Interlocked.Increment(ref _generation);
                lock (_segmenterGate)
                {
                    _segmenter.Reset();
                }
            }

            _sounds.Play(cancel ? SoundFeedback.Cue.Standby : SoundFeedback.Cue.StopTyping);
            if (cancel)
            {
                Report(StatusKind.Info, "Dictation cancelled.");
                return;
            }

            var recognizer = _captureRecognizer ?? _recognizer;
            if (audio.Length < SampleRate / 4 || recognizer is null)
            {
                return;
            }

            IReadOnlyList<float[]> chunks = _detector is null
                ? SpeechSegments.Chunk(audio, [(TimeSpan.Zero, TimeSpan.FromSeconds((double)audio.Length / SampleRate))], SampleRate, 14000, 0)
                : SpeechSegments.Chunk(audio, _detector.Detect(audio), SampleRate, 14000, 200);
            if (chunks.Count == 0)
            {
                Report(StatusKind.Info, "I didn't hear any speech.");
                return;
            }

            var parts = new List<string>();
            foreach (var chunk in chunks)
            {
                var result = await recognizer.TranscribeAsync(chunk, _shutdown.Token).ConfigureAwait(false);
                Log.Info($"dictation chunk audio={chunk.Length * 1000 / SampleRate}ms asr={result.Elapsed.TotalMilliseconds:F0}ms{(recognizer == _hinglishAccurate ? " model=hinglish-accurate" : recognizer == _hinglish ? " model=hinglish" : string.Empty)}");
                if (recognizer == _hinglishAccurate && IsPromptEcho(result.Text))
                {
                    Log.Info("dropped a repeat of the vocabulary hint");
                    continue;
                }

                parts.Add(TranscriptText.Clean(result.Text));
            }

            _controller.InsertDictation(string.Join(" ", parts.Where(p => p.Length > 0)));
        }
        finally
        {
            _dictationBusy = false;
            PostActivity(null);

            // The language may have changed during this recording.
            ReleaseAccurateIfUnused();
        }
    }

    /// <summary>The recording reached <see cref="AppSettings.MaxDictationSeconds"/>: finish it as if Ctrl+Win was tapped.</summary>
    private async Task FinishAtLimitAsync(int id)
    {
        if (!_capturing || id != _captureId)
        {
            return;
        }

        var seconds = _settings.MaxDictationSeconds;
        Log.Info($"dictation reached the {seconds}s limit");
        ReportCaptureStopped(id);
        await EndCaptureAsync(cancel: false, onlyId: id).ConfigureAwait(false);
        var limit = seconds % 60 == 0 ? $"{seconds / 60} minute{(seconds == 60 ? string.Empty : "s")}" : $"{seconds} seconds";
        Report(StatusKind.Info, $"Dictation stops by itself after {limit}. Tap Ctrl+Win to keep going.");
    }

    public void PasteLastTranscript() => Enqueue(() =>
    {
        _controller.RepeatLastTranscript();
        return Task.CompletedTask;
    });

    /// <summary>
    /// Switches dictation between English and Hindi + English (Alt+Shift+H), keeping the fast/accurate choice.
    /// Takes effect from the next phrase.
    /// </summary>
    public void ToggleDictationLanguage() => Enqueue(() =>
    {
        ApplyDictationLanguage(!_hinglishDictation, _hinglishAccurateMode);
        return Task.CompletedTask;
    });

    /// <summary>Sets the dictation language (tray menu); <paramref name="accurate"/> picks the larger Hindi + English model.</summary>
    public void SetDictationLanguage(bool hinglish, bool accurate = false) => Enqueue(() =>
    {
        if (hinglish != _hinglishDictation || (hinglish && accurate != _hinglishAccurateMode))
        {
            ApplyDictationLanguage(hinglish, hinglish ? accurate : _hinglishAccurateMode);
        }
        else if (hinglish && _recognizer is not null)
        {
            // Choosing the current mode again retries a model that failed to download or load.
            EnsureHinglishLoading();
        }

        return Task.CompletedTask;
    });

    private void ApplyDictationLanguage(bool on, bool accurate)
    {
        _hinglishDictation = on;
        _hinglishAccurateMode = accurate;
        StateFlags.Set(AppPaths.HinglishDictationFlag, on);
        StateFlags.Set(AppPaths.HinglishAccurateFlag, accurate);
        Log.Info($"dictation language: {(on ? accurate ? "hinglish-accurate" : "hinglish" : "english")}");
        Post(() => DictationLanguageChanged?.Invoke(on, accurate));
        if (!accurate)
        {
            // Not wanted any more: stop its download/load and free its memory (about 1 GB).
            _accurateLoadCts?.Cancel();
            ReleaseAccurateIfUnused();
        }

        if (!on)
        {
            Report(StatusKind.Info, "Dictation language: English.");
        }
        else if (HinglishForTyping() is { } ready && (!accurate || ready == _hinglishAccurate))
        {
            Report(StatusKind.Info, accurate
                ? "Dictation language: Hindi + English, accurate (slower; Hindi is typed in Roman letters)."
                : "Dictation language: Hindi + English (Hindi is typed in Roman letters).");
        }
        else if (_recognizer is not null)
        {
            EnsureHinglishLoading();
        }
    }

    /// <summary>The model that types Hindi + English text: the accurate one when chosen and loaded, else the fast one.</summary>
    private WhisperRecognizer? HinglishForTyping() =>
        _hinglishAccurateMode && _hinglishAccurate is { } accurate ? accurate : _hinglish;

    /// <summary>The user's vocabulary as a Whisper prompt (“GitHub Copilot, vibe coding, …”); null when empty.</summary>
    private string? VocabularyPrompt
    {
        get
        {
            var words = (_settings.DictationVocabulary ?? []).Select(w => w.Trim()).Where(w => w.Length > 0).Take(40).ToList();
            return words.Count == 0 ? null : string.Join(", ", words);
        }
    }

    /// <summary>On near-silence Whisper can repeat its prompt; such text is dropped instead of typed.</summary>
    private bool IsPromptEcho(string text)
    {
        var prompt = VocabularyPrompt;
        var heard = TranscriptText.Normalize(text);
        return prompt is not null && heard.Length > 0 && TranscriptText.Normalize(prompt).Contains(heard, StringComparison.Ordinal);
    }

    /// <summary>
    /// Frees the accurate model when that mode is no longer chosen. Runs on the work queue, where every use of it
    /// happens; a recording that started with it keeps it until it has been transcribed.
    /// </summary>
    private void ReleaseAccurateIfUnused()
    {
        if (!_hinglishAccurateMode && !_capturing && _hinglishAccurate is { } model)
        {
            _hinglishAccurate = null;
            model.Dispose();
            Log.Info("hinglish-accurate model unloaded");
        }
    }

    /// <summary>Forgets learned rephrasings and app usage, in memory and on disk.</summary>
    public void ForgetLearned() => Enqueue(() =>
    {
        _memory?.Clear();
        LearnedStore.Delete();
        Report(StatusKind.Info, "Forgot everything I'd learned.");
        return Task.CompletedTask;
    });

    private void PlayTransitionCue(InteractionMode from, InteractionMode to)
    {
        if (from == to)
        {
            return;
        }

        if (to == InteractionMode.Dictation)
        {
            _sounds.Play(SoundFeedback.Cue.StartTyping);
        }
        else if (from == InteractionMode.Dictation && to != InteractionMode.Paused)
        {
            _sounds.Play(SoundFeedback.Cue.StopTyping);
        }
        else if (from == InteractionMode.Standby && to == InteractionMode.Command)
        {
            _sounds.Play(SoundFeedback.Cue.Wake);
        }
        else if (from == InteractionMode.Command && to == InteractionMode.Standby)
        {
            _sounds.Play(SoundFeedback.Cue.Standby);
        }
    }

    /// <summary>Copies dictated text that couldn't be typed (focus moved) to the clipboard, then forgets it.</summary>
    public void CopyUntypedText() => Enqueue(() =>
    {
        var text = _controller.UntypedText;
        _controller.ClearUntypedText();
        if (!string.IsNullOrEmpty(text))
        {
            Post(() => System.Windows.Clipboard.SetText(text));
            Report(StatusKind.Success, "Copied the untyped text to the clipboard.");
        }
        else
        {
            Report(StatusKind.Info, "There's no untyped text.");
        }

        return Task.CompletedTask;
    });

    public void Dispose()
    {
        _tick.Stop();
        CancelPendingWork();
        _previewCts?.Cancel();
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        var drained = true;
        try
        {
            drained = _queueTask?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        }
        catch (AggregateException ex)
        {
            Log.Warn($"queue ended with {ex.InnerException?.GetType().Name}");
        }

        drained &= _previewTask?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        Task? hinglishLoad;
        Task? hinglishAccurateLoad;
        lock (_hinglishGate)
        {
            hinglishLoad = _hinglishLoad;
            hinglishAccurateLoad = _hinglishAccurateLoad;
        }

        drained &= hinglishLoad?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        drained &= hinglishAccurateLoad?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        _brain?.Dispose();
        StopMicrophone();
        _microphone.Dispose();
        _sounds.Dispose();
        if (drained)
        {
            _wakeCheck?.Dispose(); // shares _recognizer's model: dispose first
            _recognizer?.Dispose();
            _hinglish?.Dispose();
            _hinglishAccurate?.Dispose();
            _detector?.Dispose();
        }
        else
        {
            // Freeing the model while Whisper is mid-transcription crashes; the OS reclaims it at exit instead.
            Log.Warn("queue did not drain; leaving speech model to process exit");
        }
    }

    private Task DiscoverApps()
    {
        if (!_settings.DiscoverStartMenuApps)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            var apps = StartMenuApps.Discover();
            _catalog.SetDiscoveredApps(apps);
            Log.Info($"start menu apps discovered: {apps.Count}");
        });
    }

    private void OnSamples(float[] samples)
    {
        if (_capturing)
        {
            int recorded;
            lock (_captureGate)
            {
                _capture.AddRange(samples);
                recorded = _capture.Count;
            }

            var now = Environment.TickCount64;
            if (now - _lastLevelTicks >= LevelIntervalMs)
            {
                _lastLevelTicks = now;
                var level = AudioLevel.Of(samples);
                Post(() => InputLevel?.Invoke(level));
            }

            var max = _settings.MaxDictationSeconds;
            if (max > 0 && !_limitReached && recorded >= (long)max * SampleRate)
            {
                _limitReached = true;
                var id = _captureId;
                Enqueue(() => FinishAtLimitAsync(id));
            }

            return;
        }

        lock (_segmenterGate)
        {
            _segmenter.Push(samples);
        }
    }

    /// <summary>Drops queued speech and cancels in-flight actions (open/focus) immediately, from any thread.</summary>
    private void CancelPendingWork()
    {
        _pausePending = true;
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _actionCts, new CancellationTokenSource()).Cancel();
    }

    /// <summary>
    /// Makes sure a model file exists, downloading and verifying it on first run (portable zips ship without
    /// models). Returns false if it is still missing.
    /// </summary>
    private async Task<bool> EnsureModelAsync(string file, bool firstRun = true, string? what = null, CancellationToken cancellationToken = default)
    {
        if (File.Exists(Path.Combine(AppPaths.ModelsDir, file)))
        {
            return true;
        }

        if (!ModelDownloader.CanDownload(file))
        {
            Report(StatusKind.Error, $"Speech model {file} not found in {AppPaths.ModelsDir}.");
            return false;
        }

        Log.Info($"downloading {file}");
        what ??= "Hindi + English model (about 80 MB";
        Report(StatusKind.Info, firstRun
            ? "First run: downloading the speech model (one time, stays on this PC)…"
            : $"Downloading the {what}, one time)…");
        try
        {
            // Progress shows in the pill only on first run; later downloads must not replace a "Listening…" label,
            // so they report every 10% as a status message instead.
            var lastTenth = 0;
            await ModelDownloader.DownloadAsync(file, AppPaths.ModelsDir, percent =>
            {
                if (firstRun)
                {
                    PostActivity($"Downloading speech model… {percent}%");
                }
                else if (percent / 10 > lastTenth && percent < 100)
                {
                    lastTenth = percent / 10;
                    Report(StatusKind.Info, $"Downloading the {what.Split(" (")[0]}… {percent}%");
                }
            }, cancellationToken.CanBeCanceled ? cancellationToken : _shutdown.Token).ConfigureAwait(false);
            Log.Info($"downloaded {file}");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            Log.Info($"download of {file} cancelled");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            Log.Error($"download of {file} failed: {ex.GetType().Name}");
            Report(StatusKind.Error, firstRun
                ? "Couldn't download the speech model. Check the internet connection and restart Voice Assistant."
                : $"Couldn't download the {what.Split(" (")[0]}. Check the internet connection and switch it on again.");
            return false;
        }
        finally
        {
            if (firstRun)
            {
                PostActivity(null);
            }
        }
    }

    private async Task LoadAsync()
    {
        var modelPath = Path.Combine(AppPaths.ModelsDir, _settings.ModelFile);
        foreach (var file in new[] { _settings.ModelFile, _settings.VadModelFile })
        {
            if (!await EnsureModelAsync(file).ConfigureAwait(false) && file == _settings.ModelFile)
            {
                return;
            }
        }

        PostActivity("Loading speech model…");
        var started = DateTime.UtcNow;
        try
        {
            _recognizer = await Task.Run(() => new WhisperRecognizer(modelPath, new RecognizerOptions
            {
                Threads = _settings.Threads,
                AudioContextSize = _settings.AudioContextSize,
                Prompt = _settings.VocabularyPrompt,
            })).ConfigureAwait(false);
            await _recognizer.TranscribeAsync(new float[SampleRate], _shutdown.Token).ConfigureAwait(false);

            // The standby pre-check: the same model on the first seconds of each phrase, cut short (see WakeCheck*).
            _wakeCheck = _recognizer.WithOptions(new RecognizerOptions
            {
                Threads = WakeCheckThreads,
                AudioContextSize = WakeCheckAudioContext,
                MaxTokens = WakeCheckMaxTokens,
                TemperatureFallback = false,
            });
            await _wakeCheck.TranscribeAsync(new float[SampleRate], _shutdown.Token).ConfigureAwait(false);

            var vadPath = Path.Combine(AppPaths.ModelsDir, _settings.VadModelFile);
            if (File.Exists(vadPath))
            {
                _detector = new SpeechDetector(vadPath);
            }
            else
            {
                Log.Warn("vad model missing; using energy detection only");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"model load failed: {ex.GetType().Name}");
            Report(StatusKind.Error, "Couldn't load the speech model. Delete it from the models folder and restart to re-download.");
            return;
        }
        finally
        {
            PostActivity(null);
        }

        Log.Info($"models loaded in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms (vad={_detector is not null})");
        if (_hinglishDictation)
        {
            EnsureHinglishLoading();
        }

        if (_brain is { Availability: VoiceAssistant.Core.BrainAvailability.Sleeping })
        {
            // Loads in the background (about 10 s) so the first request is quick.
            _brain.WarmUp();
        }

        if (_settings.ListenOnStartup)
        {
            StartListening();
        }
        else
        {
            Report(StatusKind.Info, $"Ready. Press {_settings.Hotkey} to start listening.");
        }
    }

    /// <summary>
    /// Downloads (first time) and loads the Hindi + English model(s) in the background, so commands keep working
    /// meanwhile. The accurate mode loads both: the fast model drives the live preview.
    /// </summary>
    private void EnsureHinglishLoading()
    {
        lock (_hinglishGate)
        {
            if (_hinglish is null && _hinglishLoad is null)
            {
                _hinglishLoad = Task.Run(() => LoadHinglishAsync(accurate: false));
            }

            if (_hinglishAccurateMode && _hinglishAccurate is null && _hinglishAccurateLoad is null)
            {
                _accurateLoadCts?.Dispose();
                _accurateLoadCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                var token = _accurateLoadCts.Token;
                _hinglishAccurateLoad = Task.Run(() => LoadHinglishAsync(accurate: true, token));
            }
        }
    }

    private async Task LoadHinglishAsync(bool accurate, CancellationToken cancellationToken = default)
    {
        var file = accurate ? _settings.HinglishAccurateModelFile : _settings.HinglishModelFile;
        var what = accurate ? "accurate Hindi + English model (about 830 MB" : "Hindi + English model (about 80 MB";
        var token = cancellationToken.CanBeCanceled ? cancellationToken : _shutdown.Token;
        try
        {
            if (!await EnsureModelAsync(file, firstRun: false, what, token).ConfigureAwait(false))
            {
                return;
            }

            var started = DateTime.UtcNow;
            var recognizer = new WhisperRecognizer(Path.Combine(AppPaths.ModelsDir, file), new RecognizerOptions
            {
                Threads = _settings.Threads,
                AudioContextSize = _settings.AudioContextSize,

                // Measured: a word list helps the accurate model spell jargon but makes the fast one output garbage.
                Prompt = accurate ? VocabularyPrompt : null,
            });
            try
            {
                // Not cancellable: Whisper keeps running natively after a cancel, so it must finish before Dispose.
                await recognizer.TranscribeAsync(new float[SampleRate], CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                recognizer.Dispose();
                throw;
            }

            if (token.IsCancellationRequested || (accurate && !_hinglishAccurateMode))
            {
                // Switched to another mode (or exiting) while it loaded.
                recognizer.Dispose();
                Log.Info($"{(accurate ? "hinglish-accurate" : "hinglish")} model no longer needed after loading; unloaded");
                return;
            }

            if (accurate)
            {
                _hinglishAccurate = recognizer;
            }
            else
            {
                _hinglish = recognizer;
            }

            Log.Info($"{(accurate ? "hinglish-accurate" : "hinglish")} model loaded in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
            if (_hinglishDictation && accurate == _hinglishAccurateMode)
            {
                Report(StatusKind.Success, accurate
                    ? "Accurate Hindi + English dictation is ready (a few seconds per phrase; Hindi is typed in Roman letters)."
                    : "Hindi + English dictation is ready (Hindi is typed in Roman letters).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"{(accurate ? "hinglish-accurate" : "hinglish")} model load failed: {ex.GetType().Name}");
            Report(StatusKind.Error, $"Couldn't load the {what.Split(" (")[0]}. Delete it from the models folder and switch it on again.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_hinglishGate)
            {
                // Allow another attempt after a failure.
                if (accurate)
                {
                    _hinglishAccurateLoad = null;
                }
                else
                {
                    _hinglishLoad = null;
                }
            }
        }
    }

    private void StartListening()
    {
        if (_recognizer is null)
        {
            Report(StatusKind.Warning, "Still loading the speech model…");
            return;
        }

        lock (_segmenterGate)
        {
            _segmenter.Reset();
        }

        try
        {
            _microphone.Start();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            Log.Error($"microphone start failed: {ex.GetType().Name}");
            Report(StatusKind.Error, "Couldn't open the microphone. Check Settings › Privacy › Microphone.");
            return;
        }

        _captureStartedMicrophone = false;
        Interlocked.Increment(ref _generation);
        _pausePending = false;
        _controller.SetListening(true);
        Log.Info("listening");
    }

    private void StopMicrophone()
    {
        if (_capturing)
        {
            // Ctrl+Win dictation owns the microphone until it ends.
            return;
        }

        _microphone.Stop();
        Interlocked.Increment(ref _generation);
        lock (_segmenterGate)
        {
            _segmenter.Reset();
        }

        Log.Info("microphone stopped");
    }

    private void OnUtterance(Utterance utterance)
    {
        if (_pausePending)
        {
            return;
        }

        if (_controller.IsBrainRunning)
        {
            // Speech while the AI brain plans or acts would otherwise run once it's done: late, on a changed screen, and
            // often just the request said again. It's dropped; Esc stops the brain at once.
            Log.Info("speech while the AI brain was busy: ignored");
            PostVoiceActivity(null);
            Report(StatusKind.Info, _settings.PushToTalk ? "Still working on your last request. Press Esc to stop it." : "Still working on your last request.");
            return;
        }

        var generation = Volatile.Read(ref _generation);
        var brainChanges = Volatile.Read(ref _brainChangesAtSpeech);
        if (Interlocked.Increment(ref _queuedUtterances) > MaxQueuedUtterances)
        {
            Interlocked.Decrement(ref _queuedUtterances);
            Log.Warn("utterance queue full; dropped one");
            return;
        }

        Enqueue(() => TranscribeAndHandleAsync(utterance, generation, brainChanges));
    }

    private bool IsStale(int generation) => _pausePending || generation != Volatile.Read(ref _generation);

    private async Task TranscribeAndHandleAsync(Utterance utterance, int generation, int brainChanges)
    {
        try
        {
            if (IsStale(generation) || _recognizer is null)
            {
                return;
            }

            // Silero drops music/noise and splits phrases that background sound merged (at the same pause length
            // the segmenter uses, so a typing pause doesn't split a sentence here either).
            var pause = _mode == InteractionMode.Dictation ? Math.Max(_settings.EndSilenceMs, _settings.DictationEndSilenceMs) : _settings.EndSilenceMs;
            IReadOnlyList<float[]> pieces = _detector is null
                ? [utterance.Samples]
                : SpeechSegments.Split(utterance.Samples, _detector.Detect(utterance.Samples), SampleRate, pause, 250);
            if (pieces.Count == 0)
            {
                Log.Info($"no speech in {utterance.Samples.Length * 1000 / SampleRate}ms utterance");
                return;
            }

            for (var i = 0; i < pieces.Count; i++)
            {
                await HandlePieceAsync(pieces[i], utterance.FollowsForcedCut && i == 0, generation, brainChanges).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queuedUtterances);
            PostVoiceActivity(null);
        }
    }

    private async Task HandlePieceAsync(float[] samples, bool followsForcedCut, int generation, int brainChanges)
    {
        if (IsStale(generation) || _recognizer is null)
        {
            return;
        }

        var audioMs = samples.Length * 1000 / SampleRate;
        if (_controller.Mode is InteractionMode.Command or InteractionMode.Standby && audioMs > _settings.MaxCommandUtteranceMs)
        {
            // Commands are short; long speech while not typing is conversation or meeting audio.
            Log.Info($"skipped {audioMs}ms utterance while waiting for a command");
            return;
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, Volatile.Read(ref _actionCts).Token);
        if (_controller.Mode == InteractionMode.Standby && _wakeCheck is { } wakeCheck)
        {
            var head = samples.Length > WakeCheckSeconds * SampleRate ? samples[..(WakeCheckSeconds * SampleRate)] : samples;
            var rough = await wakeCheck.TranscribeAsync(head, cancel.Token).ConfigureAwait(false);
            if (IsStale(generation) || !_controller.WorthTranscribingInStandby(rough.Text, cutShort: head.Length < samples.Length))
            {
                Log.Info($"standby: no wake word in {audioMs}ms utterance (check {rough.Elapsed.TotalMilliseconds:F0}ms)");
                return;
            }
        }
        else if (_controller.Mode != InteractionMode.Standby)
        {
            PostVoiceActivity("Transcribing…");
        }

        var result = await _recognizer.TranscribeAsync(samples, cancel.Token).ConfigureAwait(false);
        Log.Info($"utterance audio={audioMs}ms asr={result.Elapsed.TotalMilliseconds:F0}ms p={result.Probability:F2} continuation={followsForcedCut}");

        if (IsStale(generation))
        {
            return;
        }

        // While typing in Hindi + English, commands are still recognized in English; text to type comes from the Hindi model.
        var hinglish = _hinglishDictation && _controller.Mode == InteractionMode.Dictation ? HinglishForTyping() : null;
        if (hinglish is null && audioMs < 2500 && KnownHallucinations.Contains(TranscriptText.Normalize(result.Text)))
        {
            return;
        }

        Func<Task<string>>? dictation = hinglish is null ? null : async () =>
        {
            var typed = await hinglish.TranscribeAsync(samples, cancel.Token).ConfigureAwait(false);
            Log.Info($"utterance hinglish asr={typed.Elapsed.TotalMilliseconds:F0}ms");
            return (audioMs < 2500 && KnownHallucinations.Contains(TranscriptText.Normalize(typed.Text))) || (hinglish == _hinglishAccurate && IsPromptEcho(typed.Text))
                ? string.Empty
                : typed.Text;
        };

        await _controller.HandleTranscriptAsync(result.Text, result.Probability, followsForcedCut, cancel.Token, dictation, brainChanges).ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                Log.Info("action cancelled by pause");
            }
            catch (Exception ex)
            {
                Log.Error($"action failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                Report(StatusKind.Error, "That didn't work: " + ex.Message);
            }
        }
    }

    private void Enqueue(Func<Task> work) => _queue.Writer.TryWrite(work);

    /// <summary>Re-transcribes the recording so far every <see cref="PreviewIntervalMs"/> and shows it live.</summary>
    private async Task RunPreviewAsync(WhisperRecognizer recognizer, CancellationToken token)
    {
        var detector = _detector;
        var preview = new DictationPreview(
            async audio =>
            {
                // Never cancel Whisper mid-run (it keeps running natively); the final transcription waits for it.
                var result = await recognizer.TranscribeAsync(audio, CancellationToken.None).ConfigureAwait(false);
                var text = TranscriptText.Clean(result.Text);
                return audio.Length < SampleRate * 5 / 2 && KnownHallucinations.Contains(TranscriptText.Normalize(text)) ? string.Empty : text;
            },
            detector is null ? null : detector.Detect,
            SampleRate);
        var seen = 0;
        var updates = 0;
        var busy = TimeSpan.Zero;
        string? shown = null;
        try
        {
            while (true)
            {
                await Task.Delay(PreviewIntervalMs, token).ConfigureAwait(false);
                float[] pending;
                lock (_captureGate)
                {
                    if (_capture.Count - seen < SampleRate * 3 / 10)
                    {
                        continue;
                    }

                    seen = _capture.Count;
                    pending = _capture.GetRange(preview.CommittedSamples, seen - preview.CommittedSamples).ToArray();
                }

                if (pending.Length < SampleRate / 2)
                {
                    continue;
                }

                var started = DateTime.UtcNow;
                var text = await preview.UpdateAsync(pending).ConfigureAwait(false);
                busy += DateTime.UtcNow - started;
                updates++;
                var formatted = text.Length == 0 ? string.Empty : _formatter.Format(text).Replace("\r", string.Empty).Replace("\n", " ↵ ");
                if (!token.IsCancellationRequested && formatted != shown)
                {
                    shown = formatted;
                    Post(() => DictationPreview?.Invoke(formatted));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"live preview stopped: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
        finally
        {
            if (updates > 0)
            {
                Log.Info($"live preview updates={updates} avg={busy.TotalMilliseconds / updates:F0}ms");
            }
        }
    }

    /// <summary>Stops the live preview and waits for any in-flight transcription so the recognizer is free.</summary>
    private async Task StopPreviewAsync()
    {
        var task = _previewTask;
        _previewCts?.Cancel();
        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }

        _previewCts = null;
        _previewTask = null;
        Post(() => DictationPreview?.Invoke(null));
    }

    private void ReportCaptureStopped(int id) => Post(() => CaptureStopped?.Invoke(id));

    private void Report(StatusKind kind, string message) =>
        Post(() => Status?.Invoke(new StatusUpdate(_mode, kind, message)));

    private void PostActivity(string? activity) => Post(() => Activity?.Invoke(_mode, activity));

    /// <summary>Activity of voice listening and commands; skipped while a Ctrl+Win dictation owns the pill.</summary>
    private void PostVoiceActivity(string? activity)
    {
        if (!_dictationBusy)
        {
            PostActivity(activity);
        }
    }

    private void Post(Action action)
    {
        if (!_ui.HasShutdownStarted)
        {
            _ui.BeginInvoke(action);
        }
    }
}
