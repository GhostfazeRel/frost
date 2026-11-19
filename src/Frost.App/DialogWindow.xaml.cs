using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Frost.App;

public partial class DialogWindow : Window
{
    private int _result = -1;

    private DialogWindow() { InitializeComponent(); }

    /// <summary>Returns the index of the clicked button, or -1 if dismissed. Must run on the UI thread.</summary>
    public static int Show(Window? owner, string title, string message, string[] buttons, string? checkText, out bool isChecked)
    {
        var d = new DialogWindow();
        if (owner != null && owner.IsVisible) d.Owner = owner; else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.TitleText.Text = title;
        d.MessageText.Text = message;
        if (checkText != null) { d.Check.Content = checkText; d.Check.Visibility = Visibility.Visible; }
        for (int i = 0; i < buttons.Length; i++)
        {
            var idx = i;
            var b = new Button
            {
                Content = buttons[i], MinWidth = 84, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                Style = (Style)Application.Current.FindResource(i == 0 ? "AccentPill" : "PillButton"),
                IsDefault = i == 0, IsCancel = i == buttons.Length - 1 && buttons.Length > 1
            };
            b.Click += (_, _) => { d._result = idx; d.Close(); };
            d.Buttons.Children.Add(b);
        }
        d.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) d.Close(); };
        d.ShowDialog();
        isChecked = d.Check.IsChecked == true;
        return d._result;
    }

    private void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) try { DragMove(); } catch { }
    }
}
