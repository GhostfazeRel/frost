using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Frost.Shell;

namespace Frost.App;

/// <summary>Setup mode. DialogResult true = "Run without installing"; anything else = exit.</summary>
public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
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

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            Installer.Install(StartMenuCheck.IsChecked == true, DesktopCheck.IsChecked == true, DefaultCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            DialogWindow.Show(this, "Couldn't install Frost", ex.Message, new[] { "OK" }, null, out _);
            return;
        }
        Mouse.OverrideCursor = null;
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
