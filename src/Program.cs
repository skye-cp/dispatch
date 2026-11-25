using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Dispatch.Steam;

namespace Dispatch
{
    internal static class Program
    {
        private const string InstanceName = "Local\\Dispatch.SingleInstance";

        [STAThread]
        private static int Main(string[] args)
        {
            // The same executable doubles as the short-lived Steam lookup process.
            if (args.Length == 2 && args[0] == LobbyClient.WorkerFlag)
            {
                try
                {
                    return LookupWorker.Run(args[1]);
                }
                catch (Exception e)
                {
                    Console.Out.WriteLine(Protocol.EncodeError("crash", "The Steam lookup failed: " + e.Message));
                    Console.Out.Flush();
                    return 5;
                }
            }

            var demo = args.Contains("--demo");
            if (demo)
            {
                // The demo feeds documentation screenshots, so it uses one fixed number format.
                System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
                System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            }
            using (var instance = new Mutex(true, demo ? InstanceName + ".Demo" : InstanceName, out var first))
            {
                if (!first)
                {
                    ShowRunningInstance();
                    return 0;
                }

                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    // A bug should cost one action, not the whole window: log it, tell the user, carry on.
                    Log(e.Exception);
                    (app.MainWindow as MainWindow)?.ShowUnexpected(e.Exception);
                    e.Handled = true;
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject as Exception);
                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    Log(e.Exception);
                    e.SetObserved();
                };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Dispatch;component/Theme.xaml"),
                });
                return app.Run(new MainWindow(demo));
            }
        }

        /// <summary>Appends an unexpected error to %LOCALAPPDATA%\Dispatch\error.log, kept small, for bug reports.</summary>
        internal static void Log(Exception e)
        {
            if (e == null)
            {
                return;
            }

            try
            {
                var path = System.IO.Path.Combine(GameLocator.CacheFolder, "error.log");
                System.IO.Directory.CreateDirectory(GameLocator.CacheFolder);
                if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 256 * 1024)
                {
                    System.IO.File.Delete(path);
                }

                System.IO.File.AppendAllText(path, DateTime.UtcNow.ToString("u", System.Globalization.CultureInfo.InvariantCulture) + "  " + e + "\n\n");
            }
            catch (Exception)
            {
                // Logging must never cause a second failure.
            }
        }

        /// <summary>A second launch brings the open window forward instead of starting another copy.</summary>
        private static void ShowRunningInstance()
        {
            using (var self = Process.GetCurrentProcess())
            {
                var all = Process.GetProcessesByName(self.ProcessName);
                try
                {
                    ShowOther(all.FirstOrDefault(p => p.Id != self.Id && p.MainWindowHandle != IntPtr.Zero));
                }
                finally
                {
                    foreach (var p in all)
                    {
                        p.Dispose();
                    }
                }
            }
        }

        private static void ShowOther(Process other)
        {
            {
                if (other == null)
                {
                    return;
                }

                if (IsIconic(other.MainWindowHandle))
                {
                    ShowWindow(other.MainWindowHandle, RestoreCommand);
                }

                SetForegroundWindow(other.MainWindowHandle);
            }
        }

        private const int RestoreCommand = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hwnd);
    }
}
