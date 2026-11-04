using System.Threading.Channels;
using System.Windows.Threading;
using VoiceAssistant.App.Platform;
using VoiceAssistant.Core;
using VoiceAssistant.Speech;

namespace VoiceAssistant.App;

/// <summary>
/// Wires microphone -> utterance segmenter -> Silero VAD -> Whisper -> interaction controller, plus
/// push-to-talk / hands-free dictation. All controller work runs on one serial queue so utterances are
/// handled strictly in order.
/// </summary>
internal sealed class AssistantHost : IDisposable
{
    private static readonly HashSet<string> KnownHallucinations =
        ["you", "thanks for watching", "thank you for watching", "please subscribe"];

    private const int MaxQueuedUtterances = 6;
    private const int SampleRate = WhisperRecognizer.SampleRate;
    private const int PreviewIntervalMs = 600;

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
    private WhisperRecognizer? _recognizer;
    private volatile WhisperRecognizer? _hinglish;
    private readonly object _hinglishGate = new();
    private Task? _hinglishLoad;
    private volatile bool _hinglishDictation;
    private WhisperRecognizer? _captureRecognizer;
    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private SpeechDetector? _detector;
    private Task? _queueTask;
    private CancellationTokenSource _actionCts = new();
    private int _generation;
    private int _queuedUtterances;
    private bool _captureStartedMicrophone;
    private volatile bool _capturing;
    private volatile bool _pausePending = true;
    private volatile InteractionMode _mode = InteractionMode.Paused;

