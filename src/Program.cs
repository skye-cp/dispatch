using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
                return LookupWorker.Run(args[1]);
            }

            var demo = args.Contains("--demo");
            using (var instance = new Mutex(true, demo ? InstanceName + ".Demo" : InstanceName, out var first))
            {
                if (!first)
                {
                    ShowRunningInstance();
                    return 0;
                }

                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Dispatch;component/Theme.xaml"),
                });
                return app.Run(new MainWindow(demo));
            }
        }

        /// <summary>A second launch brings the open window forward instead of starting another copy.</summary>
        private static void ShowRunningInstance()
        {
            using (var self = Process.GetCurrentProcess())
            {
                var other = Process.GetProcessesByName(self.ProcessName)
                    .FirstOrDefault(p => p.Id != self.Id && p.MainWindowHandle != IntPtr.Zero);
                if (other == null)
                {
                    return;
                }

                if (IsIconic(other.MainWindowHandle))
                {
                    ShowWindow(other.MainWindowHandle, RestoreCommand);
                }

                SetForegroundWindow(other.MainWindowHandle);
                other.Dispose();
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
