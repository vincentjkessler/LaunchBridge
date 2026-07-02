using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DevMind.LaunchBridge
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            EnableHighDpiRendering();

            if (IsCommandLineAction(args))
            {
                InitializeApplicationServices();
                LaunchBridgeCore.Initialize();
                HandleCommandLineAction(args);
                return;
            }

            // Take the single-instance mutex before loading configuration, starting runtime
            // monitors, or constructing WinForms. A browser "Open file" invocation can then
            // hand its package path to the resident LaunchBridge process in a few milliseconds.
            using (SingleInstanceCoordinator coordinator = new SingleInstanceCoordinator())
            {
                if (!coordinator.IsPrimary)
                {
                    if (!coordinator.ForwardToPrimary(args))
                    {
                        MessageBox.Show(
                            "LaunchBridge is already open, but Windows could not deliver this package to it. Close the existing LaunchBridge window and try Open file again.",
                            "LaunchBridge",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                    return;
                }

                InitializeApplicationServices();
                LaunchBridgeCore.Initialize();

                string packagePath = FindPackagePath(args);
                string completedUpdateVersion = FindArgumentValue(args, "--update-complete");
                bool residentStart = HasArgument(args, "--resident");
                bool packageOpenStart = !string.IsNullOrWhiteSpace(packagePath);
                string initialSource = HasArgument(args, "--smart-click") ? "Smart Click" : "Browser Open file";
                MainForm form = new MainForm(packagePath, residentStart || packageOpenStart, initialSource);
                if (!string.IsNullOrWhiteSpace(completedUpdateVersion))
                {
                    form.Shown += delegate
                    {
                        MessageBox.Show(
                            "LaunchBridge " + completedUpdateVersion + " is installed. Turbo Launch remains ready for browser Open file requests, and text now renders natively at the current Windows display scale.",
                            "LaunchBridge update complete",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    };
                }
                IntPtr formHandle = form.Handle;
                coordinator.StartListening(delegate(string[] forwardedArgs)
                {
                    if (form.IsDisposed) return;
                    try
                    {
                        form.BeginInvoke(new Action(delegate { form.ReceiveExternalRequest(forwardedArgs); }));
                    }
                    catch { }
                });
                Application.Run(form);
            }
        }


        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        private static void EnableHighDpiRendering()
        {
            // Per-monitor-v2 prevents Windows from bitmap-stretching the entire WinForms window
            // at 125%, 150%, or mixed-monitor scaling. The fallback covers older Windows builds.
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            catch { }

            try { SetProcessDPIAware(); }
            catch { }
        }

        private static void InitializeApplicationServices()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                LaunchBridgeCore.ReportFatal(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                LaunchBridgeCore.ReportFatal(ex == null ? new Exception("Unknown fatal error") : ex);
            };
        }

        private static bool IsCommandLineAction(string[] args)
        {
            if (args == null || args.Length == 0) return false;
            string first = args[0] ?? "";
            return first.Equals("--register", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("--unregister", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("--install-defaults", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("--self-test", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("--remove-associations", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("--install-smart-click", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("--remove-smart-click", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("--extract", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HandleCommandLineAction(string[] args)
        {
            if (args != null && args.Length >= 2 && args[0].Equals("--register", StringComparison.OrdinalIgnoreCase))
            {
                LaunchBridgeCore.RegisterExtension(args[1]);
                return true;
            }
            if (args != null && args.Length >= 2 && args[0].Equals("--unregister", StringComparison.OrdinalIgnoreCase))
            {
                LaunchBridgeCore.UnregisterExtension(args[1]);
                return true;
            }
            if (args != null && args.Length >= 1 && args[0].Equals("--install-defaults", StringComparison.OrdinalIgnoreCase))
            {
                LaunchBridgeCore.InstallDefaultAssociations();
                return true;
            }
            if (args != null && args.Length >= 1 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
            {
                string result = LaunchBridgeCore.RunSelfTest();
                MessageBox.Show(result, "LaunchBridge self-test", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            if (args != null && args.Length >= 1 && args[0].Equals("--remove-associations", StringComparison.OrdinalIgnoreCase))
            {
                LaunchBridgeCore.RemoveAllAssociations();
                return true;
            }
            if (args != null && args.Length >= 1 && args[0].Equals("--install-smart-click", StringComparison.OrdinalIgnoreCase))
            {
                LaunchBridgeCore.InstallSmartClickNativeHost();
                return true;
            }
            if (args != null && args.Length >= 1 && args[0].Equals("--remove-smart-click", StringComparison.OrdinalIgnoreCase))
            {
                LaunchBridgeCore.RemoveSmartClickNativeHost();
                return true;
            }
            if (args != null && args.Length >= 2 && args[0].Equals("--extract", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string destination = LaunchBridgeCore.ExtractPackageForUser(args[1]);
                    System.Diagnostics.Process.Start("explorer.exe", destination);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "LaunchBridge extraction failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return true;
            }
            return false;
        }

        private static bool HasArgument(string[] args, string name)
        {
            if (args == null || string.IsNullOrWhiteSpace(name)) return false;
            return args.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string FindArgumentValue(string[] args, string name)
        {
            if (args == null || string.IsNullOrWhiteSpace(name)) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static string FindPackagePath(string[] args)
        {
            if (args == null) return null;
            return args.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("--", StringComparison.Ordinal) && File.Exists(x));
        }
    }
}
