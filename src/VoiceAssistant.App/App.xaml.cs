using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using VoiceAssistant.App.Platform;
using VoiceAssistant.App.UI;
using VoiceAssistant.Board;

namespace VoiceAssistant.App;

public partial class App : System.Windows.Application
{
    private const string ShowWindowEvent = @"Local\VoiceAssistant.ShowWindow";
    private const string RestartedArgument = "--restarted";
    private const int MaxReminderPopups = 4;

    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private AssistantHost? _host;
    private BoardStore? _board;
    private MainWindow? _window;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private DispatcherTimer? _reminders;
    private readonly List<ReminderPopup> _popups = [];
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

        var background = e.Args.Contains(StartupRegistration.BackgroundArgument);
        var restarted = e.Args.Contains(RestartedArgument);
        _singleInstance = new Mutex(false, @"Local\VoiceAssistant.SingleInstance");
        try
        {
            // After “Save and restart” the old copy may need a moment to exit.
            _ownsMutex = _singleInstance.WaitOne(TimeSpan.FromSeconds(restarted ? 15 : 3));
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            // Launching it again (Start menu, desktop shortcut) opens the running copy's window.
            if (!background && EventWaitHandle.TryOpenExisting(ShowWindowEvent, out var show))
            {
                // This launch came from the user, so it may pass its right to take the foreground to the running copy.
                Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
                show.Set();
                show.Dispose();
            }
            else if (!background)
            {
                System.Windows.MessageBox.Show("Voice Assistant is already running — look for its icon in the system tray.", "Voice Assistant");
            }

            Shutdown();
            return;
        }

        Log.Initialize();
        Log.Info($"starting {typeof(App).Assembly.GetName().Version} on {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        DispatcherUnhandledException += OnUnhandledException;
        StartupRegistration.UpgradeOldEntry();

        var settings = AppSettings.LoadOrCreate();
        _overlay = new OverlayWindow(commandModeIsIdle: !settings.WakeWordEnabled);
        _overlay.Show();
        _overlay.Minimized = File.Exists(AppPaths.OverlayMinimizedFlag);

        _board = new BoardStore(AppPaths.BoardFile);
        _host = new AssistantHost(settings, Dispatcher, board: _board);
        _tray = new TrayIcon(_host, settings.Hotkey, settings.PasteLastHotkey, settings.DictationLanguageHotkey, _overlay, OpenPage, Quit);
        _window = new MainWindow(_host, settings, _board, Restart, message => _tray.ShowBalloon(message, warning: false));
        _host.ShowBoardRequested += _window.ShowBoard;
        if (!background)
        {
            _window.Show();
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEvent);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(() => _window.Bring()), null, Timeout.Infinite, executeOnlyOnce: false);
        _reminders = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _reminders.Tick += (_, _) => ShowDueReminders();
        _reminders.Start();
        Dispatcher.BeginInvoke(ShowDueReminders, DispatcherPriority.ApplicationIdle);
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
        _overlay.SetDictationLanguage(_host.HinglishDictation, _host.HinglishAccurate);
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
        _reminders?.Stop();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
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

    private void OpenPage(AppPage page)
    {
        if (page == AppPage.Ideas)
        {
            _window?.ShowBoard(null);
            return;
        }

        _window?.Bring();
        _window?.ShowPage(page);
    }

    private void Quit()
    {
        _window?.AllowClose();
        Shutdown();
    }

    /// <summary>Starts a fresh copy (which waits for this one to exit) so changed settings take effect.</summary>
    private void Restart()
    {
        var args = RestartedArgument + (_window?.IsVisible == true ? string.Empty : " " + StartupRegistration.BackgroundArgument);
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warn($"restart failed: 0x{ex.HResult:X8}");
            _tray?.ShowBalloon("Couldn't restart. Exit from the tray and start Voice Assistant again.");
            return;
        }

        Quit();
    }

    /// <summary>Pops up reminders that are due (including ones that came due while the app wasn't running).</summary>
    private void ShowDueReminders()
    {
        if (_board is null || _window is null)
        {
            return;
        }

        var shown = false;
        foreach (var item in _board.DueReminders())
        {
            if (_popups.Count >= MaxReminderPopups)
            {
                break;
            }

            var popup = new ReminderPopup(item, _board, () => _window.ShowBoard(BoardList.Reminders));
            popup.Loaded += (_, _) => StackPopups();
            popup.Closed += (_, _) =>
            {
                _popups.Remove(popup);
                StackPopups();
            };
            _popups.Add(popup);
            popup.Show();
            _board.MarkNotified(item.Id);
            Log.Info("reminder shown");
            shown = true;
        }

        if (shown)
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
    }

    /// <summary>Stacks the open reminder cards upwards from the bottom-right corner, oldest at the bottom.</summary>
    private void StackPopups()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom - 4;
        foreach (var popup in _popups.Where(p => p.IsLoaded))
        {
            popup.Left = area.Right - popup.ActualWidth - 16;
            popup.Top = bottom - popup.ActualHeight;
            bottom = popup.Top - 12;
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"unhandled UI exception: {e.Exception.GetType().Name} 0x{e.Exception.HResult:X8}");
        _tray?.ShowBalloon("Something went wrong: " + e.Exception.Message);
        e.Handled = true;
    }
}
