using System;
using System.Windows;
using Dispatch.Steam;

namespace Dispatch
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // The same executable doubles as the short-lived Steam lookup process.
            if (args.Length == 2 && args[0] == LobbyClient.WorkerFlag)
            {
                return LookupWorker.Run(args[1]);
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Dispatch;component/Theme.xaml"),
            });
            return app.Run(new MainWindow());
        }
    }
}
