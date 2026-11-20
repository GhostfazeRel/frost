using Avalonia.Media;
using Frost.Core;
namespace Frost.Linux;

/// <summary>Built-in vector glyphs (24x24 grid): one for folders, one for files, a few per type. No icon-theme lookup.</summary>
public static class Glyphs
{
    // Avalonia objects: create and use on the UI thread only.
    static readonly Dictionary<string, Geometry> Cache = new();
    static readonly Dictionary<string, IBrush> Brushes = new();
    const string Folder = "M10,4H4C2.9,4 2,4.9 2,6V18C2,19.1 2.9,20 4,20H20C21.1,20 22,19.1 22,18V8C22,6.9 21.1,6 20,6H12Z";
    const string File = "M14,2H6C4.9,2 4,2.9 4,4V20C4,21.1 4.9,22 6,22H18C19.1,22 20,21.1 20,20V8Z";
    const string Image = "F0 M3,5C3,3.9 3.9,3 5,3H19C20.1,3 21,3.9 21,5V19C21,20.1 20.1,21 19,21H5C3.9,21 3,20.1 3,19Z M8.5,13.5L11,16.5L14.5,12L19,18H5Z";
    const string Music = "M12,3V13.5C11.4,13.2 10.7,13 10,13C7.8,13 6,14.8 6,17C6,19.2 7.8,21 10,21C12.2,21 14,19.2 14,17V7H18V3Z";
    const string Video = "F0 M3,5H21V19H3Z M10,8.5L15.5,12L10,15.5Z";
    const string Archive = "F0 M4,3H20V8H4Z M5,9H19V21H5Z M10,11H14V13H10Z";
    const string Code = "M8,6L2,12L8,18L9.4,16.6L4.8,12L9.4,7.4Z M16,6L14.6,7.4L19.2,12L14.6,16.6L16,18L22,12Z";
    const string Text = "F0 M14,2H6C4.9,2 4,2.9 4,4V20C4,21.1 4.9,22 6,22H18C19.1,22 20,21.1 20,20V8Z M8,12H16V14H8Z M8,16H16V18H8Z";

    static readonly Dictionary<string, (string, string)> ByExt = Build();
    static Dictionary<string, (string, string)> Build()
    {
        var d = new Dictionary<string, (string, string)>();
        void Add(string exts, string geo, string color) { foreach (var e in exts.Split(' ')) d["." + e] = (geo, color); }
        Add("png jpg jpeg gif bmp webp svg tif tiff ico avif heic", Image, "#7BD88F");
        Add("mp3 flac ogg opus wav m4a aac", Music, "#F2A65A");
        Add("mp4 mkv avi mov webm m4v", Video, "#C792EA");
        Add("zip 7z rar tar gz xz bz2 zst", Archive, "#E5C07B");
        Add("cs py js ts c cpp h rs go sh json xml html css yml yaml toml", Code, "#6FC3DF");
        Add("txt md log ini conf cfg csv pdf", Text, "#A9B1C2");
        return d;
    }

    public static (Geometry Geo, IBrush Fill) For(FileEntry e)
    {
        var (geo, color) = e.IsDirectory ? (Folder, "#8AB4FF") : ByExt.GetValueOrDefault(e.Extension.ToLowerInvariant(), (File, "#A9B1C2"));
        if (!Cache.TryGetValue(geo, out var g)) Cache[geo] = g = Geometry.Parse(geo);
        if (!Brushes.TryGetValue(color, out var b)) Brushes[color] = b = new SolidColorBrush(Color.Parse(color)).ToImmutable();
        return (g, b);
    }
}

/// <summary>One row of the file list.</summary>
public sealed class Item
{
    public Item(FileEntry e, DateTime now)
    {
        Entry = e;
        (Glyph, Tint) = Glyphs.For(e);
        TypeText = Format.TypeName(e);
        SizeText = e.IsDirectory ? "" : Format.Size(e.Size);
        ModifiedText = Format.Date(e.Modified, now);
    }
    public FileEntry Entry { get; }
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public bool IsDirectory => Entry.IsDirectory;
    public Geometry Glyph { get; }
    public IBrush Tint { get; }
    public string TypeText { get; }
    public string SizeText { get; }
    public string ModifiedText { get; }
}
