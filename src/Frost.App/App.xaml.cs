using System.Windows;
using System.Windows.Threading;

namespace Frost.App;

public partial class App : Application
{
    public static string? StartPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && Directory.Exists(e.Args[0])) StartPath = e.Args[0];
        if (!SystemParameters.ClientAreaAnimation) Resources["HoverDur"] = new Duration(TimeSpan.Zero);
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
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
