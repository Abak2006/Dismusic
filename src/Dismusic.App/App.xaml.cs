using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dismusic.App.ViewModels;
using Dismusic.Core;
using Dismusic.Core.Settings;

namespace Dismusic.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\Dismusic.SingleInstance";
    private const string ActivateEventName = @"Local\Dismusic.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The UI is static forms, so software rendering costs nothing noticeable, and it avoids blank windows
        // seen with WPF's GPU path on some hybrid-graphics laptops (reproduced on the development machine).
        System.Windows.Media.RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        if (!isFirstInstance)
        {
            _activateEvent.Set(); // bring the running instance to the front instead
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        // Sign-out/shutdown: stop processing and give enhanced apps their volume back before Windows ends us.
        SessionEnding += (_, _) => ExitApp();
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        var store = new SettingsStore();
        try
        {
            var settings = store.Load(out var warning);
            _viewModel = new MainViewModel(store, settings, warning);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Startup failed", ex);
            MessageBox.Show("Dismusic couldn't connect to the Windows audio service, so it can't start. Check that Windows Audio is running and try again.",
                "Dismusic", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _window = new MainWindow { DataContext = _viewModel };
        _window.Closing += (_, args) =>
        {
            if (_exiting) return;
            args.Cancel = true; // keep running in the tray
            _window.Hide();
        };
        _tray = new TrayIcon(_viewModel, ShowWindow, ExitApp);
        _window.Icon = Imaging.CreateBitmapSourceFromHIcon(_tray.Image.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        bool startHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase) || _viewModel.StartMinimized;
        if (!startHidden) ShowWindow();
        Log.Info("Dismusic started");
    }

    private void ShowWindow()
    {
        if (_window is null || _exiting) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _activateWait?.Unregister(null);
        _viewModel?.Dispose();
        _tray?.Dispose();
        _window?.Close();
        Log.Info("Dismusic exited");
        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show("Something went wrong in Dismusic. Details were written to the log (Settings › Open log folder).",
            "Dismusic", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // keep routing running; the failed action is reported above
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting) _viewModel?.Dispose();
        _tray?.Dispose();
        _activateEvent?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { /* not owned (second instance) */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
