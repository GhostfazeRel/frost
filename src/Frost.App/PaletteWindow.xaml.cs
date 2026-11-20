using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Frost.Core;

namespace Frost.App;

/// <summary>One palette row. <paramref name="Run"/> gets true when Ctrl was held (Go to folder: open in a new tab).</summary>
public sealed record PaletteEntry(string Title, string Detail, Action<bool> Run);

public sealed class PaletteRow
{
    public PaletteRow(PaletteEntry entry, FuzzyMatch match) { Entry = entry; Match = match; }
    public PaletteEntry Entry { get; }
    public FuzzyMatch Match { get; }
    public string Detail => Entry.Detail;
}

/// <summary>Text with the fuzzy-matched characters in the accent colour.</summary>
public sealed class HighlightBlock : TextBlock
{
    public HighlightBlock() => DataContextChanged += (_, _) => Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();
        if (DataContext is not PaletteRow r) return;
        var hits = new HashSet<int>(r.Match.Indices);
        var t = r.Entry.Title;
        for (int i = 0; i < t.Length;)
        {
            int j = i; bool on = hits.Contains(i);
            while (j < t.Length && hits.Contains(j) == on) j++;
            var run = new System.Windows.Documents.Run(t[i..j]);
            if (on) { run.Foreground = (Brush)Application.Current.FindResource("AccentBrush"); run.FontWeight = FontWeights.SemiBold; }
            Inlines.Add(run);
            i = j;
        }
    }
}

/// <summary>Glass overlay with a filter box and ranked results. Closes on Esc, Enter, click or focus loss.</summary>
public partial class PaletteWindow : Window
{
    private readonly Func<string, IReadOnlyList<PaletteRow>> _source;

    public PaletteWindow(Window owner, string placeholder, Func<string, IReadOnlyList<PaletteRow>> source)
    {
        InitializeComponent();
        Owner = owner;
        _source = source;
        Hint.Text = placeholder;
        Left = owner.Left + (owner.ActualWidth - Width) / 2;
        Top = owner.Top + 60;
        Refresh();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => Input.Focus();
    private void Window_Deactivated(object? sender, EventArgs e) { if (IsVisible) Close(); }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        Hint.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void Refresh()
    {
        var rows = _source(Input.Text);
        Results.ItemsSource = rows;
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (rows.Count > 0) Results.SelectedIndex = 0;
    }

    private void Move(int d)
    {
        int n = Results.Items.Count;
        if (n == 0) return;
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + d, 0, n - 1);
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void Choose(bool ctrl)
    {
        if (Results.SelectedItem is not PaletteRow r) return;
        var owner = Owner;
        Close();
        // run after the overlay is gone so focus returns to the main window first
        owner.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => r.Entry.Run(ctrl));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.PageDown: Move(8); break;
            case Key.PageUp: Move(-8); break;
            case Key.Enter: Choose((Keyboard.Modifiers & ModifierKeys.Control) != 0); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Results_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(Results, d) is ListBoxItem li)
        {
            li.IsSelected = true;
            Choose((Keyboard.Modifiers & ModifierKeys.Control) != 0);
            e.Handled = true;
        }
    }
}
