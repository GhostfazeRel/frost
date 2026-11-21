using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Frost.Core;
namespace Frost.Linux;

// "Find biggest files": every drive scanned at once, top 50 per drive plus a combined list, live while scanning.
public partial class MainWindow
{
    internal Window? BiggestWindow { get; private set; }
    internal List<ListBox> BiggestLists { get; } = new(); // [0] = combined, then one per root

    void RevealPath(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir == null || !Directory.Exists(dir)) { Notify("That folder is gone."); return; }
        NewTab(dir);
        _ = _cur!.RefreshAsync(path);
    }

    internal void ShowBiggestFiles(IReadOnlyList<string>? roots = null)
    {
        BiggestWindow?.Close();
        roots ??= Platform.Drives().Select(d => d.Path).ToList();
        const int TopN = 50;
        var removed = new HashSet<string>();
        var latest = new Dictionary<string, DriveScan>();
        var cts = new CancellationTokenSource();
        BiggestLists.Clear();
        ListBox combined = null!;
        var status = new Dictionary<string, TextBlock>();
        var lists = new Dictionary<string, ListBox>();

        var w = BiggestWindow = new Window
        {
            Title = "Biggest files", Width = 940, Height = 720, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Dialogs.Res("CardBg"),
        };
        ListBox NewList() => new() { Background = Brushes.Transparent, SelectionMode = SelectionMode.Single, [ScrollViewer.VerticalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var template = new FuncDataTemplate<BigFile>((f, _) =>
        {
            if (f == null) return null;
            var g = new Grid { ColumnDefinitions = new("2*,3*,96,120"), Background = Brushes.Transparent };
            g.Children.Add(new TextBlock { Text = Path.GetFileName(f.Path), TextTrimming = TextTrimming.CharacterEllipsis });
            g.Children.Add(new TextBlock { Text = Path.GetDirectoryName(f.Path), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Dialogs.Res("Text3"), Margin = new Thickness(8, 0), [Grid.ColumnProperty] = 1 });
            g.Children.Add(new TextBlock { Text = Format.Size(f.Size), TextAlignment = TextAlignment.Right, Foreground = Dialogs.Res("Text2"), [Grid.ColumnProperty] = 2 });
            g.Children.Add(new TextBlock { Text = Format.Date(f.Modified, DateTime.Now), TextAlignment = TextAlignment.Right, Foreground = Dialogs.Res("Text3"), Margin = new Thickness(12, 0, 0, 0), [Grid.ColumnProperty] = 3 });
            return g;
        });
        static BigFile? FileAt(object? src) => (src as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as BigFile;
        ListBox Wire(ListBox l)
        {
            l.ItemTemplate = template;
            l.Tapped += (_, e) => { if (FileAt(e.Source) is { } f) RevealPath(f.Path); };
            l.KeyDown += (_, e) => { if (e.Key == Key.Enter && l.SelectedItem is BigFile f) { RevealPath(f.Path); e.Handled = true; } };
            l.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Right || FileAt(e.Source) is not { } f) return;
                e.Handled = true;
                Flyout(l, new Control[]
                {
                    MI("Open", () => OpenPath(f.Path)),
                    MI("Show in folder", () => RevealPath(f.Path)),
                    MI("Move to trash", async () =>
                    {
                        if (await Task.Run(() => Platform.Trash(new[] { f.Path }))) { removed.Add(f.Path); _history.RecordRecycle(new[] { f.Path }); Render(); foreach (var t in AllTabs.Where(t => t.Loaded)) _ = t.RefreshAsync(); }
                        else Notify("Couldn't move it to the trash. Nothing was deleted.");
                    }),
                }, atPointer: true);
            };
            BiggestLists.Add(l);
            return l;
        }
        Control Card(Control header, ListBox list) => new Border { Classes = { "card" }, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), Child = new StackPanel { Spacing = 6, Children = { header, list } } };

        combined = Wire(NewList());
        var stack = new StackPanel { Margin = new Thickness(14, 10) };
        stack.Children.Add(Card(new TextBlock { Text = "Biggest overall", FontSize = 16, FontWeight = FontWeight.SemiBold }, combined));

        foreach (var root in roots)
        {
            string used = "";
            double pct = 0;
            try { var d = new DriveInfo(root); pct = d.TotalSize > 0 ? 100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize : 0; used = $"{Format.Size(d.TotalSize - d.AvailableFreeSpace)} used of {Format.Size(d.TotalSize)}  ·  {Format.Size(d.AvailableFreeSpace)} free"; } catch { }
            var st = status[root] = new TextBlock { Text = "Waiting…", Classes = { "dim" } };
            var l = lists[root] = Wire(NewList());
            var head = new StackPanel { Spacing = 4, Children =
            {
                new Grid { ColumnDefinitions = new("*,Auto"), Children = { new TextBlock { Text = root, FontSize = 16, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = used, Classes = { "dim" }, [Grid.ColumnProperty] = 1 } } },
                new ProgressBar { Minimum = 0, Maximum = 100, Value = pct },
                st,
            } };
            stack.Children.Add(Card(head, l));
        }

        void Render()
        {
            combined.ItemsSource = BiggestFiles.Combine(latest.Values, TopN + removed.Count).Where(f => !removed.Contains(f.Path)).Take(TopN).ToList();
            foreach (var (root, s) in latest)
            {
                lists[root].ItemsSource = s.Top.Where(f => !removed.Contains(f.Path)).ToList();
                if (!cts.IsCancellationRequested) status[root].Text = (s.Done ? "Done" : "Scanning") + $"  ·  {s.FilesScanned:N0} files" + (s.Unreadable.Count > 0 ? $"  ·  {s.Unreadable.Count} folder(s) couldn't be read" : "");
            }
        }

        var cancel = new Button { Content = "Cancel scan" };
        var close = new Button { Content = "Close" };
        cancel.Click += (_, _) => { cts.Cancel(); cancel.IsEnabled = false; foreach (var t in status.Values) if (t.Text!.StartsWith("Scanning") || t.Text == "Waiting…") t.Text = "Cancelled"; };
        close.Click += (_, _) => w.Close();
        var bar = new DockPanel { Margin = new Thickness(14, 10, 14, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, close } };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(new TextBlock { Text = "Click a file to show it in a tab. Right-click for more.", Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center });
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar); dock.Children.Add(new ScrollViewer { Content = stack });
        w.Content = dock;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.Closed += (_, _) => { cts.Cancel(); if (BiggestWindow == w) BiggestWindow = null; };

        var progress = new Progress<DriveScan>(s => { latest[s.Root] = s; Render(); });
        _ = Task.Run(async () =>
        {
            try { return await BiggestFiles.ScanAsync(roots, TopN, progress, cts.Token); } catch { return null; }
        }).ContinueWith(r => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (r.Result != null) foreach (var s in r.Result) latest[s.Root] = s;
            Render(); cancel.IsEnabled = false;
        }));
        w.Show(this);
    }
}
