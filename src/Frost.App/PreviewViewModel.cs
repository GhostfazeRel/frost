using System.Text;
using System.Windows.Media;
using Frost.Core;
using Frost.Shell;

namespace Frost.App;

public sealed class PreviewViewModel : ObservableObject
{
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".xml", ".csv", ".cs", ".cpp", ".c", ".h", ".hpp", ".py", ".js", ".ts", ".tsx", ".jsx",
        ".html", ".htm", ".css", ".xaml", ".ini", ".cfg", ".conf", ".yml", ".yaml", ".toml", ".sh", ".bat", ".cmd", ".ps1",
        ".java", ".kt", ".rs", ".go", ".sql", ".csproj", ".sln", ".props", ".targets", ".gitignore", ".editorconfig", ".reg", ".lua", ".php", ".rb"
    };

    private CancellationTokenSource? _cts;
    private string _title = "", _type = "", _size = "", _modified = "", _created = "", _text = "";
    private ImageSource? _picture;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string TypeText { get => _type; private set => Set(ref _type, value); }
    public string SizeText { get => _size; private set => Set(ref _size, value); }
    public string ModifiedText { get => _modified; private set => Set(ref _modified, value); }
    public string CreatedText { get => _created; private set => Set(ref _created, value); }
    public string TextContent { get => _text; private set { if (Set(ref _text, value)) Raise(nameof(HasText)); } }
    public bool HasText => _text.Length > 0;
    public ImageSource? Picture { get => _picture; private set => Set(ref _picture, value); }

    public async void Show(IReadOnlyList<ItemViewModel> sel, TabViewModel? tab)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            TextContent = ""; ModifiedText = ""; CreatedText = ""; TypeText = ""; SizeText = "";
            if (sel.Count == 0)
            {
                if (tab == null) return;
                Title = tab.Title; TypeText = "Folder"; SizeText = tab.CountText;
                Picture = ShellIcons.Get(tab.Path, true, true);
                return;
            }
            if (sel.Count > 1)
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
