using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Kudurescue.Core;
using Kudurescue.Views;

namespace Kudurescue
{
    public static class Program
    {
        /// <summary>
        /// KUDURESCUE.exe                 open
        /// KUDURESCUE.exe --scan D:       open and start a Quick scan of D: (used after restarting as administrator)
        /// KUDURESCUE.exe --scan D: --deep   the same with a Deep scan
        /// KUDURESCUE.exe --drive D:\     open with D: chosen (the drive right-click menu)
        /// KUDURESCUE.exe --open scan.kudurescue   open a saved scan
        /// KUDURESCUE.exe --register-shell | --unregister   used by the installer
        /// KUDURESCUE.exe --self-test     automated checks for the build
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            var app = new App();
            if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase))) return app.RunSelfTest();

            if (args.Any(a => string.Equals(a, "--register-shell", StringComparison.OrdinalIgnoreCase)))
            {
                Settings.ShellMenu = true;
                ShellIntegration.Register();
                return 0;
            }
            if (args.Any(a => string.Equals(a, "--unregister", StringComparison.OrdinalIgnoreCase)))
            {
                ShellIntegration.Remove();
                return 0;
            }

            string Value(string name)
            {
                for (var i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                return null;
            }
            var deep = args.Any(a => string.Equals(a, "--deep", StringComparison.OrdinalIgnoreCase));
            var open = Value("--open") ?? args.FirstOrDefault(a => a.EndsWith(SavedScan.Extension, StringComparison.OrdinalIgnoreCase));
            return app.Run(Value("--scan"), deep, Value("--drive"), open);
        }
    }

    public sealed class App : Application
    {
        public static App Instance => (App)Current;

        public int Run(string scanDrive, bool deep, string drive, string savedScan)
        {
            Startup += (s, e) =>
            {
                Init();
                var main = new MainWindow();
                MainWindow = main;
                main.Show();
                ShellIntegration.Refresh();
                if (!string.IsNullOrEmpty(scanDrive)) main.StartScanFromCommandLine(scanDrive, deep);
                else if (!string.IsNullOrEmpty(savedScan)) main.OpenSavedScan(savedScan);
                else if (!string.IsNullOrEmpty(drive)) main.ChooseDrive(drive);
            };
            return Run();
        }

        public int RunSelfTest()
        {
            var code = 1;
            Startup += (s, e) =>
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DispatcherUnhandledException += OnUnhandledException;
                code = SelfTest.Run();
                Shutdown(code);
            };
            Run();
            return code;
        }

        internal void Init()
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            DispatcherUnhandledException += OnUnhandledException;
            ThemeManager.Init(Settings.Theme);
            ThemeManager.WatchSystem();
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (SelfTest.Running)
            {
                SelfTest.Errors.Add(e.Exception.ToString());
                e.Handled = true;
                return;
            }
            Dialogs.Error(MainWindow?.IsVisible == true ? MainWindow : null, "Something went wrong:\n\n" + e.Exception.Message);
            e.Handled = true;
        }
    }
}
