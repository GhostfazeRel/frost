using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Frost.Core;
using Frost.Shell;

namespace Frost.App;

public sealed class DriveRowViewModel : ObservableObject
{
    private string _status = "";
    public string Root { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsAll { get; init; }
    public double UsedPercent { get; init; }
    public string FreeText { get; init; } = "";
    public Visibility BarVisibility => IsAll ? Visibility.Collapsed : Visibility.Visible;
    public string Status { get => _status; set => Set(ref _status, value); }
}

public sealed class BigFileViewModel
{
    public BigFileViewModel(BigFile f)
    {
        Path = f.Path; Size = f.Size;
        Name = System.IO.Path.GetFileName(f.Path);
        Folder = System.IO.Path.GetDirectoryName(f.Path) ?? "";
        SizeText = Format.Size(f.Size);
        ModifiedText = Format.Date(f.Modified, DateTime.Now);
    }
    public string Path { get; }
    public long Size { get; }
    public string Name { get; }
    public string Folder { get; }
    public string SizeText { get; }
    public string ModifiedText { get; }
    public ImageSource Icon => ShellIcons.Get(Path, false, false);
}

/// <summary>"Find biggest files": every fixed and removable drive is scanned at once; per drive the top 50 grows live, "All drives" shows the combined top 50.</summary>
public partial class BiggestFilesWindow : Window
{
    private const int TopN = 50;
    private readonly MainViewModel _vm;
    private readonly Action<string> _reveal;
    private readonly Dictionary<string, DriveScan> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _gone = new(StringComparer.OrdinalIgnoreCase); // moved to the Recycle Bin from this window
    private readonly ObservableCollection<DriveRowViewModel> _drives = new();
    private readonly BulkCollection<BigFileViewModel> _shown = new();
    private CancellationTokenSource? _cts;

    public BiggestFilesWindow(Window owner, MainViewModel vm, Action<string> reveal)
    {
        InitializeComponent();
        Owner = owner;
        _vm = vm; _reveal = reveal;
        DriveList.ItemsSource = _drives;
        FileList.ItemsSource = _shown;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => StartScan();
    private void Window_Closed(object? sender, EventArgs e) => _cts?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_Down(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) try { DragMove(); } catch { } }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
    private void Rescan_Click(object sender, RoutedEventArgs e) => StartScan();

    private async void StartScan()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _latest.Clear(); _gone.Clear(); _drives.Clear(); _shown.ReplaceAll(Array.Empty<BigFileViewModel>());
        var all = new DriveRowViewModel { IsAll = true, Title = "All drives", Status = "Biggest overall" };
        _drives.Add(all);
        var roots = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
                var root = d.RootDirectory.FullName;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                _drives.Add(new DriveRowViewModel
                {
                    Root = root, Title = $"{label} ({root.TrimEnd('\\')})",
                    UsedPercent = d.TotalSize > 0 ? 100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize : 0,
                    FreeText = $"{Format.Size(d.AvailableFreeSpace)} free of {Format.Size(d.TotalSize)}", Status = "Waiting…"
                });
                roots.Add(root);
            }
            catch { }
        }
        DriveList.SelectedIndex = 0;
        if (roots.Count == 0) { StatusText.Text = "No drives to scan."; return; }
        StopButton.IsEnabled = true; RescanButton.IsEnabled = false;
        StatusText.Text = $"Scanning {roots.Count} drive(s)…";
        var progress = new Progress<DriveScan>(s => { if (_cts == cts) OnDrive(s); });
        try { await BiggestFiles.ScanAsync(roots, TopN, progress, cts.Token); StatusText.Text = "Done."; }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled. The lists show what was found so far."; }
        catch (Exception ex) { StatusText.Text = "Scan failed: " + ex.Message; }
        if (_cts == cts) { StopButton.IsEnabled = false; RescanButton.IsEnabled = true; }
    }

    private void OnDrive(DriveScan s)
    {
        _latest[s.Root] = s;
        var row = _drives.FirstOrDefault(d => string.Equals(d.Root, s.Root, StringComparison.OrdinalIgnoreCase));
        if (row != null)
        {
            var unread = s.Unreadable.Count > 0 ? $" · {s.Unreadable.Count} folder(s) couldn't be read" : "";
            row.Status = (s.Done ? "Done · " : "Scanning… ") + $"{s.FilesScanned:N0} files" + unread;
        }
        RefreshShown();
    }

    private void DriveList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshShown();

    private void RefreshShown()
    {
        if (DriveList.SelectedItem is not DriveRowViewModel sel) return;
        IEnumerable<BigFile> files = sel.IsAll
            ? BiggestFiles.Combine(_latest.Values, TopN + _gone.Count)
            : _latest.TryGetValue(sel.Root, out var d) ? d.Top : Array.Empty<BigFile>();
        _shown.ReplaceAll(files.Where(f => !_gone.Contains(f.Path)).Take(TopN).Select(f => new BigFileViewModel(f)));
    }

    // ---- row actions ----
    private void FileList_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(FileList, d) is ListViewItem { DataContext: BigFileViewModel f })
            _reveal(f.Path);
    }

    private void FileList_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(FileList, d) is ListViewItem it) it.IsSelected = true;
    }

    private void FileList_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not BigFileViewModel f) return;
        var m = new ContextMenu();
        m.Items.Add(Item("Open", () => { try { ShellActions.Open(f.Path); } catch (Exception ex) { _vm.Notify("Couldn't open file: " + ex.Message); } }));
        m.Items.Add(Item("Show in folder", () => _reveal(f.Path)));
        m.Items.Add(Item("Move to Recycle Bin", () => Recycle(f)));
        m.PlacementTarget = FileList; m.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem Item(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        return mi;
    }

    private void Recycle(BigFileViewModel f)
    {
        try
        {
            ShellActions.Recycle(new[] { f.Path }, this);
            if (File.Exists(f.Path)) return; // cancelled or failed
            _vm.History.RecordRecycle(new[] { f.Path });
            _gone.Add(f.Path);
            RefreshShown();
            _vm.Notify($"Moved \"{f.Name}\" to the Recycle Bin. Ctrl+Z in the main window can undo it.");
        }
        catch (Exception ex) { _vm.Notify("Couldn't move to the Recycle Bin: " + ex.Message); }
    }
}
