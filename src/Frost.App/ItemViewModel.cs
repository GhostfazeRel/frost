using System.Windows.Media;
using Frost.Core;
using Frost.Shell;

namespace Frost.App;

public sealed class ItemViewModel : ObservableObject
{
    private static readonly SemaphoreSlim ThumbGate = new(4);
    private static readonly HashSet<string> ThumbExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".avif", ".svg", ".psd",
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".pdf"
    };

    private readonly CancellationToken _ct;
    private ImageSource? _icon, _big, _thumb;
    private bool _thumbStarted, _renaming, _dropTarget;
    private string _editName = "";

    public ItemViewModel(FileEntry e, DateTime now, CancellationToken ct)
    {
        Entry = e;
        _ct = ct;
        Name = e.Name;
        FullPath = e.FullPath;
        IsDirectory = e.IsDirectory;
        TypeText = Format.TypeName(e);
        SizeText = e.IsDirectory ? "" : Format.Size(e.Size);
        ModifiedText = Format.Date(e.Modified, now);
        ItemOpacity = e.IsHidden ? 0.55 : 1.0;
    }

    public FileEntry Entry { get; }
    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string TypeText { get; }
    public string SizeText { get; }
    public string ModifiedText { get; }
    public double ItemOpacity { get; }

    public ImageSource Icon => _icon ??= ShellIcons.Get(FullPath, IsDirectory, false);

    public ImageSource Picture
    {
        get
        {
            if (_thumb != null) return _thumb;
            if (!_thumbStarted && !IsDirectory && ThumbExt.Contains(Entry.Extension)) LoadThumb();
            return _big ??= ShellIcons.Get(FullPath, IsDirectory, true);
        }
    }

    private async void LoadThumb()
    {
        _thumbStarted = true;
        try
        {
            await ThumbGate.WaitAsync(_ct);
            try
            {
                var img = await ShellIcons.ThumbnailAsync(FullPath, 192, _ct);
                if (img != null && !_ct.IsCancellationRequested) { _thumb = img; Raise(nameof(Picture)); }
            }
            finally { ThumbGate.Release(); }
        }
        catch { }
    }

    public bool IsRenaming { get => _renaming; set => Set(ref _renaming, value); }
    public string EditName { get => _editName; set => Set(ref _editName, value); }
    public bool IsDropTarget { get => _dropTarget; set => Set(ref _dropTarget, value); }

    public void BeginRename() { EditName = Name; IsRenaming = true; }
}
