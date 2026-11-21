using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using Frost.Core;
using Frost.Shell;

namespace Frost.App;

public sealed record InfoRow(string Label, string Value);
public sealed record Swatch(string Hex, Brush Brush);

public sealed class PreviewViewModel : ObservableObject
{
    internal static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".xml", ".csv", ".cs", ".cpp", ".c", ".h", ".hpp", ".py", ".js", ".ts", ".tsx", ".jsx",
        ".html", ".htm", ".css", ".xaml", ".ini", ".cfg", ".conf", ".yml", ".yaml", ".toml", ".sh", ".bat", ".cmd", ".ps1",
        ".java", ".kt", ".rs", ".go", ".sql", ".csproj", ".sln", ".props", ".targets", ".gitignore", ".editorconfig", ".reg", ".lua", ".php", ".rb"
    };

    private CancellationTokenSource? _cts;
    private string _title = "", _type = "", _size = "", _modified = "", _created = "", _text = "", _extra = "", _location = "";
    private double _maxHeight = 200;
    private ImageSource? _picture;

    public PreviewViewModel()
    {
        CopyLocationCommand = new RelayCommand(_ => CopyText(_location, "Location copied"));
        CopyHexCommand = new RelayCommand(o => { if (o is string hex) CopyText(hex, "Copied " + hex); });
    }

    /// <summary>Set by MainViewModel: shows a toast.</summary>
    public Action<string>? Notify { get; set; }
    /// <summary>Pane width in device pixels; the picture is decoded to this width.</summary>
    public int DecodeWidth { get; set; } = 400;

    public ObservableCollection<InfoRow> Rows { get; } = new();
    public ObservableCollection<Swatch> Swatches { get; } = new();
    public ICommand CopyLocationCommand { get; }
    public ICommand CopyHexCommand { get; }

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string TypeText { get => _type; private set => Set(ref _type, value); }
    public string SizeText { get => _size; private set => Set(ref _size, value); }
    public string ModifiedText { get => _modified; private set => Set(ref _modified, value); }
    public string CreatedText { get => _created; private set => Set(ref _created, value); }
    public string TextContent { get => _text; private set { if (Set(ref _text, value)) Raise(nameof(HasText)); } }
    public bool HasText => _text.Length > 0;
    /// <summary>"3 pictures, 12.4 MB" when several pictures are selected.</summary>
    public string Extra { get => _extra; private set { if (Set(ref _extra, value)) Raise(nameof(HasExtra)); } }
    public bool HasExtra => _extra.Length > 0;
    public string LocationText { get => _location; private set { if (Set(ref _location, value)) Raise(nameof(HasLocation)); } }
    public bool HasLocation => _location.Length > 0;
    public bool HasSwatches => Swatches.Count > 0;
    public bool HasRows => Rows.Count > 0;
    public double PictureMaxHeight { get => _maxHeight; private set => Set(ref _maxHeight, value); }
    public ImageSource? Picture { get => _picture; private set => Set(ref _picture, value); }

    private void CopyText(string text, string toast)
    {
        if (text.Length == 0) return;
        try { System.Windows.Clipboard.SetText(text); Notify?.Invoke(toast); }
        catch { Notify?.Invoke("Couldn't copy to the clipboard."); }
    }

    private void Reset()
    {
        TextContent = ""; ModifiedText = ""; CreatedText = ""; TypeText = ""; SizeText = ""; Extra = ""; LocationText = "";
        Rows.Clear(); Swatches.Clear(); PictureMaxHeight = 200;
        Raise(nameof(HasRows)); Raise(nameof(HasSwatches));
    }

    public async void Show(IReadOnlyList<ItemViewModel> sel, TabViewModel? tab)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            Reset();
            if (sel.Count == 0)
            {
                if (tab == null) return;
                Title = tab.Title; TypeText = "Folder"; SizeText = tab.CountText;
                Picture = ShellIcons.Get(tab.Path, true, true);
                return;
            }
            bool pictures = sel.All(PictureLoader.IsPicture);
            if (sel.Count > 1 && !pictures)
            {
                Title = sel.Count + " items selected";
                SizeText = Format.Size(sel.Where(i => !i.IsDirectory).Sum(i => i.Entry.Size));
                Picture = sel[0].Picture;
                return;
            }
            var it = sel[0];
            Title = it.Name; TypeText = it.TypeText; ModifiedText = it.ModifiedText;
            CreatedText = Format.Date(it.Entry.Created, DateTime.Now);
            Picture = ShellIcons.Get(it.FullPath, it.IsDirectory, true);
            if (sel.Count > 1) Extra = $"{sel.Count} pictures, {Format.Size(sel.Sum(i => i.Entry.Size))}";
            if (pictures) { SizeText = Format.Size(it.Entry.Size); await ShowPictureAsync(it, ct); return; }

            var thumbTask = it.IsDirectory ? Task.FromResult<ImageSource?>(null) : ShellIcons.ThumbnailAsync(it.FullPath, 320, ct);
            if (it.IsDirectory)
            {
                SizeText = "Calculating…";
                var m = await FileOperations.MeasureAsync(new[] { it.FullPath }, ct);
                if (ct.IsCancellationRequested) return;
                SizeText = $"{Format.Size(m.Bytes)} · {m.Files} files, {m.Folders} folders";
                return;
            }
            SizeText = Format.Size(it.Entry.Size);
            var th = await thumbTask;
            if (ct.IsCancellationRequested) return;
            if (th != null) Picture = th;
            if (Archives.IsZip(it.Name))
            {
                var z = await Task.Run(() => Archives.Describe(it.FullPath, 200), ct);
                if (ct.IsCancellationRequested) return;
                SizeText = $"{Format.Size(it.Entry.Size)} · {z.Count} entries, {Format.Size(z.Bytes)} uncompressed";
                TextContent = string.Join('\n', z.Names);
                return;
            }
            if (TextExt.Contains(it.Entry.Extension) && it.Entry.Size < 1_000_000)
            {
                var text = await Task.Run(() => ReadText(it.FullPath));
                if (!ct.IsCancellationRequested && text != null) TextContent = text;
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private sealed record Loaded(ImageInfo? Info, System.Windows.Media.Imaging.BitmapSource? Img, IReadOnlyList<uint> Pal, bool Skipped);

    private async Task ShowPictureAsync(ItemViewModel it, CancellationToken ct)
    {
        int width = DecodeWidth;
        var r = await Task.Run(() =>
        {
            var info = ImageInfo.TryRead(it.FullPath);
            if (PictureLoader.TooBig(it.Entry.Size, info)) return new Loaded(info, null, Array.Empty<uint>(), true);
            ct.ThrowIfCancellationRequested();
            try
            {
                var img = PictureLoader.Decode(it.FullPath, info, width, ct);
                IReadOnlyList<uint> pal = Array.Empty<uint>();
                try { pal = PictureLoader.Palette(img); } catch { }
                return new Loaded(info, img, pal, false);
            }
            catch (OperationCanceledException) { throw; }
            catch { return new Loaded(info, null, Array.Empty<uint>(), false); } // WIC can't decode it (e.g. HEIC without the codec)
        }, ct);
        if (ct.IsCancellationRequested) return;

        if (r.Img != null) { Picture = r.Img; PictureMaxHeight = 460; }
        else if (!r.Skipped)
        {
            var th = await ShellIcons.ThumbnailAsync(it.FullPath, 512, ct); // shell thumbnail when WIC has no codec
            if (ct.IsCancellationRequested) return;
            if (th != null) { Picture = th; PictureMaxHeight = 460; }
        }
        foreach (var c in r.Pal) Swatches.Add(new Swatch(ColorPalette.Hex(c), Frozen(new SolidColorBrush(PictureLoader.ToColor(c)))));
        Raise(nameof(HasSwatches));
        FillRows(r.Info, r.Skipped);
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private void FillRows(ImageInfo? info, bool skipped)
    {
        if (info == null) { Rows.Add(new InfoRow("Format", "Unknown or damaged picture")); Raise(nameof(HasRows)); return; }
        Rows.Add(new InfoRow("Format", MediaFormat.FormatName(info.Format)));
        Rows.Add(new InfoRow("Dimensions", $"{info.DisplayWidth} × {info.DisplayHeight} · {MediaFormat.Megapixels(info.DisplayWidth, info.DisplayHeight)}"));
        if (info.Frames > 1) Rows.Add(new InfoRow("Frames", info.Frames.ToString()));
        if (info.HasAlpha) Rows.Add(new InfoRow("Transparency", "has transparency"));
        if (skipped) Rows.Add(new InfoRow("Preview", "skipped, the picture is very large"));
        var e = info.Exif;
        if (e != null)
        {
            if (e.Taken != null) Rows.Add(new InfoRow("Taken", e.Taken.Value.ToString("g")));
            var cam = string.Join(' ', new[] { e.Make, e.Model }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
            if (e.Make != null && e.Model != null && e.Model.StartsWith(e.Make.Trim(), StringComparison.OrdinalIgnoreCase)) cam = e.Model.Trim();
            if (cam.Length > 0) Rows.Add(new InfoRow("Camera", cam));
            if (!string.IsNullOrWhiteSpace(e.Lens)) Rows.Add(new InfoRow("Lens", e.Lens!.Trim()));
            var exp = new List<string>();
            if (e.ExposureSeconds != null) exp.Add(MediaFormat.Exposure(e.ExposureSeconds.Value));
            if (e.FNumber != null) exp.Add(MediaFormat.FNumber(e.FNumber.Value));
            if (e.Iso != null) exp.Add("ISO " + e.Iso);
            if (exp.Count > 0) Rows.Add(new InfoRow("Exposure", string.Join(" · ", exp)));
            if (e.FocalLengthMm != null) Rows.Add(new InfoRow("Focal length", MediaFormat.FocalLength(e.FocalLengthMm.Value)));
            if (e.HasLocation) LocationText = MediaFormat.Location(e.Latitude!.Value, e.Longitude!.Value);
        }
        Raise(nameof(HasRows));
    }

    private static string? ReadText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Math.Min(65536, fs.Length)];
            var n = fs.Read(buf, 0, buf.Length);
            if (Array.IndexOf(buf, (byte)0, 0, Math.Min(n, 8000)) >= 0) return null;
            var lines = Encoding.UTF8.GetString(buf, 0, n).Split('\n').Take(200).Select(l => l.TrimEnd('\r'));
            return string.Join('\n', lines);
        }
        catch { return null; }
    }
}
