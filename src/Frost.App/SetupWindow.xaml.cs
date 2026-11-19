using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Frost.Shell;

namespace Frost.App;

/// <summary>Setup mode. DialogResult true = "Run without installing"; anything else = exit.</summary>
public partial class SetupWindow : Window
{
    private bool _busy;

    public SetupWindow()
    {
        InitializeComponent();
        Closing += (_, e) => e.Cancel = _busy; // never quit halfway through copying Frost.exe
        TitleText.Text = Title = $"Install Frost {Installer.Version}";
        PathBox.Text = Installer.Dir;
        // Reinstall/update: show what is actually there now.
        DefaultCheck.IsChecked = DefaultFileManager.IsEnabled();
        if (File.Exists(Installer.Exe))
        {
            StartMenuCheck.IsChecked = File.Exists(Installer.StartMenuLink);
            DesktopCheck.IsChecked = File.Exists(Installer.DesktopLink);
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        IsEnabled = false; // also blocks Esc/Cancel while copying
        _busy = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var upgrading = await Task.Run(Installer.CopySelf);
            Installer.Register(upgrading, StartMenuCheck.IsChecked == true, DesktopCheck.IsChecked == true, DefaultCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
            _busy = false;
            DialogWindow.Show(this, "Couldn't install Frost", ex.Message, new[] { "OK" }, null, out _);
            return;
        }
        Mouse.OverrideCursor = null;
        _busy = false;
        if (LaunchCheck.IsChecked == true)
            try { Process.Start(new ProcessStartInfo(Installer.Exe) { UseShellExecute = false }); } catch { }
        DialogResult = false;
    }

    private void Run_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) try { DragMove(); } catch { }
    }
}
