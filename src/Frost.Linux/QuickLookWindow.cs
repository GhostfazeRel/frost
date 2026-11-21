using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Frost.Core;
namespace Frost.Linux;

/// <summary>Picture viewer inside Quick Look: wheel zooms, drag pans, double-click or Z toggles fit / 100%.</summary>
sealed class ZoomView : Border
{
    readonly Control _content;
    readonly int _w, _h; // displayed size in image pixels
    double _z = 1; Point _pan, _last; bool _drag;

    public ZoomView(Control content, int displayW, int displayH)
    {
        (_content, _w, _h) = (content, displayW, displayH);
        ClipToBounds = true; Background = Brushes.Transparent; Cursor = new Cursor(StandardCursorType.Hand);
        Child = content;
        PointerWheelChanged += (_, e) => { Zoom(e.Delta.Y > 0 ? 1.15 : 1 / 1.15); e.Handled = true; };
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _drag = true; _last = e.GetPosition(this); e.Pointer.Capture(this); } };
        PointerMoved += (_, e) => { if (!_drag) return; var p = e.GetPosition(this); _pan += p - _last; _last = p; Apply(); };
        PointerReleased += (_, e) => { _drag = false; e.Pointer.Capture(null); };
        DoubleTapped += (_, _) => Toggle();
    }

    internal double ZoomLevel => _z;
    void Zoom(double f) { _z = Math.Clamp(_z * f, 0.1, 40); Apply(); }
    void Apply() => _content.RenderTransform = new MatrixTransform(Matrix.CreateScale(_z, _z) * Matrix.CreateTranslation(_pan.X, _pan.Y));

    public void Toggle()
    {
        if (Math.Abs(_z - 1) > 0.01 || _pan != default) { _z = 1; _pan = default; }
        else
        {
            var fit = Math.Min(Bounds.Width / _w, Bounds.Height / _h); // dips per image pixel when fitted
            _z = fit > 0 ? 1 / (fit * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1)) : 1;
        }
        Apply();
    }
}

