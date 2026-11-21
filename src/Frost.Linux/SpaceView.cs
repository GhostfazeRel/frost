using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Frost.Core;
namespace Frost.Linux;

// "What's using space?": bars for the children of a folder, sorted by size; click drills in, Esc/Back goes up.
public partial class MainWindow
{
    Border? _space;
    internal ListBox? SpaceList { get; private set; }
    internal TextBlock? SpaceTitle { get; private set; }
    CancellationTokenSource? _spaceCts;
    Op? _spaceOp;
    Func<Key, bool>? _spaceKey;

    void CloseSpace()
    {
        _spaceCts?.Cancel(); _spaceCts = null;
        if (_spaceOp != null) { Ops.Children.Remove(_spaceOp.Row); _spaceOp = null; }
        _spaceKey = null;
        if (_space != null) { ListGrid.Children.Remove(_space); _space = null; SpaceList = null; FocusList(); }
    }

    internal void ShowSpace(string? root = null)
    {
        var tab = _cur; if (tab == null) return;
        root ??= tab.Path;
        CloseSpace();
        var path = new Stack<SizeNode>();
        SizeNode? shown = null;
        var title = SpaceTitle = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var sub = new TextBlock { Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var back = new Button { Content = "←", IsEnabled = false }; ToolTip.SetTip(back, "Up one level (Backspace)");
        var open = new Button { Content = "Open folder", IsEnabled = false };
        var close = new Button { Content = "✕" }; ToolTip.SetTip(close, "Close (Esc)");
        Avalonia.Automation.AutomationProperties.SetName(close, "Close space analyzer");
        Avalonia.Automation.AutomationProperties.SetName(back, "Up one level");
        var list = SpaceList = new ListBox { Background = Brushes.Transparent, SelectionMode = SelectionMode.Single };
        list.ItemTemplate = new FuncDataTemplate<SizeNode>((n, _) =>
        {
            if (n == null) return null;
            double pct = shown is { Size: > 0 } s ? Math.Clamp(n.Size / (double)s.Size, 0, 1) : 0;
            var g = new Grid { ColumnDefinitions = new("2*,3*,90,56"), Background = Brushes.Transparent };
            var glyph = Glyphs.For(new FileEntry(n.Name, n.Path, n.IsDirectory, n.Size, default, default, n.IsDirectory ? FileAttributes.Directory : 0));
            g.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Glyph(glyph.Geo, glyph.Fill, 16), new TextBlock { Text = n.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } } });
            var bar = new Grid { Margin = new Thickness(8, 5), [Grid.ColumnProperty] = 1, ColumnDefinitions = new() { new ColumnDefinition(Math.Max(pct, 0.003), GridUnitType.Star), new ColumnDefinition(Math.Max(1 - pct, 0.0001), GridUnitType.Star) } };
            bar.Children.Add(new Border { Background = Dialogs.Res("Accent"), CornerRadius = new CornerRadius(3), Opacity = 0.75 });
            g.Children.Add(bar);
            g.Children.Add(new TextBlock { Text = n.Unreadable ? "can't read" : Format.Size(n.Size), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Dialogs.Res("Text2"), [Grid.ColumnProperty] = 2 });
            g.Children.Add(new TextBlock { Text = pct >= 0.001 ? pct.ToString("P0") : "<1%", TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Dialogs.Res("Text3"), [Grid.ColumnProperty] = 3 });
            return g;
        });
        void Show(SizeNode n)
        {
            shown = n;
            title.Text = n.Path; sub.Text = Format.Size(n.Size) + (n.Children.Count > 0 ? $" in {n.Children.Count} items" : "");
            list.ItemsSource = n.Children;
            back.IsEnabled = path.Count > 0; open.IsEnabled = true;
        }
        void Up() { if (path.Count > 0) Show(path.Pop()); }
        void Drill(SizeNode n) { if (n.IsDirectory && n.Children.Count > 0 && shown != null) { path.Push(shown); Show(n); } }
        back.Click += (_, _) => Up();
        close.Click += (_, _) => CloseSpace();
        open.Click += (_, _) => { var p = shown?.Path; CloseSpace(); if (p != null) _cur?.Navigate(p); };
        list.Tapped += (_, e) => { if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is SizeNode n) Drill(n); };
        _spaceKey = key =>
        {
            if (key == Key.Enter && list.SelectedItem is SizeNode n) Drill(n);
            else if (key == Key.Back) Up();
            else if (key == Key.Escape) { if (path.Count > 0) Up(); else CloseSpace(); }
            else return false;
            return true;
        };
        var top = new DockPanel { Margin = new Thickness(6, 4) };
        DockPanel.SetDock(back, Dock.Left); DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(open, Dock.Right);
        top.Children.Add(back); top.Children.Add(close); top.Children.Add(open);
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0), Children = { title, sub } });
        var wait = new TextBlock { Text = "Scanning… (cancel in the panel below)", Classes = { "dim" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, [Grid.RowProperty] = 1 };
        var inner = new Grid { RowDefinitions = new("Auto,*"), Children = { top, list, wait } };
        list.SetValue(Grid.RowProperty, 1);
        title.Text = root;
        _space = new Border { Background = Dialogs.Res("CardBg"), CornerRadius = new CornerRadius(12), [Grid.RowSpanProperty] = 2, Child = inner };
        ListGrid.Children.Add(_space);

        var cts = _spaceCts = new CancellationTokenSource();
        var op = _spaceOp = AddOp("Scanning " + root);
        op.Cts.Token.Register(() => cts.Cancel());
        var progress = new Progress<OperationProgress>(op.Report);
        _ = Task.Run(async () =>
        {
            try { return await SpaceScan.ScanAsync(root, progress, cts.Token); } catch { return null; }
        }).ContinueWith(r => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_spaceCts != cts) return; // closed or replaced meanwhile
            Ops.Children.Remove(op.Row); _spaceOp = null;
            if (r.Result == null) { Notify(cts.IsCancellationRequested ? "Scan cancelled." : "Couldn't scan this folder."); CloseSpace(); return; }
            inner.Children.Remove(wait);
            Show(r.Result);
            list.Focus();
        }));
    }
}
