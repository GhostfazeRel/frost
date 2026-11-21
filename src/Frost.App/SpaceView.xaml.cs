using System.Windows;
using System.Windows.Controls;
using Frost.Core;

namespace Frost.App;

public sealed record SpaceRow(SizeNode Node, string Name, string Path, string Glyph, string SizeText, string PercentText, double BarWidth);

/// <summary>"What's using space?": bars for every item of a folder, biggest first. Click a folder to drill in, Back to go up, click a file to show it in the file list.</summary>
public partial class SpaceView : UserControl
{
    private const double BarMax = 194;
    private readonly Stack<SizeNode> _back = new();
    private MainViewModel? _vm;
    private SizeNode? _node;
    private OperationViewModel? _op;

    public SpaceView() { InitializeComponent(); }

    public event Action? CloseRequested;
    public event Action<string>? RevealRequested;

    public async void Start(MainViewModel vm, string folder)
    {
        _vm = vm;
        _back.Clear();
        _node = null;
        Rows.ItemsSource = null;
        await ScanAsync(folder);
    }

    /// <summary>Cancels a running scan (view closed).</summary>
    public void Stop() => _op?.Cts.Cancel();

    private async Task ScanAsync(string folder)
    {
        if (_vm == null) return;
        Stop();
        var name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : folder;
        var op = _op = new OperationViewModel("Scanning " + name);
        _vm.Operations.Add(op);
        TitleText.Text = folder;
        StatusText.Text = "Scanning…";
        StopButton.Visibility = Visibility.Visible;
        BackButton.IsEnabled = _back.Count > 0;
        var progress = new Progress<OperationProgress>(p => { op.Report(p); if (_op == op) StatusText.Text = $"Scanning… {p.ItemsDone:N0} items · {p.CurrentItem}"; });
        SizeNode? root = null;
        try { root = await SpaceScan.ScanAsync(folder, progress, op.Cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _vm.Notify("Scan failed: " + ex.Message); }
        finally { _vm.Operations.Remove(op); if (_op == op) { _op = null; StopButton.Visibility = Visibility.Collapsed; } }
        if (root != null) Show(root);
        else { StatusText.Text = "Scan cancelled."; BackButton.IsEnabled = _back.Count > 0; if (_node != null) TitleText.Text = _node.Path; }
    }

    private void Show(SizeNode node)
    {
        _node = node;
        TitleText.Text = node.Path;
        BackButton.IsEnabled = _back.Count > 0;
        double total = Math.Max(1, node.Size);
        Rows.ItemsSource = node.Children.Select(c => new SpaceRow(c, c.Name, c.Path, c.IsDirectory ? "" : "",
            c.Unreadable ? "can't read" : Format.Size(c.Size), c.Size <= 0 ? "" : (c.Size * 100.0 / total).ToString(c.Size * 100.0 / total < 1 ? "0.#" : "0") + "%",
            Math.Max(c.Size > 0 ? 2 : 0, BarMax * c.Size / total))).ToList();
        StatusText.Text = node.Children.Count == 0 ? $"{Format.Size(node.Size)} · nothing inside" : $"{Format.Size(node.Size)} in {node.Children.Count:N0} items" + (node.Unreadable ? " · some folders couldn't be read" : "");
    }

    private async void Rows_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || ItemsControl.ContainerFromElement(Rows, d) is not ListBoxItem { DataContext: SpaceRow row }) return;
        if (!row.Node.IsDirectory) { RevealRequested?.Invoke(row.Path); return; }
        if (row.Node.Unreadable) return;
        if (_node != null) _back.Push(_node);
        // children not part of the scan result (or an empty tree for a non-empty folder): scan this folder on its own
        if (row.Node.Children.Count > 0 || row.Node.Size == 0) Show(row.Node);
        else
        {
            await ScanAsync(row.Path);
            if (_node == null || !Paths.Same(_node.Path, row.Path)) { if (_back.Count > 0) _back.Pop(); }
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_back.Count > 0) Show(_back.Pop());
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
