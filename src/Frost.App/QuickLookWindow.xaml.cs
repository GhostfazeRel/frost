using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Frost.Core;
using Frost.Shell;

namespace Frost.App;

/// <summary>Space on a file: a big floating preview. Left/Right flip through the folder, Space/Esc close, Enter opens.</summary>
public partial class QuickLookWindow : Window
{
    private readonly IReadOnlyList<ItemViewModel> _items;
    private readonly Action<ItemViewModel> _onOpen, _onClose;
    private int _index;
    private bool _openOnClose;
    private CancellationTokenSource? _cts;

    // picture view state: the Image element is _pw x _ph device-independent units at scale 1
    private double _pw, _ph, _natW, _scale = 1, _tx, _ty;
    private bool _fit = true, _dragging;
    private Point _last;

    public QuickLookWindow(Window owner, IReadOnlyList<ItemViewModel> items, int index, Action<ItemViewModel> onOpen, Action<ItemViewModel> onClose)
    {
        InitializeComponent();
        Owner = owner;
        _items = items; _index = index; _onOpen = onOpen; _onClose = onClose;
        Width = Math.Max(640, owner.ActualWidth * 0.8);
        Height = Math.Max(480, owner.ActualHeight * 0.8);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Loaded += (_, _) => Load();
    }

