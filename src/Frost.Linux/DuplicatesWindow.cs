using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Frost.Core;
namespace Frost.Linux;

// "Find duplicate files": pick a folder, groups of identical files with checkboxes, keep newest/oldest, trash the checked ones.
public partial class MainWindow
{
    internal Window? DuplicatesWindow { get; private set; }
    internal List<CheckBox> DuplicateChecks { get; } = new();

    internal void ShowDuplicates(string? folder = null)
    {
        DuplicatesWindow?.Close();
        var w = DuplicatesWindow = new Window { Title = "Duplicate files", Width = 900, Height = 700, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Dialogs.Res("CardBg") };
        var box = new TextBox { Text = folder ?? _cur?.Path ?? Platform.Home };
        var browse = new Button { Content = "Browse…" };
        var scan = new Button { Content = "Scan", Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsEnabled = false };
        var status = new TextBlock { Classes = { "dim" }, TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { IsIndeterminate = false, Minimum = 0, Maximum = 100, IsVisible = false };
        var groupsHost = new StackPanel { Spacing = 8 };
        var keepNew = new Button { Content = "Keep newest" }; var keepOld = new Button { Content = "Keep oldest" };
        var none = new Button { Content = "Select none" };
        var trash = new Button { Content = "Move selected to trash", IsEnabled = false };
        IReadOnlyList<DuplicateGroup> groups = Array.Empty<DuplicateGroup>();
        var rows = new List<(CheckBox Box, string Path, int Group)>();
        CancellationTokenSource? cts = null;

        void Count()
        {
            var picked = rows.Where(r => r.Box.IsChecked == true).ToList();
            trash.IsEnabled = picked.Count > 0;
            trash.Content = picked.Count > 0 ? $"Move selected to trash ({picked.Count}, {Format.Size(picked.Sum(r => new FileInfo(r.Path).Exists ? new FileInfo(r.Path).Length : 0))})" : "Move selected to trash";
        }
        bool Guard(int g) { var all = rows.Where(r => r.Group == g).ToList(); return all.All(r => r.Box.IsChecked == true); } // true = every copy is ticked
        void Build()
        {
            groupsHost.Children.Clear(); rows.Clear(); DuplicateChecks.Clear();
            for (int g = 0; g < groups.Count; g++)
            {
                int gi = g;
                var grp = groups[g];
                var card = new StackPanel { Spacing = 2 };
                card.Children.Add(new TextBlock { Text = $"{grp.Paths.Count} copies · {Format.Size(grp.Size)} each · {Format.Size(grp.Size * (grp.Paths.Count - 1))} wasted", FontWeight = FontWeight.SemiBold });
                foreach (var p in grp.Paths)
                {
                    var cb = new CheckBox { Content = new TextBlock { Text = p + "   " + Format.Date(File.GetLastWriteTime(p), DateTime.Now), TextTrimming = TextTrimming.CharacterEllipsis } };
                    cb.IsCheckedChanged += (_, _) =>
                    {
                        if (cb.IsChecked == true && Guard(gi)) { cb.IsChecked = false; status.Text = "At least one copy of each file must stay."; return; }
                        Count();
                    };
                    rows.Add((cb, p, gi)); DuplicateChecks.Add(cb);
                    card.Children.Add(cb);
                }
                groupsHost.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(12, 8), Child = card });
            }
            Count();
        }
        // Tick every copy except the one to keep (newest or oldest by modified time).
        void Keep(bool newest)
        {
            foreach (var r in rows) r.Box.IsChecked = false;
            for (int g = 0; g < groups.Count; g++)
            {
                var ps = groups[g].Paths.OrderBy(File.GetLastWriteTimeUtc).ToList();
                var keep = newest ? ps[^1] : ps[0];
                foreach (var r in rows.Where(r => r.Group == g && r.Path != keep)) r.Box.IsChecked = true;
            }
        }
        keepNew.Click += (_, _) => Keep(true);
        keepOld.Click += (_, _) => Keep(false);
        none.Click += (_, _) => { foreach (var r in rows) r.Box.IsChecked = false; };
        browse.Click += async (_, _) =>
        {
            var r = await w.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder to scan" });
            if (r.Count > 0 && r[0].TryGetLocalPath() is { } p) box.Text = p;
        };
        cancel.Click += (_, _) => cts?.Cancel();
        trash.Click += async (_, _) =>
        {
            var picked = rows.Where(r => r.Box.IsChecked == true).Select(r => r.Path).ToList();
            if (picked.Count == 0) return;
            trash.IsEnabled = false;
            if (!await Task.Run(() => Platform.Trash(picked))) { status.Text = "Couldn't move them to the trash. Nothing was deleted."; Count(); return; }
            var gone = picked.Where(p => !File.Exists(p)).ToList();
            _history.RecordRecycle(gone);
            status.Text = $"Moved {gone.Count} file(s) to the trash. Ctrl+Z in the main window undoes it.";
            groups = groups.Select(g => new DuplicateGroup(g.Size, g.Paths.Where(p => !gone.Contains(p)).ToList())).Where(g => g.Paths.Count > 1).ToList();
            Build();
            foreach (var t in AllTabs.Where(t => t.Loaded)) _ = t.RefreshAsync();
        };
        async void Scan()
        {
            var root = (box.Text ?? "").Trim();
            if (!Directory.Exists(root)) { status.Text = "That folder doesn't exist."; return; }
            cts?.Cancel();
            var c = cts = new CancellationTokenSource();
            scan.IsEnabled = false; cancel.IsEnabled = true; bar.IsVisible = true; bar.Value = 0; status.Text = "Scanning…";
            groups = Array.Empty<DuplicateGroup>(); Build();
            var progress = new Progress<OperationProgress>(p =>
            {
                status.Text = "Scanning… " + p.CurrentItem;
                bar.Value = p.ItemsTotal > 0 ? 100.0 * p.ItemsDone / p.ItemsTotal : 0;
            });
            try
            {
                groups = await Task.Run(() => Duplicates.FindAsync(root, progress, c.Token));
                status.Text = groups.Count == 0 ? "No duplicates found." : $"{groups.Count} group(s) of identical files, {Format.Size(groups.Sum(g => g.Size * (g.Paths.Count - 1)))} could be freed.";
                Build();
            }
            catch (OperationCanceledException) { status.Text = "Cancelled."; }
            catch (Exception ex) { status.Text = "Scan failed: " + ex.Message; }
            if (cts == c) { scan.IsEnabled = true; cancel.IsEnabled = false; bar.IsVisible = false; }
        }
        scan.Click += (_, _) => Scan();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Scan(); e.Handled = true; } };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.Closed += (_, _) => { cts?.Cancel(); if (DuplicatesWindow == w) DuplicatesWindow = null; };

        var top = new DockPanel { Margin = new Thickness(14, 12, 14, 6) };
        var tb = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { browse, scan, cancel } };
        DockPanel.SetDock(tb, Dock.Right);
        top.Children.Add(tb); top.Children.Add(box);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 6, 14, 12), Children = { keepNew, keepOld, none, trash } };
        var mid = new StackPanel { Margin = new Thickness(14, 0), Spacing = 4, Children = { bar, status } };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(mid, Dock.Top); DockPanel.SetDock(actions, Dock.Bottom);
        dock.Children.Add(top); dock.Children.Add(mid); dock.Children.Add(actions);
        dock.Children.Add(new ScrollViewer { Content = new Border { Padding = new Thickness(14, 6), Child = groupsHost } });
        w.Content = dock;
        w.Opened += (_, _) => scan.Focus();
        w.Show(this);
    }
}
