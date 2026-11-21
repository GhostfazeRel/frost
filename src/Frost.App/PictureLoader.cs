using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Frost.Core;

namespace Frost.App;

/// <summary>WIC decoding shared by the preview pane, Quick Look and the photo tools. All methods are safe to call off the UI thread (results are frozen).</summary>
internal static class PictureLoader
{
    public const long MaxBytes = 200L * 1024 * 1024;
    public const long MaxPixels = 100_000_000;

    public static bool IsPicture(ItemViewModel i) => !i.IsDirectory && ImageInfo.IsImageExtension(i.Entry.Extension);

    /// <summary>True when decoding would be too heavy (file above 200 MB or picture above 100 MP): show info only.</summary>
    public static bool TooBig(long fileSize, ImageInfo? info) =>
        fileSize > MaxBytes || (info != null && (long)info.Width * info.Height > MaxPixels);

    /// <summary>Decodes to at most <paramref name="targetPx"/> pixels wide (0 = full size) as displayed, with EXIF orientation applied. Throws when WIC can't decode.</summary>
    public static BitmapSource Decode(string path, ImageInfo? info, int targetPx, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = fs;
        if (info != null && targetPx > 0 && info.DisplayWidth > targetPx)
        {
            // a 90 degree EXIF rotation swaps the axes: the stored height becomes the displayed width
            if (info.IsRotated90) bi.DecodePixelHeight = targetPx; else bi.DecodePixelWidth = targetPx;
        }
        bi.EndInit();
        bi.Freeze();
        ct.ThrowIfCancellationRequested();
        var r = ApplyOrientation(bi, info?.Orientation ?? 1);
        if (!r.IsFrozen) r.Freeze();
        return r;
    }

    public static BitmapSource ApplyOrientation(BitmapSource src, int orientation)
    {
        var (flipH, flipV, angle) = orientation switch
        {
            2 => (true, false, 0), 3 => (false, false, 180), 4 => (false, true, 0), 5 => (true, false, 270),
            6 => (false, false, 90), 7 => (true, false, 90), 8 => (false, false, 270), _ => (false, false, 0)
        };
        BitmapSource r = src;
        if (flipH || flipV) r = new TransformedBitmap(r, new ScaleTransform(flipH ? -1 : 1, flipV ? -1 : 1));
        if (angle != 0) r = new TransformedBitmap(r, new RotateTransform(angle));
        return r;
    }

    /// <summary>Main colours of a picture, taken from a ~64 px copy.</summary>
    public static IReadOnlyList<uint> Palette(BitmapSource src)
    {
        double s = Math.Min(1.0, 64.0 / Math.Max(src.PixelWidth, src.PixelHeight));
        BitmapSource small = s < 1 ? new TransformedBitmap(src, new ScaleTransform(s, s)) : src;
        var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var buf = new byte[w * h * 4];
        conv.CopyPixels(buf, w * 4, 0);
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = BitConverter.ToUInt32(buf, i * 4); // B,G,R,A little-endian = 0xAARRGGBB
        return ColorPalette.Extract(px, 5);
    }

    public static Color ToColor(uint argb) => Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
