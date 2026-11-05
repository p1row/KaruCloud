using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace KaruCloud
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Whatever goes wrong ends up in %APPDATA%\KaruCloud\crash.log instead of vanishing.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Store.Log("fatal", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
            TaskScheduler.UnobservedTaskException += (_, e) => { Store.Log("task", e.Exception); e.SetObserved(); };

            using var mutex = new Mutex(true, "KaruCloud.Single", out bool first);
            var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "KaruCloud.Show");
            if (!first) { showEvt.Set(); return; } // already running: just bring it forward

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // A stray error in one handler (a bad network reply, a window that just closed...) must not kill the player.
            app.DispatcherUnhandledException += (_, e) => { Store.Log("ui", e.Exception); e.Handled = true; };

            MainWindow win;
            try { win = new MainWindow(); }
            catch (Exception ex)
            {
                Store.Log("startup", ex);
                MessageBox.Show("KaruCloud couldn't start:\n\n" + ex.Message + "\n\nDetails were saved to:\n" + Path.Combine(Store.Dir, "crash.log"),
                    "KaruCloud", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var listener = new Thread(() =>
            {
                while (showEvt.WaitOne())
                    app.Dispatcher.BeginInvoke(new Action(win.ShowFromTray));
            }) { IsBackground = true };
            listener.Start();

            // Start hidden (tray only) when launched at login or when "Start minimized" is on.
            if (!(args.Contains("--hidden") || win.StartMinimized)) win.Show();

            app.Run();
        }
    }
}
