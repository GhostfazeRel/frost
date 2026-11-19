using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Frost.Core;

namespace Frost.App;

public partial class App : Application
{
    public static string? StartPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation) Resources["HoverDur"] = new Duration(TimeSpan.Zero);
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // dialogs shown before the main window must not end the app

        var args = e.Args;
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            if (DialogWindow.Show(null, "Uninstall Frost?", @"Your settings in %AppData%\Frost are kept.", new[] { "Uninstall", "Cancel" }, null, out _) == 0)
                Installer.Uninstall();
            Shutdown();
            return;
        }
        if (Installer.IsSetupMode(args))
        {
            var w = Array.FindIndex(args, a => a.Equals("--wait-pid", StringComparison.OrdinalIgnoreCase));
            if (w >= 0 && w + 1 < args.Length && int.TryParse(args[w + 1], out var pid)) Installer.WaitPid = pid;
            if (new SetupWindow().ShowDialog() != true) { Shutdown(); return; }
        }
        else if (args.Length > 0)
        {
            var path = PathUtil.FromShellArg(args[0]);
            if (Directory.Exists(path)) StartPath = path;
            else
            {
                // e.g. ::{CLSID} (Recycle Bin, Control Panel) sent to us while Frost is the default: let Explorer show it.
                try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path } }); } catch { }
                Shutdown();
                return;
            }
        }

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Frost");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:O}\n{e.Exception}\n\n");
        }
        catch { }
        e.Handled = true; // keep the window alive; details are in %AppData%\Frost\crash.log
    }
}