    private void Header_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) try { DragMove(); } catch { }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _onClose(_items[_index]);
        if (_openOnClose) _onOpen(_items[_index]);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: case Key.Space: Close(); break;
            case Key.Enter: _openOnClose = true; Close(); break;
            case Key.Left: Flip(-1); break;
            case Key.Right: Flip(1); break;
            case Key.F: ToggleFit(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Flip(int dir)
    {
        int n = Math.Clamp(_index + dir, 0, _items.Count - 1);
        if (n == _index) return;
        _index = n;
        Load();
    }

    private async void Load()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        var it = _items[_index];
        TitleText.Text = it.Name;
        CounterText.Text = $"{_index + 1} / {_items.Count}";
        FooterText.Text = $"{it.TypeText} · {(it.IsDirectory ? "" : Format.Size(it.Entry.Size) + " · ")}{it.ModifiedText}";
        PicHost.Visibility = TextView.Visibility = InfoView.Visibility = FitButton.Visibility = Visibility.Collapsed;
        BusyText.Visibility = Visibility.Visible;
        try
        {
            if (it.IsDirectory) { await ShowInfoAsync(it, "Calculating…", ct); var m = await FileOperations.MeasureAsync(new[] { it.FullPath }, ct); if (!ct.IsCancellationRequested) InfoText.Text = InfoLines(it, $"{m.Files} files, {m.Folders} folders · {Format.Size(m.Bytes)}"); return; }
            if (PictureLoader.IsPicture(it) && await ShowPictureAsync(it, ct)) return;
            if (ct.IsCancellationRequested) return;
            if (Archives.IsZip(it.Name))
            {
                var z = await Task.Run(() => Archives.Describe(it.FullPath, 500), ct);
                if (ct.IsCancellationRequested) return;
                ShowText($"{z.Count} entries, {Format.Size(z.Bytes)} uncompressed\n\n" + string.Join('\n', z.Names));
                return;
            }
            if (PreviewViewModel.TextExt.Contains(it.Entry.Extension))
            {
                var text = await Task.Run(() => ReadText(it.FullPath), ct);
                if (ct.IsCancellationRequested) return;
                if (text != null) { ShowText(text); return; }
            }
            await ShowInfoAsync(it, InfoLines(it, null), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) { InfoIcon.Source = null; InfoText.Text = "Can't preview this file.\n" + ex.Message; BusyText.Visibility = Visibility.Collapsed; InfoView.Visibility = Visibility.Visible; } }
    }

    private static string InfoLines(ItemViewModel it, string? extra)
    {
        var sb = new StringBuilder(it.TypeText);
        if (extra != null) sb.Append('\n').Append(extra);
        else if (!it.IsDirectory) sb.Append('\n').Append(Format.Size(it.Entry.Size));
        sb.Append("\nModified ").Append(it.ModifiedText).Append("\nCreated ").Append(Format.Date(it.Entry.Created, DateTime.Now));
        sb.Append('\n').Append(it.FullPath);
        return sb.ToString();
    }

    private async Task ShowInfoAsync(ItemViewModel it, string text, CancellationToken ct)
    {
        InfoIcon.Source = ShellIcons.Get(it.FullPath, it.IsDirectory, true);
        InfoText.Text = text == "Calculating…" ? InfoLines(it, text) : text;
        BusyText.Visibility = Visibility.Collapsed;
        InfoView.Visibility = Visibility.Visible;
        if (it.IsDirectory) return;
        var th = await ShellIcons.ThumbnailAsync(it.FullPath, 256, ct);
        if (th != null && !ct.IsCancellationRequested) InfoIcon.Source = th;
    }

    private void ShowText(string text)
    {
        TextView.Text = text;
        TextView.ScrollToHome();
        BusyText.Visibility = Visibility.Collapsed;
        TextView.Visibility = Visibility.Visible;
    }

    private static string? ReadText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[Math.Min(1_000_000, fs.Length)];
            int n = 0;
            while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
            if (Array.IndexOf(buf, (byte)0, 0, Math.Min(n, 8000)) >= 0) return null; // binary
            return Encoding.UTF8.GetString(buf, 0, n);
        }
        catch { return null; }
    }

    // ---- pictures: zoom with the wheel, drag to pan, F / button toggles fit and 100% ----
    private async Task<bool> ShowPictureAsync(ItemViewModel it, CancellationToken ct)
    {
        var r = await Task.Run<(ImageInfo? info, BitmapSource img)?>(() =>
        {
            var info = ImageInfo.TryRead(it.FullPath);
            if (PictureLoader.TooBig(it.Entry.Size, info)) return null;
            try { return (info, img: PictureLoader.Decode(it.FullPath, info, 4096, ct)); }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }, ct);
        if (ct.IsCancellationRequested) return true;
        if (r == null) return false; // no codec or too big: caller shows the info view with the shell thumbnail
        _pw = r.Value.img.PixelWidth; _ph = r.Value.img.PixelHeight;
        _natW = r.Value.info?.DisplayWidth ?? _pw;
        Pic.Source = r.Value.img; Pic.Width = _pw; Pic.Height = _ph;
        BusyText.Visibility = Visibility.Collapsed;
        PicHost.Visibility = FitButton.Visibility = Visibility.Visible;
        if (r.Value.info != null) FooterText.Text += $" · {r.Value.info.DisplayWidth} × {r.Value.info.DisplayHeight}";
        PicHost.UpdateLayout();
        SetFit();
        return true;
    }

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;
    private double OneToOne => _natW / _pw / Dpi; // scale at which one picture pixel is one screen pixel
    private double FitScale => PicHost.ActualWidth <= 0 || _pw <= 0 ? 1 : Math.Min(Math.Min(PicHost.ActualWidth / _pw, PicHost.ActualHeight / _ph), Math.Max(OneToOne, 1.0 / Dpi));

    private void SetFit() { _fit = true; _scale = FitScale; _tx = _ty = 0; ApplyTransform(); }
    private void ApplyTransform()
    {
        ScaleT.ScaleX = ScaleT.ScaleY = _scale; MoveT.X = _tx; MoveT.Y = _ty;
        FitButton.Content = _fit ? "100%" : "Fit";
    }

    private void ToggleFit()
    {
        if (PicHost.Visibility != Visibility.Visible) return;
        if (_fit) { _fit = false; _scale = OneToOne; _tx = _ty = 0; ApplyTransform(); } else SetFit();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => ToggleFit();
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { if (_fit && PicHost.Visibility == Visibility.Visible) SetFit(); }

    private void Pic_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double k = e.Delta > 0 ? 1.15 : 1 / 1.15;
        double min = FitScale / 2, max = Math.Max(OneToOne, FitScale) * 16;
        double ns = Math.Clamp(_scale * k, min, max);
        k = ns / _scale;
        var m = e.GetPosition(PicHost);
        double mx = m.X - PicHost.ActualWidth / 2, my = m.Y - PicHost.ActualHeight / 2;
        _tx = mx - (mx - _tx) * k; _ty = my - (my - _ty) * k;
        _scale = ns; _fit = false;
        ApplyTransform();
        e.Handled = true;
    }

    private void Pic_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleFit(); return; }
        _dragging = true; _last = e.GetPosition(PicHost); PicHost.CaptureMouse();
    }

    private void Pic_Up(object sender, MouseButtonEventArgs e) { _dragging = false; PicHost.ReleaseMouseCapture(); }

    private void Pic_Move(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(PicHost);
        _tx += p.X - _last.X; _ty += p.Y - _last.Y; _last = p;
        _fit = false;
        ApplyTransform();
    }
}
