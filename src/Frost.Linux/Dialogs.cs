using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Frost.Core;
namespace Frost.Linux;

/// <summary>Small code-built dialogs: message with buttons, text prompt, command palette, batch rename.</summary>
public static class Dialogs
{
    public static IBrush Res(string key) => (IBrush)Application.Current!.FindResource(key)!;

    static Window Base(string title, double width) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = Res("CardBg"),
    };

    /// <summary>Index of the pressed button; -1 for Esc or closing the window.</summary>
    public static async Task<(int Index, bool All)> Ask(Window owner, string title, string message, string[] buttons, string? checkbox = null)
    {
        var w = Base(title, 440);
        int result = -1;
        var check = checkbox == null ? null : new CheckBox { Content = checkbox };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            var b = new Button { Content = buttons[i], Classes = { i == 0 ? "accent" : "x" } };
            b.Click += (_, _) => { result = idx; w.Close(); };
            row.Children.Add(b);
        }
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (check != null) panel.Children.Add(check);
        panel.Children.Add(row);
        w.Content = panel;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        await w.ShowDialog(owner);
        return (result, check?.IsChecked == true);
    }

    /// <summary>Text prompt; null when cancelled. Selects the name without its extension, like the inline rename.</summary>
    public static async Task<string?> Prompt(Window owner, string title, string initial, bool isDir)
    {
        var w = Base(title, 420);
        string? result = null;
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel" };
        void Done() { result = box.Text; w.Close(); }
        ok.Click += (_, _) => Done();
        cancel.Click += (_, _) => w.Close();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Done(); e.Handled = true; } };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 14,
            Children = { box, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } } }
        };
        w.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectionStart = 0;
            box.SelectionEnd = isDir ? initial.Length : Path.GetFileNameWithoutExtension(initial).Length;
        };
        await w.ShowDialog(owner);
        return result;
    }

    public sealed record Row(string Title, string Right, Action Run, int[] Hits);

    /// <summary>Command palette / Go to folder: filter box and ranked results. Esc, Enter, click or focus loss closes it.</summary>
    public static void Palette(Window owner, string placeholder, Func<string, IReadOnlyList<Row>> source)
    {
        var w = new Window
        {
            Width = 560, Height = 420, SystemDecorations = SystemDecorations.BorderOnly, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Background = Res("MenuBg"),
        };
        var box = new TextBox { Watermark = placeholder, Margin = new Thickness(12, 12, 12, 6) };
        var list = new ListBox { Background = Brushes.Transparent, Margin = new Thickness(6, 0, 6, 6) };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Row>((r, _) =>
        {
            if (r == null) return null; // called with null while rows are recycled
            var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var hits = new HashSet<int>(r.Hits);
            for (int i = 0; i < r.Title.Length;)
            {
                int j = i; bool on = hits.Contains(i);
                while (j < r.Title.Length && hits.Contains(j) == on) j++;
                var run = new Run(r.Title[i..j]);
                if (on) { run.Foreground = Res("Accent"); run.FontWeight = FontWeight.SemiBold; }
                t.Inlines!.Add(run);
                i = j;
            }
            var g = new Grid { ColumnDefinitions = new("*,Auto") };
            g.Children.Add(t);
            g.Children.Add(new TextBlock { Text = r.Right, Foreground = Res("Text3"), [Grid.ColumnProperty] = 1, VerticalAlignment = VerticalAlignment.Center });
            return g;
        });
        void Refresh() { list.ItemsSource = source(box.Text ?? "").Take(200).ToList(); list.SelectedIndex = list.ItemCount > 0 ? 0 : -1; }
        void Run() { if (list.SelectedItem is Row r) { w.Close(); r.Run(); } }
        box.TextChanged += (_, _) => Refresh();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) w.Close();
            else if (e.Key == Key.Enter) Run();
            else if (e.Key == Key.Down) list.SelectedIndex = Math.Min(list.ItemCount - 1, list.SelectedIndex + 1);
            else if (e.Key == Key.Up) list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
            else return;
            e.Handled = true;
        };
        list.DoubleTapped += (_, _) => Run();
        list.Tapped += (_, _) => Run();
        w.Deactivated += (_, _) => w.Close();
        var dock = new DockPanel();
        DockPanel.SetDock(box, Dock.Top);
        dock.Children.Add(box); dock.Children.Add(list);
        w.Content = new Border { BorderBrush = Res("SubtleRim"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Child = dock };
        w.Opened += (_, _) => { Refresh(); box.Focus(); };
        w.Show(owner);
    }

    /// <summary>Batch rename with live preview (planning runs off the UI thread). Returns the renamed pairs.</summary>
    public static async Task<IReadOnlyList<(string Old, string New)>> BatchRename(Window owner, IReadOnlyList<string> paths)
    {
        var w = Base($"Rename {paths.Count} items", 560);
        IReadOnlyList<(string, string)> applied = Array.Empty<(string, string)>();
        TextBox T(string text = "", string mark = "") => new() { Text = text, Watermark = mark, MinWidth = 60 };
        var find = T(mark: "Find"); var repl = T(mark: "Replace with ({n} = number)");
        var prefix = T(mark: "Prefix"); var suffix = T(mark: "Suffix");
        prefix.Width = suffix.Width = 256; // side by side across the 520 px content width
        var start = T("1"); var step = T("1"); var width = T("1");
        var rx = new CheckBox { Content = "Regex" }; var mc = new CheckBox { Content = "Match case" }; var ext = new CheckBox { Content = "Include extension" };
        var casing = new ComboBox { ItemsSource = new[] { "Keep case", "lower", "UPPER", "Title Case" }, SelectedIndex = 0 };
        var preview = new ListBox { Height = 180, Background = Brushes.Transparent };
        var summary = new TextBlock { Classes = { "dim" }, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "Rename", Classes = { "accent" }, IsEnabled = false };
        var cancel = new Button { Content = "Cancel" };
        IReadOnlyList<BatchRenameItem> plan = Array.Empty<BatchRenameItem>();
        CancellationTokenSource? cts = null;
        bool busy = false;
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

        async void Recompute()
        {
            debounce.Stop(); cts?.Cancel();
            if (!int.TryParse(start.Text?.Trim(), out var s) || !int.TryParse(step.Text?.Trim(), out var st) || !int.TryParse(width.Text?.Trim(), out var wd) || wd is < 1 or > 18)
            { summary.Text = "Start, step and digits (1-18) must be whole numbers."; return; }
            var o = new BatchRenameOptions
            {
                Find = find.Text ?? "", Replace = repl.Text ?? "", UseRegex = rx.IsChecked == true, MatchCase = mc.IsChecked == true, Prefix = prefix.Text ?? "",
                Suffix = suffix.Text ?? "", IncludeExtension = ext.IsChecked == true, Start = s, Step = st, Width = wd, Case = (BatchCase)casing.SelectedIndex
            };
            var c = cts = new CancellationTokenSource();
            try { plan = await Task.Run(() => Core.BatchRename.Plan(paths, o, c.Token), c.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { summary.Text = "Couldn't build the preview: " + ex.Message; return; }
            if (c.IsCancellationRequested) return;
            preview.ItemsSource = plan.Select(p => p.Error != null ? $"{p.OldName}  ->  {p.NewName}   ({p.Error})" : $"{p.OldName}  ->  {p.NewName}").ToList();
            int errors = plan.Count(p => p.Error != null), changed = plan.Count(p => p.NewName != p.OldName);
            summary.Text = errors > 0 ? $"{errors} problem(s): see the lines in brackets." : $"{changed} of {plan.Count} will be renamed.";
            ok.IsEnabled = errors == 0 && changed > 0 && !busy;
        }
        void Changed() { ok.IsEnabled = false; debounce.Stop(); debounce.Start(); } // never apply a stale preview
        debounce.Tick += (_, _) => Recompute();
        foreach (var t in new[] { find, repl, prefix, suffix, start, step, width }) t.TextChanged += (_, _) => Changed();
        foreach (var c in new[] { rx, mc, ext }) c.IsCheckedChanged += (_, _) => Changed();
        casing.SelectionChanged += (_, _) => Changed();

        ok.Click += async (_, _) =>
        {
            if (!ok.IsEnabled || busy) return;
            busy = true; ok.IsEnabled = false; cancel.IsEnabled = false;
            var p = plan;
            try { applied = await Task.Run(() => Core.BatchRename.Apply(p)); busy = false; w.Close(); }
            catch (Exception ex) { busy = false; cancel.IsEnabled = true; summary.Text = ex is BatchRenameRollbackException ? ex.Message : "Nothing was renamed (changes were rolled back): " + ex.Message; }
        };
        cancel.Click += (_, _) => w.Close();
        w.Closing += (_, e) => { if (busy) e.Cancel = true; else cts?.Cancel(); }; // never close while files are being renamed
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape && !busy) w.Close(); };
        w.Opened += (_, _) => { find.Focus(); Recompute(); };

        Control L(string s, Control c) => new StackPanel { Spacing = 4, Children = { new TextBlock { Text = s, Classes = { "dim" } }, c } };
        w.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                find, repl, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { rx, mc, ext } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { prefix, suffix } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { L("Start", start), L("Step", step), L("Digits", width), L("Case", casing) } },
                preview, summary,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
            }
        };
        await w.ShowDialog(owner);
        return applied;
    }
}
