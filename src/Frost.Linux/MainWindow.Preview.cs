using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Frost.Core;
namespace Frost.Linux;

// Preview pane: opens by itself for pictures, closes again for anything else unless pinned with Alt+P (_s.PreviewPane).
public partial class MainWindow
{
    bool _autoPane;
    internal bool PaneVisible => _s.PreviewPane || _autoPane;
    const double PaneMin = 220;

    void SetUpPreview()
    {
        ContentGrid.ColumnDefinitions[3].MaxWidth = 900;
        PaneSplitter.DragCompleted += (_, _) => SavePaneWidth();
        ApplyPane();
    }

    void ApplyPane()
    {
        bool v = PaneVisible;
        PreviewPane.IsVisible = v; PaneSplitter.IsVisible = v;
        var col = ContentGrid.ColumnDefinitions[3];
        col.MinWidth = v ? PaneMin : 0;
        col.Width = new GridLength(v ? _s.PreviewWidth : 0);
    }

    void SavePaneWidth() { if (PaneVisible && PreviewPane.Bounds.Width >= PaneMin) _s.PreviewWidth = Math.Round(PreviewPane.Bounds.Width); }

    void TogglePreview()
    {
        SavePaneWidth();
        _s.PreviewPane = !_s.PreviewPane;
        if (!_s.PreviewPane) _autoPane = false;
        ApplyPane();
        if (PaneVisible) UpdatePreview();
    }

    void TogglePreviewOnClick()
    {
        _s.PreviewOnClick = !_s.PreviewOnClick;
        Notify(_s.PreviewOnClick ? "Pictures open the preview when clicked." : "Pictures no longer open the preview by themselves.");
        UpdatePreview();
    }

    static bool IsPicture(Item i) => !i.IsDirectory && ImageInfo.IsImageExtension(i.Entry.Extension);

    void UpdatePreview()
    {
        bool pics = Sel.Count > 0 && Sel.All(IsPicture);
        if (!_s.PreviewOnClick && _autoPane) { _autoPane = false; ApplyPane(); }
        else if (_s.PreviewOnClick && pics && !PaneVisible) { _autoPane = true; ApplyPane(); }
        else if (!pics && _autoPane && !_s.PreviewPane) { SavePaneWidth(); _autoPane = false; ApplyPane(); _previewCts?.Cancel(); }
        if (PaneVisible) { _previewTimer.Stop(); _previewTimer.Start(); }
    }

    void ClearPreview()
    {
        PrevHost.IsVisible = false;
        var old = PrevImage.Source as IDisposable; PrevImage.Source = null; old?.Dispose();
        PrevGlyph.IsVisible = true; PrevText.Text = ""; PrevInfo.Text = ""; PrevBadge.IsVisible = false;
        PrevRows.Children.Clear(); PrevSwatches.Children.Clear(); PrevSwatches.IsVisible = false;
    }

