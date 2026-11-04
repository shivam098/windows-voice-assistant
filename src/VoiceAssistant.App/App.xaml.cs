using System.Windows;
using System.Windows.Threading;
using VoiceAssistant.App.Platform;
using VoiceAssistant.App.UI;

namespace VoiceAssistant.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private AssistantHost? _host;
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private GlobalHotkey? _pasteHotkey;
    private GlobalHotkey? _languageHotkey;
    private DictationKeyHook? _keyHook;
    private OverlayWindow? _overlay;
    private MarkerOverlay? _markers;
    private DictationState _dictation;

    private enum DictationState
    {
        None,
        Holding,
        HandsFree,
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] == "--self-test")
        {
            Log.Initialize();
            _ = SelfTest.RunAsync(e.Args[1], e.Args.Length > 2 ? e.Args[2] : e.Args[1] + ".out.txt", Dispatcher)
                .ContinueWith(_ => Dispatcher.BeginInvoke(() => Shutdown()), TaskScheduler.Default);
            return;
        }

        _singleInstance = new Mutex(false, @"Local\VoiceAssistant.SingleInstance");
        try
        {
            _ownsMutex = _singleInstance.WaitOne(TimeSpan.FromSeconds(3));
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            System.Windows.MessageBox.Show("Voice Assistant is already running — look for its icon in the system tray.", "Voice Assistant");
            Shutdown();
            return;
        }

        Log.Initialize();
        Log.Info($"starting {typeof(App).Assembly.GetName().Version} on {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        DispatcherUnhandledException += OnUnhandledException;

        var settings = AppSettings.LoadOrCreate();
        _overlay = new OverlayWindow(commandModeIsIdle: !settings.WakeWordEnabled);
        _overlay.Show();
        _overlay.Minimized = File.Exists(AppPaths.OverlayMinimizedFlag);

        _host = new AssistantHost(settings, Dispatcher);
        _tray = new TrayIcon(_host, settings.Hotkey, settings.DictationLanguageHotkey, _overlay, () => Shutdown());
        _overlay.MinimizedChanged += minimized =>
        {
            StateFlags.Set(AppPaths.OverlayMinimizedFlag, minimized);
            if (minimized)
            {
                _tray.ShowBalloon("The status pill now appears only while you dictate or give a command. Tray menu › Always show status pill brings it back.", warning: false);
            }
        };
        _host.Status += update =>
        {
            _overlay.ShowStatus(update);
            _tray.Update(update.Mode);
        };
        _host.Activity += (mode, activity) => _overlay.ShowMode(mode, activity);
        _host.DictationPreview += _overlay.ShowPreview;
        _host.DictationLanguageChanged += _overlay.SetDictationLanguage;
        _overlay.SetDictationLanguage(_host.HinglishDictation);
        _markers = new MarkerOverlay();
        _host.ChoicesChanged += _markers.ShowBadges;
        _host.Highlight += _markers.Flash;

        _hotkey = TryRegister(settings.Hotkey, _host.ToggleListening, "pause/resume");
        _pasteHotkey = TryRegister(settings.PasteLastHotkey, _host.PasteLastTranscript, "paste last dictation");
        _languageHotkey = TryRegister(settings.DictationLanguageHotkey, _host.ToggleDictationLanguage, "switch dictation language");
        if (settings.PushToTalk)
        {
            InstallDictationKeys();
        }

        _host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("exiting");
        _keyHook?.Dispose();
        _hotkey?.Dispose();
        _pasteHotkey?.Dispose();
        _languageHotkey?.Dispose();
        _host?.Dispose();
        _tray?.Dispose();
        if (_ownsMutex)
        {
            _singleInstance?.ReleaseMutex();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Wispr-style keys: hold Ctrl+Win to talk, Ctrl+Win+Space for hands-free, Esc to cancel.</summary>
    private void InstallDictationKeys()
    {
        _keyHook = new DictationKeyHook(Dispatcher);
        if (!_keyHook.IsInstalled)
        {
            _tray?.ShowBalloon("Couldn't install the Ctrl+Win dictation keys.");
            return;
        }

        void Begin(DictationState state, string label)
        {
            _dictation = state;
            _keyHook.IsRecording = true;
            _host!.BeginDictationCapture(label);
        }

        void End(bool cancel)
        {
            _dictation = DictationState.None;
            _keyHook.IsRecording = false;
            _host!.EndDictationCapture(cancel);
        }

        _keyHook.HoldStarted += () =>
        {
            if (_dictation == DictationState.None)
            {
                Begin(DictationState.Holding, "Dictating… release Ctrl+Win to type");
            }
        };
        _keyHook.HoldEnded += () =>
        {
            if (_dictation == DictationState.Holding)
            {
                End(cancel: false);
            }
        };
        _keyHook.HoldAborted += () =>
        {
            if (_dictation == DictationState.Holding)
            {
                End(cancel: true);
            }
        };
        _keyHook.HandsFreeToggled += () =>
        {
            switch (_dictation)
            {
                case DictationState.Holding:
                    _dictation = DictationState.HandsFree;
                    _host!.BeginDictationCapture("Dictating hands-free… Ctrl+Win+Space to finish");
                    break;
                case DictationState.HandsFree:
                    End(cancel: false);
                    break;
                default:
                    Begin(DictationState.HandsFree, "Dictating hands-free… Ctrl+Win+Space to finish");
                    break;
            }
        };
        _keyHook.CancelPressed += () =>
        {
            if (_dictation != DictationState.None)
            {
                End(cancel: true);
            }
        };
    }

    private GlobalHotkey? TryRegister(string chord, Action action, string purpose)
    {
        try
        {
            var hotkey = new GlobalHotkey(chord, action);
            if (!hotkey.IsRegistered)
            {
                _tray?.ShowBalloon($"{chord} ({purpose}) is used by another app.");
            }

            return hotkey;
        }
        catch (ArgumentException ex)
        {
            _tray?.ShowBalloon(ex.Message);
            return null;
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"unhandled UI exception: {e.Exception.GetType().Name} 0x{e.Exception.HResult:X8}");
        _tray?.ShowBalloon("Something went wrong: " + e.Exception.Message);
        e.Handled = true;
    }
}
