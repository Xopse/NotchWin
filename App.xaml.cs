using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Windows;

namespace NotchWin;

public partial class App : Application
{
    private static Mutex _singleInstance;
    private static EventWaitHandle _openSettingsSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool restarting = e.Args.Contains("--restart");
        bool wantsSettings = e.Args.Contains("--settings");

        // Shared signal: launching NotchWin again (e.g. from the Start menu) opens Settings in the running copy.
        _openSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "NotchWin.OpenSettings");

        // Only one notch at a time. When restarting from Settings, wait for the old copy to exit.
        _singleInstance = new Mutex(false, "NotchWin.SingleInstance");
        bool acquired;
        try { acquired = _singleInstance.WaitOne(restarting ? 5000 : 0); }
        catch (AbandonedMutexException) { acquired = true; } // previous copy exited without releasing
        if (!acquired)
        {
            _openSettingsSignal.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (s, ex) =>
        {
            Log.Write("UI crash: " + ex.Exception);
            ex.Handled = true; // keep the notch alive instead of dying on one bad update
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ex) => Log.Write("Fatal: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (s, ex) => { Log.Write("Task: " + ex.Exception); ex.SetObserved(); };

        base.OnStartup(e);
        var win = new MainWindow();
        MainWindow = win;
        win.Show();

        // Wait (on a background thread, costs nothing) for "open settings" requests from a second launch.
        var listener = new Thread(() =>
        {
            while (true)
            {
                _openSettingsSignal.WaitOne();
                Dispatcher.BeginInvoke(() => win.OpenSettings());
            }
        }) { IsBackground = true, Name = "SettingsSignal" };
        listener.Start();

        if (wantsSettings) Dispatcher.BeginInvoke(() => win.OpenSettings());
    }
}

/// <summary>Tiny log file at %AppData%\NotchWin\log.txt so you can see what went wrong.</summary>
public static class Log
{
    private static readonly object Lock = new();

    public static void Write(string msg)
    {
        try
        {
            lock (Lock)
            {
                var path = Path.Combine(AppConfig.Folder, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