    async Task ShowPreview()
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        var ct = cts.Token;
        ClearPreview();
        var sel = Sel;
        try
        {
            if (sel.Count == 0)
            {
                if (_cur == null) return;
                PrevTitle.Text = _cur.Title; PrevInfo.Text = "Folder\n" + _cur.CountText;
                var g = Glyphs.For(new FileEntry("", _cur.Path, true, 0, default, default, FileAttributes.Directory));
                PrevGlyph.Data = g.Geo; PrevGlyph.Fill = g.Fill;
                return;
            }
            if (sel.All(IsPicture)) { await ShowPicture(sel, ct); return; }
            if (sel.Count > 1)
            {
                PrevTitle.Text = sel.Count + " items selected"; PrevInfo.Text = Format.Size(sel.Where(i => !i.IsDirectory).Sum(i => i.Entry.Size));
                PrevGlyph.Data = sel[0].Glyph; PrevGlyph.Fill = sel[0].Tint;
                return;
            }
            var it = sel[0];
            PrevTitle.Text = it.Name;
            PrevGlyph.Data = it.Glyph; PrevGlyph.Fill = it.Tint;
            string head = BasicInfo(it);
            PrevInfo.Text = head;
            if (it.IsDirectory)
            {
                PrevInfo.Text = head + "Calculating…";
                var m = await FileOperations.MeasureAsync(new[] { it.FullPath }, ct);
                if (!ct.IsCancellationRequested) PrevInfo.Text = head + $"{Format.Size(m.Bytes)} · {m.Files} files, {m.Folders} folders";
                return;
            }
            PrevInfo.Text = head + Format.Size(it.Entry.Size);
            if (Archives.IsZip(it.Name))
            {
                var z = await Task.Run(() => Archives.Describe(it.FullPath, 200), ct);
                if (ct.IsCancellationRequested) return;
                PrevInfo.Text = head + $"{Format.Size(it.Entry.Size)} · {z.Count} entries, {Format.Size(z.Bytes)} uncompressed";
                PrevText.Text = string.Join('\n', z.Names);
                return;
            }
            if (it.Entry.Size < 1_000_000)
            {
                var text = await Task.Run(() => ReadText(it.FullPath));
                if (!ct.IsCancellationRequested && text != null) PrevText.Text = text;
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    static string BasicInfo(Item it) => $"{it.TypeText}\nModified: {it.ModifiedText}\nCreated: {Format.Date(it.Entry.Created, DateTime.Now)}\n";

    async Task ShowPicture(IReadOnlyList<Item> sel, CancellationToken ct)
    {
        var it = sel[0];
        PrevTitle.Text = it.Name;
        PrevGlyph.Data = it.Glyph; PrevGlyph.Fill = it.Tint;
        if (sel.Count > 1) PrevInfo.Text = $"{sel.Count} pictures, total {Format.Size(sel.Sum(i => i.Entry.Size))}";
        var info = await Task.Run(() => ImageInfo.TryRead(it.FullPath), ct);
        if (ct.IsCancellationRequested) return;
        FillRows(it, info);
        if (info?.Exif is { HasLocation: true }) PrevBadge.IsVisible = true;
        if (info == null) { if (sel.Count == 1) PrevInfo.Text = "Can't read this picture."; return; }
        if (!ImageOps.Decodable(info, it.Entry.Size)) { PrevInfo.Text = (sel.Count > 1 ? PrevInfo.Text + "\n" : "") + "Too large to preview."; return; }
        if (!ImageOps.SkiaFormat(info)) { PrevInfo.Text = (sel.Count > 1 ? PrevInfo.Text + "\n" : "") + "No preview for this format."; return; }

        double dips = Math.Max(120, (PreviewPane.Bounds.Width > 0 ? PreviewPane.Bounds.Width : _s.PreviewWidth) - 30);
        int px = (int)(dips * RenderScaling);
        // EXIF 5-8 swap the axes: the decoded width is the displayed height.
        int decodeW = Math.Clamp(info.IsRotated90 ? (int)(px * (double)info.Width / Math.Max(1, info.Height)) : px, 16, Math.Max(16, info.Width));
        var path = it.FullPath;
        var bmp = await Task.Run(() => { try { using var fs = File.OpenRead(path); return Bitmap.DecodeToWidth(fs, decodeW); } catch { return null; } }, ct);
        if (ct.IsCancellationRequested) { bmp?.Dispose(); return; }
        if (bmp != null)
        {
            PrevImage.MaxHeight = 460;
            PrevImage.Source = bmp;
            PrevHost.LayoutTransform = new MatrixTransform(ImageOps.DisplayMatrix(info.Orientation));
            PrevHost.IsVisible = true; PrevGlyph.IsVisible = false;
        }
        var pal = await Task.Run(() => ImageOps.Palette(path, info.Orientation), ct);
        if (ct.IsCancellationRequested) return;
        foreach (var c in pal)
        {
            var hex = ColorPalette.Hex(c);
            var b = new Button { Width = 30, Height = 30, MinHeight = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 6, 0), Tag = hex,
                Background = new SolidColorBrush(Color.FromUInt32(c | 0xFF000000)), BorderBrush = Dialogs.Res("SubtleRim"), BorderThickness = new Thickness(1) };
            ToolTip.SetTip(b, hex + " (click to copy)");
            AutomationProperties.SetName(b, "Copy colour " + hex);
            b.Click += (_, _) => { _ = Clipboard?.SetTextAsync(hex); Notify("Copied " + hex); };
            PrevSwatches.Children.Add(b);
        }
        PrevSwatches.IsVisible = pal.Count > 0;
    }

    void Row(string label, string? value, Control? extra = null)
    {
        if (string.IsNullOrEmpty(value)) return;
        var g = new Grid { ColumnDefinitions = new("84,*") };
        g.Children.Add(new TextBlock { Text = label, Classes = { "dim" } });
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, [Grid.ColumnProperty] = 1 };
        if (extra == null) g.Children.Add(v);
        else g.Children.Add(new StackPanel { Spacing = 4, [Grid.ColumnProperty] = 1, Children = { v, extra } });
        PrevRows.Children.Add(g);
    }

