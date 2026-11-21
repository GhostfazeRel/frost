using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Frost.Core;
using SkiaSharp;
namespace Frost.Linux;

/// <summary>Picture thumbnails for the icon views, cached the freedesktop way (~/.cache/thumbnails/{normal,large}/md5(uri).png) so other file managers share them.</summary>
public static class Thumbnails
{
    static string Root => Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x : Path.Combine(Platform.Home, ".cache"), "thumbnails");
    static readonly SemaphoreSlim Gate = new(3);
    static readonly Dictionary<string, Bitmap> Memory = new(); // UI thread only

    /// <summary>RFC 3986 file URI the way GLib writes it, which is what the cache file name is hashed from.</summary>
    public static string FileUri(string path)
    {
        var sb = new StringBuilder("file://");
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            char c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || "-_.~!$&'()*+,;=:@/".Contains(c))) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    public static string CachePath(string path, bool large) => Path.Combine(Root, large ? "large" : "normal", Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(FileUri(path)))) + ".png");

    static long Mtime(string path) => new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();

    /// <summary>Thumbnail bitmap about <paramref name="px"/> wide, or null (not a picture Skia reads, too big, unreadable). Call from the UI thread.</summary>
    public static async Task<Bitmap?> GetAsync(string path, int px, CancellationToken ct)
    {
        var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + px;
        if (Memory.TryGetValue(key, out var hit)) return hit;
        bool large = px > 128;
        Bitmap? bmp;
        try
        {
            await Gate.WaitAsync(ct);
            try { bmp = await Task.Run(() => Load(path, large, px), ct); }
            finally { Gate.Release(); }
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
        if (bmp != null) { if (Memory.Count > 300) Memory.Clear(); Memory[key] = bmp; }
        return bmp;
    }

    static Bitmap? Load(string path, bool large, int px)
    {
        long mtime = Mtime(path);
        foreach (var l in large ? new[] { true } : new[] { false, true }) // a 256 px thumbnail also serves a small tile
        {
            var cache = CachePath(path, l);
            if (!File.Exists(cache)) continue;
            try
            {
                var bytes = File.ReadAllBytes(cache);
                if (ReadText(bytes).TryGetValue("Thumb::MTime", out var m) && m == mtime.ToString()) return Decode(bytes, px);
            }
            catch { }
        }
        var info = ImageInfo.TryRead(path);
        if (!ImageOps.SkiaFormat(info) || !ImageOps.Decodable(info, new FileInfo(path).Length)) return null;
        using var small = ImageOps.LoadSmall(path, large ? 256 : 128, info!.Orientation);
        if (small == null) return null;
        int side = large ? 256 : 128;
        using var scaled = Math.Max(small.Width, small.Height) > side ? ImageOps.Scale(small, small.Width >= small.Height ? side : Math.Max(1, small.Width * side / small.Height), small.Height > small.Width ? side : Math.Max(1, small.Height * side / small.Width)) : null;
        var png = WithText(ImageOps.Encode(scaled ?? small, SKEncodedImageFormat.Png), new Dictionary<string, string>
        {
            ["Thumb::URI"] = FileUri(path), ["Thumb::MTime"] = mtime.ToString(), ["Thumb::Size"] = new FileInfo(path).Length.ToString(), ["Software"] = "Frost",
        });
        try { Save(CachePath(path, large), png); } catch { } // a read-only cache must not break the view
        return Decode(png, px);
    }

    static Bitmap Decode(byte[] png, int px) { using var ms = new MemoryStream(png); return Bitmap.DecodeToWidth(ms, px); }

    static void Save(string target, byte[] data)
    {
        if (OperatingSystem.IsWindows()) return; // Frost.Linux only runs on Linux; this satisfies the platform analyzer
        var dir = Path.GetDirectoryName(target)!;
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (!Directory.Exists(dir)) { Directory.CreateDirectory(Path.GetDirectoryName(dir)!, mode); Directory.CreateDirectory(dir, mode); }
        var tmp = target + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, target, true);
    }

    // ---- PNG text chunks ----
    public static Dictionary<string, string> ReadText(byte[] png)
    {
        var d = new Dictionary<string, string>();
        int p = 8;
        while (p + 12 <= png.Length)
        {
            int len = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3];
            if (len < 0 || p + 12 + len > png.Length) break;
            var type = Encoding.ASCII.GetString(png, p + 4, 4);
            if (type == "IDAT") break;
            if (type == "tEXt")
            {
                int z = Array.IndexOf(png, (byte)0, p + 8, len);
                if (z > 0) d[Encoding.Latin1.GetString(png, p + 8, z - p - 8)] = Encoding.UTF8.GetString(png, z + 1, p + 8 + len - z - 1);
            }
            p += 12 + len;
        }
        return d;
    }

    static byte[] WithText(byte[] png, Dictionary<string, string> text)
    {
        var ms = new MemoryStream();
        ms.Write(png, 0, 33); // signature + IHDR
        foreach (var (k, v) in text)
        {
            var data = Encoding.Latin1.GetBytes(k).Concat(new byte[] { 0 }).Concat(Encoding.UTF8.GetBytes(v)).ToArray();
            Chunk(ms, "tEXt", data);
        }
        ms.Write(png, 33, png.Length - 33);
        return ms.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        void Be(uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        Be((uint)data.Length);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (var b in t.Concat(data)) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; }
        Be(~crc);
    }
}

public partial class MainWindow
{
    /// <summary>The icon of an item in the Large / Extra large views: its thumbnail for pictures once it is ready, the type glyph meanwhile.</summary>
    Control ItemIcon(Item it, double size)
    {
        var glyph = Glyph(it.Glyph, it.Tint, size);
        if (size < 100 || !IsPicture(it) || it.Entry.Size > ImageOps.MaxBytes) return glyph;
        var img = new Avalonia.Controls.Image { Stretch = Avalonia.Media.Stretch.Uniform, IsVisible = false, Width = size, Height = size };
        var cts = new CancellationTokenSource();
        img.DetachedFromVisualTree += (_, _) => cts.Cancel(); // list rebuilt or scrolled away: drop the work
        _ = ShowThumb(it.FullPath, (int)(size * RenderScaling), img, glyph, cts.Token);
        return new Avalonia.Controls.Panel { Width = size, Height = size, Children = { glyph, img } };
    }

    static async Task ShowThumb(string path, int px, Avalonia.Controls.Image img, Control glyph, CancellationToken ct)
    {
        var bmp = await Thumbnails.GetAsync(path, px, ct);
        if (bmp == null || ct.IsCancellationRequested) return;
        img.Source = bmp; img.IsVisible = true; glyph.IsVisible = false;
    }
}
