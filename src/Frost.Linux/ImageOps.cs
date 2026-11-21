using Avalonia;
using Avalonia.Media;
using Frost.Core;
using SkiaSharp;
namespace Frost.Linux;

/// <summary>Pixel work on pictures with SkiaSharp (already shipped with Avalonia): orientation, small decodes, encode, resize.</summary>
public static class ImageOps
{
    public const long MaxBytes = 200_000_000;
    public const double MaxMegapixels = 100;

    /// <summary>True when the file is small enough to decode without risking memory.</summary>
    public static bool Decodable(ImageInfo? info, long size) => info != null && size <= MaxBytes && info.Megapixels <= MaxMegapixels;

    /// <summary>Skia can decode these; HEIC/AVIF/TIFF/SVG are shown as icon + info.</summary>
    public static bool SkiaFormat(ImageInfo? i) => i != null && i.Format is ImageFormat.Jpeg or ImageFormat.Png or ImageFormat.Gif or ImageFormat.Bmp or ImageFormat.WebP;

    /// <summary>Transform that displays an unrotated decode the way EXIF <paramref name="orientation"/> says (matches <see cref="Orient"/>).</summary>
    public static Matrix DisplayMatrix(int orientation)
    {
        var rot = Matrix.CreateRotation(Math.PI / 2);
        return orientation switch
        {
            2 => Matrix.CreateScale(-1, 1),
            3 => Matrix.CreateRotation(Math.PI),
            4 => Matrix.CreateScale(1, -1),
            5 => rot * Matrix.CreateScale(-1, 1),
            6 => rot,
            7 => rot * Matrix.CreateScale(1, -1),
            8 => Matrix.CreateRotation(-Math.PI / 2),
            _ => Matrix.Identity,
        };
    }

    /// <summary>A new bitmap with EXIF orientation applied (the same bitmap for 1).</summary>
    public static SKBitmap Orient(SKBitmap src, int orientation)
    {
        if (orientation is < 2 or > 8) return src;
        float w = src.Width, h = src.Height;
        var m = orientation switch
        {
            2 => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            3 => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            4 => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            5 => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            6 => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            7 => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            _ => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        };
        bool swap = orientation >= 5;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height, src.ColorType, src.AlphaType);
        using (var c = new SKCanvas(dst)) { c.SetMatrix(m); c.DrawBitmap(src, 0, 0); }
        src.Dispose();
        return dst;
    }

    /// <summary>Full decode with orientation applied; null when Skia can't read it.</summary>
    public static SKBitmap? Load(string path, int orientation)
    {
        try { var b = SKBitmap.Decode(path); return b == null ? null : Orient(b, orientation); }
        catch { return null; }
    }

    /// <summary>Decodes at most about <paramref name="maxSide"/> pixels on the long side (JPEG decodes at 1/2, 1/4, 1/8 size, which is fast), orientation applied.</summary>
    public static SKBitmap? LoadSmall(string path, int maxSide, int orientation)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null) return null;
            var size = codec.GetScaledDimensions(Math.Min(1f, maxSide / (float)Math.Max(codec.Info.Width, codec.Info.Height)));
            var bmp = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            if (codec.GetPixels(bmp.Info, bmp.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) { bmp.Dispose(); return null; }
            return Orient(bmp, orientation);
        }
        catch { return null; }
    }

    public static SKBitmap Scale(SKBitmap src, int width, int height)
    {
        var r = src.Resize(new SKImageInfo(Math.Max(1, width), Math.Max(1, height), src.ColorType, src.AlphaType), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return r ?? src;
    }

    /// <summary>The main colours of a picture (decoded small), or empty.</summary>
    public static IReadOnlyList<uint> Palette(string path, int orientation)
    {
        using var small = LoadSmall(path, 64, orientation);
        if (small == null) return Array.Empty<uint>();
        using var sq = Scale(small, 64, 64);
        using var bgra = sq.ColorType == SKColorType.Bgra8888 ? null : sq.Copy(SKColorType.Bgra8888);
        var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>((bgra ?? sq).GetPixelSpan()); // Bgra8888 little-endian = 0xAARRGGBB
        return ColorPalette.Extract(px, 5);
    }

    public static string ExtFor(SKEncodedImageFormat f) => f switch { SKEncodedImageFormat.Png => ".png", SKEncodedImageFormat.Webp => ".webp", _ => ".jpg" };

    public static byte[] Encode(SKBitmap bmp, SKEncodedImageFormat f)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(f, f == SKEncodedImageFormat.Png ? 100 : 90);
        return data.ToArray();
    }

    /// <summary>"name (suffix).ext", then "name (suffix) (2).ext" ... never an existing file.</summary>
    public static string UniquePath(string dir, string stem, string ext)
    {
        var p = Path.Combine(dir, stem + ext);
        for (int i = 2; File.Exists(p) || Directory.Exists(p); i++) p = Path.Combine(dir, $"{stem} ({i}){ext}");
        return p;
    }
}

public static class ImageOpsExtra
{
    /// <summary>JPEG has no alpha: paint transparent pixels on white instead of black.</summary>
    public static SKBitmap OnWhite(SKBitmap src)
    {
        if (src.AlphaType == SKAlphaType.Opaque) return src;
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var c = new SKCanvas(dst)) { c.Clear(SKColors.White); c.DrawBitmap(src, 0, 0); }
        return dst;
    }
}
