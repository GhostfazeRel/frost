using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Frost.Core;
using Frost.Shell;

namespace Frost.App;

public sealed class DupFile : ObservableObject
{
    private bool _checked;
    public DupFile(string path, Action changed)
    {
        Path = path;
        try { Modified = File.GetLastWriteTime(path); } catch { Modified = DateTime.MinValue; }
        ModifiedText = Modified == DateTime.MinValue ? "" : Format.Date(Modified, DateTime.Now);
        _changed = changed;
    }
    private readonly Action _changed;
    public string Path { get; }
    public DateTime Modified { get; }
    public string ModifiedText { get; }
    public bool IsChecked { get => _checked; set { if (Set(ref _checked, value)) _changed(); } }
}

public sealed class DupGroup : ObservableObject
{
    public DupGroup(long size, IEnumerable<DupFile> files)
    {
        Size = size;
        Files = new ObservableCollection<DupFile>(files);
        Files.CollectionChanged += (_, _) => Raise(nameof(Header));
    }
    public long Size { get; }
    public ObservableCollection<DupFile> Files { get; }
    public string Header => $"{Files.Count} identical files · {Format.Size(Size)} each · {Format.Size(Size * (Files.Count - 1))} wasted";
}

/// <summary>Finds identical files under a folder. Ticked copies go to the Recycle Bin (undoable with Ctrl+Z); at least one copy per group always stays.</summary>
public partial class DuplicatesWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<DupGroup> _groups = new();
    private string _folder;
    private CancellationTokenSource? _cts;

    public DuplicatesWindow(Window owner, MainViewModel vm, string folder)
    {
        InitializeComponent();
        Owner = owner;
        _vm = vm; _folder = folder;
        FolderText.Text = folder;
        GroupList.ItemsSource = _groups;
        UpdateSelected();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => Scan_Click(sender, e);
    private void Window_Closed(object? sender, EventArgs e) => _cts?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_Down(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) try { DragMove(); } catch { } }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to scan for duplicates", InitialDirectory = _folder };
        if (dlg.ShowDialog(this) != true) return;
        _folder = dlg.FolderName;
        FolderText.Text = _folder;
        Scan_Click(sender, e);
    }

    private void File_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: DupFile f })
            try { ShellActions.Open(f.Path); } catch (Exception ex) { _vm.Notify("Couldn't open file: " + ex.Message); }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _groups.Clear();
        UpdateSelected();
        ScanButton.Content = "Cancel";
        ScanButton.Click -= Scan_Click; ScanButton.Click += Cancel_Click;
        var op = new OperationViewModel("Looking for duplicates");
        _vm.Operations.Add(op);
        cts.Token.Register(() => op.Cts.Cancel());
        op.Cts.Token.Register(() => cts.Cancel());
        StatusText.Text = "Scanning…";
        var progress = new Progress<OperationProgress>(p => { op.Report(p); if (_cts == cts) StatusText.Text = $"Scanning… {p.CurrentItem}"; });
        IReadOnlyList<DuplicateGroup>? found = null;
        try { found = await Duplicates.FindAsync(_folder, progress, cts.Token); }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; }
        catch (Exception ex) { StatusText.Text = "Scan failed: " + ex.Message; }
        finally
        {
            _vm.Operations.Remove(op);
            if (_cts == cts) { ScanButton.Content = "Scan"; ScanButton.Click -= Cancel_Click; ScanButton.Click += Scan_Click; }
        }
        if (found == null || _cts != cts) return;
        foreach (var g in found) _groups.Add(new DupGroup(g.Size, g.Paths.Select(p => new DupFile(p, UpdateSelected))));
        UpdateStatus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void UpdateStatus()
    {
        long wasted = _groups.Sum(g => g.Size * (g.Files.Count - 1));
        StatusText.Text = _groups.Count == 0 ? "No duplicates found." : $"{_groups.Count:N0} groups of duplicates, {Format.Size(wasted)} wasted. Tick the copies you don't need.";
    }

    private IEnumerable<DupFile> Ticked => _groups.SelectMany(g => g.Files).Where(f => f.IsChecked);

    private void UpdateSelected()
    {
        var n = Ticked.Count();
        SelectedText.Text = n == 0 ? "" : $"{n} ticked";
        MoveButton.IsEnabled = n > 0;
    }

    private void Keep(bool newest)
    {
        foreach (var g in _groups)
        {
            var keep = (newest ? g.Files.OrderByDescending(f => f.Modified) : g.Files.OrderBy(f => f.Modified)).First();
            foreach (var f in g.Files) f.IsChecked = f != keep;
        }
    }

    private void KeepNewest_Click(object sender, RoutedEventArgs e) => Keep(true);
    private void KeepOldest_Click(object sender, RoutedEventArgs e) => Keep(false);
    private void Clear_Click(object sender, RoutedEventArgs e) { foreach (var f in _groups.SelectMany(g => g.Files)) f.IsChecked = false; }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        var paths = Ticked.Select(f => f.Path).ToList();
        if (paths.Count == 0) return;
        if (_groups.Any(g => g.Files.All(f => f.IsChecked)))
        {
            DialogWindow.Show(this, "Keep at least one copy", "Every copy in at least one group is ticked. Untick one copy per group so one stays.", new[] { "OK" }, null, out _);
            return;
        }
        if (DialogWindow.Show(this, "Move to Recycle Bin", $"Move {paths.Count} file(s) to the Recycle Bin?", new[] { "Move", "Cancel" }, null, out _) != 0) return;
        try
        {
            ShellActions.Recycle(paths, this);
            var gone = paths.Where(p => !File.Exists(p)).ToList();
            _vm.History.RecordRecycle(gone);
            var goneSet = new HashSet<string>(gone, StringComparer.OrdinalIgnoreCase);
            foreach (var g in _groups.ToList())
            {
                foreach (var f in g.Files.Where(f => goneSet.Contains(f.Path)).ToList()) g.Files.Remove(f);
                if (g.Files.Count < 2) _groups.Remove(g);
            }
            UpdateSelected(); UpdateStatus();
            _vm.Notify($"Moved {gone.Count} file(s) to the Recycle Bin. Ctrl+Z can undo it.");
            foreach (var t in _vm.AllTabs.ToList()) if (t.Loaded) _ = t.RefreshAsync();
        }
        catch (Exception ex) { _vm.Notify("Couldn't move to the Recycle Bin: " + ex.Message); }
    }
}