    void FillRows(Item it, ImageInfo? info)
    {
        var now = DateTime.Now;
        if (info != null)
        {
            Row("Format", MediaFormat.FormatName(info.Format));
            Row("Dimensions", $"{info.DisplayWidth} × {info.DisplayHeight}  ·  {MediaFormat.Megapixels(info.DisplayWidth, info.DisplayHeight)}");
        }
        else Row("Type", it.TypeText);
        Row("Size", Format.Size(it.Entry.Size));
        Row("Modified", it.ModifiedText);
        Row("Created", Format.Date(it.Entry.Created, now));
        if (info == null) return;
        if (info.Frames > 1) Row("Frames", info.Frames.ToString());
        if (info.HasAlpha) Row("Transparency", "Has transparency");
        if (info.Exif is not { } x) return;
        Row("Taken", x.Taken?.ToString("d MMM yyyy, HH:mm"));
        var cam = string.Join(' ', new[] { x.Make, x.Model }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        if (x.Make != null && x.Model != null && x.Model.StartsWith(x.Make.Trim(), StringComparison.OrdinalIgnoreCase)) cam = x.Model.Trim();
        Row("Camera", cam);
        Row("Lens", x.Lens);
        var exp = new List<string>();
        if (x.ExposureSeconds is { } e) exp.Add(MediaFormat.Exposure(e));
        if (x.FNumber is { } f) exp.Add(MediaFormat.FNumber(f));
        if (x.Iso is { } iso) exp.Add("ISO " + iso);
        Row("Exposure", string.Join(" · ", exp));
        if (x.FocalLengthMm is { } fl) Row("Focal length", MediaFormat.FocalLength(fl));
        if (x is { Latitude: { } lat, Longitude: { } lon })
        {
            var text = MediaFormat.Location(lat, lon);
            var copy = new Button { Content = "Copy", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2), MinHeight = 24 };
            AutomationProperties.SetName(copy, "Copy location");
            copy.Click += (_, _) => { _ = Clipboard?.SetTextAsync(text); Notify("Copied " + text); };
            Row("Location", text, copy);
        }
    }

    static string? ReadText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Math.Min(65536, fs.Length)];
            var n = fs.Read(buf, 0, buf.Length);
            if (Array.IndexOf(buf, (byte)0, 0, Math.Min(n, 8000)) >= 0) return null; // binary
            return string.Join('\n', System.Text.Encoding.UTF8.GetString(buf, 0, n).Split('\n').Take(200).Select(l => l.TrimEnd('\r')));
        }
        catch { return null; }
    }
}
