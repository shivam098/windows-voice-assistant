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
    private OverlayWindow? _overlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
            System.Windows.MessageBox.Show("Voice Assistant is already running — look for the microphone icon in the system tray.", "Voice Assistant");
            Shutdown();
            return;
        }

        Log.Initialize();
        Log.Info($"starting {typeof(App).Assembly.GetName().Version} on {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        DispatcherUnhandledException += OnUnhandledException;

        var settings = AppSettings.LoadOrCreate();
        _overlay = new OverlayWindow();
        _overlay.Show();

        _host = new AssistantHost(settings, Dispatcher);
        _tray = new TrayIcon(_host, settings.Hotkey, () => Shutdown());
        _host.Status += update =>
        {
            _overlay.ShowStatus(update);
            _tray.Update(update.Mode);
        };
        _host.Activity += (mode, activity) => _overlay.ShowMode(mode, activity);

        try
        {
            _hotkey = new GlobalHotkey(settings.Hotkey, _host.ToggleListening);
            if (!_hotkey.IsRegistered)
            {
                _tray.ShowBalloon($"{settings.Hotkey} is used by another app. Use the tray icon to pause or resume.");
            }
        }
        catch (ArgumentException ex)
        {
            _tray.ShowBalloon(ex.Message);
        }

        _host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("exiting");
        _hotkey?.Dispose();
        _host?.Dispose();
        _tray?.Dispose();
        if (_ownsMutex)
        {
            _singleInstance?.ReleaseMutex();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"unhandled UI exception: {e.Exception.GetType().Name} 0x{e.Exception.HResult:X8}");
        _tray?.ShowBalloon("Something went wrong: " + e.Exception.Message);
        e.Handled = true;
    }
}