    public AssistantHost(AppSettings settings, Dispatcher ui, IDesktop? desktop = null, bool persistMemory = true)
    {
        _settings = settings;
        _ui = ui;
        _hinglishDictation = persistMemory && File.Exists(AppPaths.HinglishDictationFlag);
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
        if (persistMemory && _memory is not null)
        {
            _controller.MemoryChanged += () => LearnedStore.Save(_memory);
        }

        _segmenter = new UtteranceSegmenter(new SegmenterOptions { EndSilenceMs = settings.EndSilenceMs });
        _segmenter.SpeechStarted += () =>
        {
            if (_mode != InteractionMode.Standby)
            {
                PostActivity("Hearing you…");
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
                await StopPreviewAsync().ConfigureAwait(false);
                StopMicrophone();
                _capturing = false;
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

    /// <summary>Raised on the UI thread with a transient activity label (null clears it).</summary>
    public event Action<InteractionMode, string?>? Activity;

    /// <summary>Raised on the UI thread with the live transcript while dictation records (null clears it).</summary>
    public event Action<string?>? DictationPreview;

    /// <summary>Raised on the UI thread when dictation switches between English (false) and Hindi + English (true).</summary>
    public event Action<bool>? DictationLanguageChanged;

    /// <summary>Raised on the UI thread with numbers to draw on screen (null hides them).</summary>
    public event Action<IReadOnlyList<ChoiceBadge>?>? ChoicesChanged;

    /// <summary>Raised on the UI thread with something to outline briefly (just clicked, selected, or focused).</summary>
    public event Action<ScreenRect>? Highlight;

    /// <summary>True when dictation uses the Hindi + English model.</summary>
    public bool HinglishDictation => _hinglishDictation;

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

    /// <summary>Starts recording for push-to-talk or hands-free dictation (works even while paused).</summary>
    public void BeginDictationCapture(string label) => Enqueue(() =>
    {
        if (_capturing || _recognizer is null)
        {
            if (_recognizer is null)
            {
                Report(StatusKind.Warning, "Still loading the speech model…");
            }

            return Task.CompletedTask;
        }

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
                return Task.CompletedTask;
            }
        }

        _capturing = true;
        _captureRecognizer = _recognizer;
        if (_hinglishDictation)
        {
            if (_hinglish is not null)
            {
                _captureRecognizer = _hinglish;
            }
            else
            {
                EnsureHinglishLoading();
                Report(StatusKind.Warning, "The Hindi + English model isn't ready yet; this dictation uses English.");
            }
        }

        _sounds.Play(SoundFeedback.Cue.StartTyping);
        PostActivity(label);
        if (_settings.LivePreview)
        {
            _previewCts = new CancellationTokenSource();
            var token = _previewCts.Token;
            var recognizer = _captureRecognizer;
            _previewTask = Task.Run(() => RunPreviewAsync(recognizer, token));
        }

        return Task.CompletedTask;
    });

    /// <summary>Stops push-to-talk / hands-free recording; transcribes and types unless cancelled.</summary>
    public void EndDictationCapture(bool cancel) => Enqueue(async () =>
    {
        if (!_capturing)
        {
            return;
        }

        _capturing = false;
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

        PostActivity(null);
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

        PostActivity("Transcribing…");
        try
        {
            var parts = new List<string>();
            foreach (var chunk in chunks)
            {
                var result = await recognizer.TranscribeAsync(chunk, _shutdown.Token).ConfigureAwait(false);
                Log.Info($"dictation chunk audio={chunk.Length * 1000 / SampleRate}ms asr={result.Elapsed.TotalMilliseconds:F0}ms{(recognizer == _hinglish ? " model=hinglish" : string.Empty)}");
                parts.Add(TranscriptText.Clean(result.Text));
            }

            _controller.InsertDictation(string.Join(" ", parts.Where(p => p.Length > 0)));
        }
        finally
        {
            PostActivity(null);
        }
    });

    public void PasteLastTranscript() => Enqueue(() =>
    {
        _controller.RepeatLastTranscript();
        return Task.CompletedTask;
    });

    /// <summary>Switches dictation between English and Hindi + English (Alt+Shift+H). Takes effect from the next phrase.</summary>
    public void ToggleDictationLanguage() => Enqueue(() =>
    {
        ApplyDictationLanguage(!_hinglishDictation);
        return Task.CompletedTask;
    });

    /// <summary>Sets the dictation language (tray menu); does nothing if it is already that language.</summary>
    public void SetDictationLanguage(bool hinglish) => Enqueue(() =>
    {
        if (hinglish != _hinglishDictation)
        {
            ApplyDictationLanguage(hinglish);
        }

        return Task.CompletedTask;
    });

    private void ApplyDictationLanguage(bool on)
    {
        _hinglishDictation = on;
        StateFlags.Set(AppPaths.HinglishDictationFlag, on);
        Log.Info($"dictation language: {(on ? "hinglish" : "english")}");
        Post(() => DictationLanguageChanged?.Invoke(on));
        if (!on)
        {
            Report(StatusKind.Info, "Dictation language: English.");
        }
        else if (_hinglish is not null)
        {
            Report(StatusKind.Info, "Dictation language: Hindi + English (Hindi is typed in Roman letters).");
        }
        else if (_recognizer is not null)
        {
            EnsureHinglishLoading();
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
        lock (_hinglishGate)
        {
            hinglishLoad = _hinglishLoad;
        }

        drained &= hinglishLoad?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        StopMicrophone();
        _microphone.Dispose();
        _sounds.Dispose();
        if (drained)
        {
            _recognizer?.Dispose();
            _hinglish?.Dispose();
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
            lock (_captureGate)
            {
                _capture.AddRange(samples);
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
    private async Task<bool> EnsureModelAsync(string file, bool firstRun = true)
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
        Report(StatusKind.Info, firstRun
            ? "First run: downloading the speech model (one time, stays on this PC)…"
            : "Downloading the Hindi + English model (about 80 MB, one time)…");
        try
        {
            // Progress shows in the pill only on first run; later downloads must not replace a "Dictating…" label.
            await ModelDownloader.DownloadAsync(file, AppPaths.ModelsDir, percent =>
            {
                if (firstRun)
                {
                    PostActivity($"Downloading speech model… {percent}%");
                }
            }, _shutdown.Token).ConfigureAwait(false);
            Log.Info($"downloaded {file}");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            Log.Error($"download of {file} failed: {ex.GetType().Name}");
            Report(StatusKind.Error, firstRun
                ? "Couldn't download the speech model. Check the internet connection and restart Voice Assistant."
                : "Couldn't download the Hindi + English model. Check the internet connection and switch it on again.");
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

        if (_settings.ListenOnStartup)
        {
            StartListening();
        }
        else
        {
            Report(StatusKind.Info, $"Ready. Press {_settings.Hotkey} to start listening.");
        }
    }

    /// <summary>Downloads (first time) and loads the Hindi + English model in the background, so commands keep working meanwhile.</summary>
    private void EnsureHinglishLoading()
    {
        lock (_hinglishGate)
        {
            if (_hinglish is null && _hinglishLoad is null)
            {
                _hinglishLoad = Task.Run(LoadHinglishAsync);
            }
        }
    }

    private async Task LoadHinglishAsync()
    {
        try
        {
            if (!await EnsureModelAsync(_settings.HinglishModelFile, firstRun: false).ConfigureAwait(false))
            {
                return;
            }

            var started = DateTime.UtcNow;
            var recognizer = new WhisperRecognizer(Path.Combine(AppPaths.ModelsDir, _settings.HinglishModelFile), new RecognizerOptions
            {
                Threads = _settings.Threads,
                AudioContextSize = _settings.AudioContextSize,
            });
            await recognizer.TranscribeAsync(new float[SampleRate], _shutdown.Token).ConfigureAwait(false);
            _hinglish = recognizer;
            Log.Info($"hinglish model loaded in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
            if (_hinglishDictation)
            {
                Report(StatusKind.Success, "Hindi + English dictation is ready (Hindi is typed in Roman letters).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"hinglish model load failed: {ex.GetType().Name}");
            Report(StatusKind.Error, "Couldn't load the Hindi + English model. Delete it from the models folder and switch it on again.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_hinglishGate)
            {
                // Allow another attempt after a failure.
                _hinglishLoad = null;
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
            // Push-to-talk owns the microphone until it ends.
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

        var generation = Volatile.Read(ref _generation);
        if (Interlocked.Increment(ref _queuedUtterances) > MaxQueuedUtterances)
        {
            Interlocked.Decrement(ref _queuedUtterances);
            Log.Warn("utterance queue full; dropped one");
            return;
        }

        Enqueue(() => TranscribeAndHandleAsync(utterance, generation));
    }

    private bool IsStale(int generation) => _pausePending || generation != Volatile.Read(ref _generation);

    private async Task TranscribeAndHandleAsync(Utterance utterance, int generation)
    {
        try
        {
            if (IsStale(generation) || _recognizer is null)
            {
                return;
            }

            // Silero drops music/noise and splits phrases that background sound merged together.
            IReadOnlyList<float[]> pieces = _detector is null
                ? [utterance.Samples]
                : SpeechSegments.Split(utterance.Samples, _detector.Detect(utterance.Samples), SampleRate, _settings.EndSilenceMs, 250);
            if (pieces.Count == 0)
            {
                Log.Info($"no speech in {utterance.Samples.Length * 1000 / SampleRate}ms utterance");
                return;
            }

            for (var i = 0; i < pieces.Count; i++)
            {
                await HandlePieceAsync(pieces[i], utterance.FollowsForcedCut && i == 0, generation).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queuedUtterances);
            PostActivity(null);
        }
    }

    private async Task HandlePieceAsync(float[] samples, bool followsForcedCut, int generation)
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
        if (_controller.Mode != InteractionMode.Standby)
        {
            PostActivity("Transcribing…");
        }

        var result = await _recognizer.TranscribeAsync(samples, cancel.Token).ConfigureAwait(false);
        Log.Info($"utterance audio={audioMs}ms asr={result.Elapsed.TotalMilliseconds:F0}ms p={result.Probability:F2} continuation={followsForcedCut}");

        if (IsStale(generation))
        {
            return;
        }

        // While typing in Hindi + English, commands are still recognized in English; text to type comes from the Hindi model.
        var hinglish = _hinglishDictation && _controller.Mode == InteractionMode.Dictation ? _hinglish : null;
        if (hinglish is null && audioMs < 2500 && KnownHallucinations.Contains(TranscriptText.Normalize(result.Text)))
        {
            return;
        }

        Func<Task<string>>? dictation = hinglish is null ? null : async () =>
        {
            var typed = await hinglish.TranscribeAsync(samples, cancel.Token).ConfigureAwait(false);
            Log.Info($"utterance hinglish asr={typed.Elapsed.TotalMilliseconds:F0}ms");
            return audioMs < 2500 && KnownHallucinations.Contains(TranscriptText.Normalize(typed.Text)) ? string.Empty : typed.Text;
        };

        await _controller.HandleTranscriptAsync(result.Text, result.Probability, followsForcedCut, cancel.Token, dictation).ConfigureAwait(false);
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

    private void Report(StatusKind kind, string message) =>
        Post(() => Status?.Invoke(new StatusUpdate(_mode, kind, message)));

    private void PostActivity(string? activity) => Post(() => Activity?.Invoke(_mode, activity));

    private void Post(Action action)
    {
        if (!_ui.HasShutdownStarted)
        {
            _ui.BeginInvoke(action);
        }
    }
}
