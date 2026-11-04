using System.Threading.Channels;
using System.Windows.Threading;
using VoiceAssistant.App.Platform;
using VoiceAssistant.Core;
using VoiceAssistant.Speech;

namespace VoiceAssistant.App;

/// <summary>
/// Wires microphone -> utterance segmenter -> Whisper -> interaction controller.
/// All controller work runs on one serial queue so utterances are handled strictly in order.
/// </summary>
internal sealed class AssistantHost : IDisposable
{
    private static readonly HashSet<string> KnownHallucinations =
        ["you", "thanks for watching", "thank you for watching", "please subscribe"];

    private const int MaxQueuedUtterances = 6;

    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _segmenterGate = new();
    private readonly UtteranceSegmenter _segmenter;
    private readonly MicrophoneCapture _microphone;
    private readonly AppCatalog _catalog;
    private readonly InteractionController _controller;
    private WhisperRecognizer? _recognizer;
    private Task? _queueTask;
    private CancellationTokenSource _actionCts = new();
    private int _generation;
    private int _queuedUtterances;
    private volatile bool _pausePending = true;
    private volatile InteractionMode _mode = InteractionMode.Paused;

    public AssistantHost(AppSettings settings, Dispatcher ui)
    {
        _settings = settings;
        _ui = ui;
        _catalog = new AppCatalog(settings.Apps);
        _controller = new InteractionController(
            new WindowsDesktop(),
            _catalog,
            new CommandParser(settings.CommandPrefix, settings.RequireCommandPrefixWhileTyping),
            new ControllerOptions
            {
                ReturnToCommandModeAfterSend = settings.ReturnToCommandModeAfterSend,
                MinCommandConfidence = settings.MinCommandConfidence,
            });
        _controller.Status += update =>
        {
            _mode = update.Mode;
            Post(() => Status?.Invoke(update));
        };
        _controller.PauseRequested += () =>
        {
            _pausePending = true;
            StopMicrophone();
        };

        _segmenter = new UtteranceSegmenter(new SegmenterOptions { EndSilenceMs = settings.EndSilenceMs });
        _segmenter.SpeechStarted += () => PostActivity("Hearing you…");
        _segmenter.UtteranceReady += OnUtterance;

        _microphone = new MicrophoneCapture(settings.MicrophoneDevice);
        _microphone.Samples += samples =>
        {
            lock (_segmenterGate)
            {
                _segmenter.Push(samples);
            }
        };
        _microphone.Failed += ex =>
        {
            Log.Error($"microphone failed: {ex.GetType().Name}");
            CancelPendingWork();
            Enqueue(() =>
            {
                StopMicrophone();
                _controller.SetListening(false);
                Report(StatusKind.Error, $"The microphone stopped. Press {_settings.Hotkey} to try again.");
                return Task.CompletedTask;
            });
        };
    }

    /// <summary>Raised on the UI thread.</summary>
    public event Action<StatusUpdate>? Status;

    /// <summary>Raised on the UI thread with a transient activity label (null clears it).</summary>
    public event Action<InteractionMode, string?>? Activity;

    public InteractionMode Mode => _mode;

    public void Start()
    {
        _queueTask = Task.Run(ProcessQueueAsync);
        Enqueue(LoadAsync);
        if (_settings.DiscoverStartMenuApps)
        {
            _ = Task.Run(() =>
            {
                var apps = StartMenuApps.Discover();
                _catalog.SetDiscoveredApps(apps);
                Log.Info($"start menu apps discovered: {apps.Count}");
            });
        }
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
        CancelPendingWork();
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

        StopMicrophone();
        _microphone.Dispose();
        if (drained)
        {
            _recognizer?.Dispose();
        }
        else
        {
            // Freeing the model while Whisper is mid-transcription crashes; the OS reclaims it at exit instead.
            Log.Warn("queue did not drain; leaving speech model to process exit");
        }
    }

    /// <summary>Drops queued speech and cancels in-flight actions (open/focus) immediately, from any thread.</summary>
    private void CancelPendingWork()
    {
        _pausePending = true;
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _actionCts, new CancellationTokenSource()).Cancel();
    }

    private async Task LoadAsync()
    {
        var modelPath = Path.Combine(AppPaths.ModelsDir, _settings.ModelFile);
        if (!File.Exists(modelPath))
        {
            Report(StatusKind.Error, $"Speech model not found ({_settings.ModelFile}). Re-run install.ps1.");
            return;
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
            await _recognizer.TranscribeAsync(new float[WhisperRecognizer.SampleRate], _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"model load failed: {ex.GetType().Name}");
            Report(StatusKind.Error, "Couldn't load the speech model. Re-run install.ps1.");
            return;
        }

        Log.Info($"model loaded in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
        if (_settings.ListenOnStartup)
        {
            StartListening();
        }
        else
        {
            Report(StatusKind.Info, $"Ready. Press {_settings.Hotkey} to start listening.");
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

        Interlocked.Increment(ref _generation);
        _pausePending = false;
        _controller.SetListening(true);
        Log.Info("listening");
    }

    private void StopMicrophone()
    {
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
            Report(StatusKind.Warning, "I'm falling behind — skipped some speech.");
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

            var audioMs = utterance.Samples.Length * 1000 / WhisperRecognizer.SampleRate;
            if (_controller.Mode == InteractionMode.Command && audioMs > _settings.MaxCommandUtteranceMs)
            {
                // Commands are short; long speech while not typing is conversation or meeting audio.
                Log.Info($"skipped {audioMs}ms utterance in command mode");
                return;
            }

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, Volatile.Read(ref _actionCts).Token);
            PostActivity("Transcribing…");
            var result = await _recognizer.TranscribeAsync(utterance.Samples, cancel.Token).ConfigureAwait(false);
            Log.Info($"utterance audio={audioMs}ms asr={result.Elapsed.TotalMilliseconds:F0}ms p={result.Probability:F2} continuation={utterance.FollowsForcedCut}");

            if (IsStale(generation))
            {
                return;
            }

            if (audioMs < 2500 && KnownHallucinations.Contains(TranscriptText.Normalize(result.Text)))
            {
                return;
            }

            await _controller.HandleTranscriptAsync(result.Text, result.Probability, utterance.FollowsForcedCut, cancel.Token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _queuedUtterances);
            PostActivity(null);
        }
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