/// <summary>Space: a big floating glass preview of the selected item; left/right flip through the folder.</summary>
sealed class QuickLookWindow : Window
{
    readonly IReadOnlyList<Item> _items;
    readonly Action<Item> _follow, _open;
    int _i;
    CancellationTokenSource? _cts;
    readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _info = new() { Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Border _host = new() { Margin = new Thickness(14, 8, 14, 14) };
    internal Item Current => _items[_i];
    internal Control? Body => _host.Child as Control;

    public QuickLookWindow(Window owner, IReadOnlyList<Item> items, int index, Action<Item> follow, Action<Item> open)
    {
        (_items, _i, _follow, _open) = (items, index, follow, open);
        Width = Math.Max(480, owner.Bounds.Width * 0.72); Height = Math.Max(360, owner.Bounds.Height * 0.8);
        WindowDecorations = WindowDecorations.BorderOnly; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; Background = Dialogs.Res("MenuBg");
        var hint = new TextBlock { Text = "← →  browse    Z  zoom    Enter  open    Space / Esc  close", Classes = { "dim" }, FontSize = 11, Margin = new Thickness(14, 0, 14, 8), HorizontalAlignment = HorizontalAlignment.Center };
        var head = new StackPanel { Margin = new Thickness(16, 12, 16, 0), Children = { _title, _info } };
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(hint, Dock.Bottom);
        dock.Children.Add(head); dock.Children.Add(hint); dock.Children.Add(_host);
        Content = new Border { BorderBrush = Dialogs.Res("SubtleRim"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Child = dock };
        KeyDown += OnKey;
        Deactivated += (_, _) => Close();
        Closed += (_, _) => _cts?.Cancel();
        Opened += (_, _) => { Focus(); _ = Load(); };
    }

    void OnKey(object? s, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key is Key.Space or Key.Escape) Close();
        else if (e.Key == Key.Enter) { var it = Current; Close(); _open(it); }
        else if (e.Key is Key.Left or Key.Up) Step(-1);
        else if (e.Key is Key.Right or Key.Down) Step(1);
        else if (e.Key is Key.Z or Key.D1 or Key.NumPad1) (Body as ZoomView)?.Toggle();
        else e.Handled = false;
    }

    internal void Step(int d)
    {
        var n = _i + d;
        if (n < 0 || n >= _items.Count) return;
        _i = n; _follow(_items[n]); _ = Load();
    }

    void Show(Control c) { var old = ((_host.Child as ZoomView)?.Child as LayoutTransformControl)?.Child as Image; _host.Child = c; (old?.Source as IDisposable)?.Dispose(); }

    Control IconFor(Item it, string? text = null)
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 12 };
        p.Children.Add(new Avalonia.Controls.Shapes.Path { Data = it.Glyph, Fill = it.Tint, Width = 120, Height = 120, Stretch = Stretch.Uniform });
        if (text != null) p.Children.Add(new TextBlock { Text = text, TextAlignment = TextAlignment.Center, Foreground = Dialogs.Res("Text2") });
        return p;
    }

    internal async Task Load()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource(); var ct = cts.Token;
        var it = _items[_i];
        _title.Text = it.Name;
        _info.Text = $"{_i + 1} of {_items.Count}  ·  {it.TypeText}" + (it.IsDirectory ? "" : "  ·  " + it.SizeText) + "  ·  " + it.ModifiedText;
        Show(IconFor(it));
        try
        {
            if (it.IsDirectory)
            {
                Show(IconFor(it, "Calculating…"));
                var m = await FileOperations.MeasureAsync(new[] { it.FullPath }, ct);
                if (!ct.IsCancellationRequested) Show(IconFor(it, $"{m.Files} files, {m.Folders} folders\n{Format.Size(m.Bytes)}"));
            }
            else if (ImageInfo.IsImageExtension(it.Entry.Extension)) await LoadPicture(it, ct);
            else if (Archives.IsZip(it.Name))
            {
                var z = await Task.Run(() => Archives.Describe(it.FullPath, 2000), ct);
                if (!ct.IsCancellationRequested) Show(Lines(z.Names, $"{z.Count} entries, {Format.Size(z.Bytes)} uncompressed"));
            }
            else
            {
                var text = await Task.Run(() => ReadText(it.FullPath), ct);
                if (ct.IsCancellationRequested) return;
                if (text != null) Show(Lines(text, null));
                else Show(IconFor(it, "No preview for this kind of file."));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Show(IconFor(it, "Can't preview: " + ex.Message)); }
    }

    async Task LoadPicture(Item it, CancellationToken ct)
    {
        var info = await Task.Run(() => ImageInfo.TryRead(it.FullPath), ct);
        if (ct.IsCancellationRequested) return;
        if (info == null) { Show(IconFor(it, "Can't read this picture.")); return; }
        _info.Text += $"  ·  {info.DisplayWidth} × {info.DisplayHeight}";
        if (!ImageOps.Decodable(info, it.Entry.Size)) { Show(IconFor(it, "Too large to preview.")); return; }
        if (!ImageOps.SkiaFormat(info)) { Show(IconFor(it, "No preview for this format.")); return; }
        var w = Math.Min(info.Width, 4096);
        var path = it.FullPath;
        var bmp = await Task.Run(() => { try { using var fs = File.OpenRead(path); return Bitmap.DecodeToWidth(fs, w); } catch { return null; } }, ct);
        if (ct.IsCancellationRequested) { bmp?.Dispose(); return; }
        if (bmp == null) { Show(IconFor(it, "Can't read this picture.")); return; }
        var img = new Image { Source = bmp, Stretch = Stretch.Uniform };
        var tr = new LayoutTransformControl { Child = img, LayoutTransform = new MatrixTransform(ImageOps.DisplayMatrix(info.Orientation)) };
        Show(new ZoomView(tr, info.DisplayWidth, info.DisplayHeight));
    }

    static Control Lines(IReadOnlyList<string> names, string? note) => Lines(names.ToList(), note);

    static Control Lines(string text, string? note) => Lines(text.Split('\n').Select(l => l.TrimEnd('\r')).ToList(), note);

    static Control Lines(List<string> lines, string? note)
    {
        var list = new ListBox { ItemsSource = lines, Background = Brushes.Transparent, SelectionMode = SelectionMode.Single };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(PaddingProperty, new Thickness(8, 0)), new Setter(MarginProperty, new Thickness(0)), new Setter(MinHeightProperty, 0.0) } });
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((s, _) => new TextBlock { Text = s, FontFamily = new FontFamily("monospace"), FontSize = 13 });
        var dock = new DockPanel();
        if (note != null) { var n = new TextBlock { Text = note, Classes = { "dim" }, Margin = new Thickness(0, 0, 0, 6) }; DockPanel.SetDock(n, Dock.Top); dock.Children.Add(n); }
        dock.Children.Add(list);
        return dock;
    }

    /// <summary>First 1 MB as text; null for binary files.</summary>
    static string? ReadText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Math.Min(1_048_576, fs.Length)];
            int n = 0, r;
            while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
            if (Array.IndexOf(buf, (byte)0, 0, Math.Min(n, 8000)) >= 0) return null;
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
        }
        catch { return null; }
    }
}

public partial class MainWindow
{
    QuickLookWindow? _quick;
    internal QuickLookWindow? QuickLook => _quick;

    void ShowQuickLook()
    {
        if (_quick != null) { _quick.Close(); return; }
        var (tab, list) = Side2Active ? (_tab2!, List2) : (_cur!, List);
        if (tab == null || list.SelectedItem is not Item it) return;
        var items = tab.Items;
        int i = items.IndexOf(it);
        if (i < 0) return;
        var q = _quick = new QuickLookWindow(this, items, i, x => { list.SelectedItems!.Clear(); list.SelectedItems.Add(x); list.ScrollIntoView(x); }, x => Open(x));
        q.Closed += (_, _) => { _quick = null; FocusList(); };
        q.Show(this);
    }
}
