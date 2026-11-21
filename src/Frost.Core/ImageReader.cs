using System.Text;

namespace Frost.Core;

/// <summary>Header-only picture parsing for ImageInfo.TryRead. Reads only headers/metadata chunks, never pixel data.</summary>
internal static class ImageReader
{
    private const int MaxMeta = 1 << 20; // never buffer more than 1 MB of metadata

    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "avif" };

    public static bool IsImageExtension(string? ext) => ext != null && Exts.Contains(ext.TrimStart('.'));

    private static int Fill(Stream s, byte[] buf, int n)
    {
        int t = 0;
        while (t < n) { int r = s.Read(buf, t, n - t); if (r <= 0) break; t += r; }
        return t;
    }

    private static byte[]? Read(Stream s, long n)
    {
        if (n < 0 || n > MaxMeta || s.Position + n > s.Length) return null;
        var b = new byte[n];
        return Fill(s, b, (int)n) == n ? b : null;
    }

    private static uint BE32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    private static int LE16(byte[] b, int o) => b[o] | b[o + 1] << 8;
    private static uint LE32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

    public static ImageInfo? Read(Stream s)
    {
        if (!s.CanSeek)
        {
            var ms = new MemoryStream(); // shortcut: non-seekable streams are buffered up to 8 MB, enough for any header
            var buf = new byte[81920];
            int r;
            while (ms.Length < (8 << 20) && (r = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, r);
            s = ms;
        }
        s.Position = 0;
        var h = new byte[16];
        if (Fill(s, h, 16) < 12) return null;
        s.Position = 0;
        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return Jpeg(s);
        if (h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G') return Png(s);
        if (h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8') return Gif(s);
        if (h[0] == 'B' && h[1] == 'M') return Bmp(s);
        if (h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return WebP(s);
        if ((h[0] == 'I' && h[1] == 'I' && h[2] == 42 && h[3] == 0) || (h[0] == 'M' && h[1] == 'M' && h[2] == 0 && h[3] == 42)) return Tiff(s);
        if (h[4] == 'f' && h[5] == 't' && h[6] == 'y' && h[7] == 'p') return Heif(s);
        return null;
    }

    // ---- JPEG ----
    private static ImageInfo? Jpeg(Stream s)
    {
        s.Position = 2;
        int w = 0, h = 0;
        ExifInfo? ex = null;
        for (int seg = 0; seg < 1024; seg++)
        {
            int m = NextMarker(s);
            if (m < 0 || m == 0xD9 || m == 0xDA) break;
            if (m == 0x01 || m is >= 0xD0 and <= 0xD8) continue;
            var l = Read(s, 2);
            if (l == null) break;
            int len = l[0] << 8 | l[1];
            if (len < 2) break;
            long payload = len - 2, start = s.Position;
            if (start + payload > s.Length) break;
            if (m == 0xE1 && ex == null && payload >= 14)
            {
                var b = Read(s, payload);
                if (b != null && b[0] == 'E' && b[1] == 'x' && b[2] == 'i' && b[3] == 'f' && b[4] == 0 && b[5] == 0) ex = ExifParser.Parse(b, 6, b.Length);
            }
            else if (m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC) && payload >= 5)
            {
                var b = Read(s, 5);
                if (b != null) { h = b[1] << 8 | b[2]; w = b[3] << 8 | b[4]; }
            }
            s.Position = start + payload;
            if (w > 0 && ex != null) break;
        }
        return w > 0 && h > 0 ? new ImageInfo(ImageFormat.Jpeg, w, h, ex?.Orientation ?? 1, 1, false, ex?.Data) : null;
    }

    private static int NextMarker(Stream s)
    {
        int b = s.ReadByte();
        if (b != 0xFF) return -1; // JPEG segments are contiguous up to SOS
        while (b == 0xFF) b = s.ReadByte();
        return b;
    }

    // ---- PNG ----
    private static ImageInfo? Png(Stream s)
    {
        s.Position = 8;
        int w = 0, h = 0, frames = 1;
        bool alpha = false, gotHdr = false;
        ExifInfo? ex = null;
        for (int i = 0; i < 512; i++)
        {
            var c = Read(s, 8);
            if (c == null) break;
            long len = BE32(c, 0), start = s.Position;
            string type = Encoding.ASCII.GetString(c, 4, 4);
            if (start + len + 4 > s.Length) { if (type != "IHDR") break; }
            if (type == "IHDR" && len >= 13)
            {
                var b = Read(s, 13);
                if (b == null) break;
                w = (int)BE32(b, 0); h = (int)BE32(b, 4);
                alpha = b[9] is 4 or 6;
                gotHdr = true;
            }
            else if (!gotHdr) return null;
            else if (type == "tRNS") alpha = true;
            else if (type == "acTL" && len >= 4) { var b = Read(s, 4); if (b != null) frames = (int)Math.Clamp(BE32(b, 0), 1, 100000); }
            else if (type == "eXIf" && len >= 8) { var b = Read(s, len); if (b != null) ex = ExifParser.Parse(b, 0, b.Length); }
            else if (type == "IDAT") break;
            s.Position = start + len + 4;
        }
        return gotHdr && w > 0 && h > 0 ? new ImageInfo(ImageFormat.Png, w, h, ex?.Orientation ?? 1, frames, alpha, ex?.Data) : null;
    }

    // ---- GIF ----
    private static ImageInfo? Gif(Stream s)
    {
        var d = Read(s, 13);
        if (d == null) return null;
        int w = LE16(d, 6), h = LE16(d, 8), frames = 0;
        bool alpha = false;
        if ((d[10] & 0x80) != 0) s.Position += 3L << ((d[10] & 7) + 1);
        long steps = 0;
        const int Cap = 200_000, MaxFrames = 10_000; // shortcut: huge GIFs report the frames counted so far
        while (steps++ < Cap && frames < MaxFrames)
        {
            int b = s.ReadByte();
            if (b == 0x21)
            {
                int label = s.ReadByte();
                if (label == 0xF9)
                {
                    var g = Read(s, 6);
                    if (g == null) break;
                    if (g[0] == 4 && (g[1] & 1) != 0) alpha = true;
                    if (g[5] != 0) s.Position--; // not the block terminator: let the generic skip handle it
                    else continue;
                }
                if (!SkipSubBlocks(s, ref steps, Cap)) break;
            }
            else if (b == 0x2C)
            {
                var id = Read(s, 9);
                if (id == null) break;
                frames++;
                if ((id[8] & 0x80) != 0) s.Position += 3L << ((id[8] & 7) + 1);
                s.Position++; // LZW minimum code size
                if (!SkipSubBlocks(s, ref steps, Cap)) break;
            }
            else break; // trailer 0x3B, EOF or garbage
        }
        return w > 0 && h > 0 ? new ImageInfo(ImageFormat.Gif, w, h, 1, Math.Max(frames, 1), alpha, null) : null;
    }

    private static bool SkipSubBlocks(Stream s, ref long steps, int cap)
    {
        while (steps++ < cap)
        {
            int n = s.ReadByte();
            if (n < 0) return false;
            if (n == 0) return true;
            s.Position += n;
        }
        return false;
    }

    // ---- BMP ----
    private static ImageInfo? Bmp(Stream s)
    {
        var b = Read(s, 30);
        if (b == null) return null;
        uint hs = LE32(b, 14);
        int w, h, bpp;
        if (hs == 12) { w = LE16(b, 18); h = LE16(b, 20); bpp = LE16(b, 24); }
        else if (hs >= 40) { w = (int)LE32(b, 18); h = (int)LE32(b, 22); bpp = LE16(b, 28); }
        else return null;
        h = Math.Abs(h);
        return w > 0 && h > 0 ? new ImageInfo(ImageFormat.Bmp, w, h, 1, 1, bpp == 32 && hs >= 40, null) : null;
    }

    // ---- WebP ----
    private static ImageInfo? WebP(Stream s)
    {
        s.Position = 12;
        int w = 0, h = 0, frames = 0;
        bool alpha = false, vp8x = false, anim = false;
        ExifInfo? ex = null;
        for (int i = 0; i < 20_000; i++)
        {
            var c = Read(s, 8);
            if (c == null) break;
            string type = Encoding.ASCII.GetString(c, 0, 4);
            long len = LE32(c, 4), start = s.Position;
            if (start + len > s.Length) break;
            if (type == "VP8X" && len >= 10)
            {
                var b = Read(s, 10);
                if (b == null) break;
                alpha |= (b[0] & 0x10) != 0; anim = (b[0] & 0x02) != 0;
                w = 1 + (b[4] | b[5] << 8 | b[6] << 16); h = 1 + (b[7] | b[8] << 8 | b[9] << 16);
                vp8x = true;
            }
            else if (type == "VP8 " && len >= 10 && !vp8x)
            {
                var b = Read(s, 10);
                if (b != null && b[3] == 0x9D && b[4] == 0x01 && b[5] == 0x2A) { w = LE16(b, 6) & 0x3FFF; h = LE16(b, 8) & 0x3FFF; }
            }
            else if (type == "VP8L" && len >= 5 && !vp8x)
            {
                var b = Read(s, 5);
                if (b != null && b[0] == 0x2F) { uint bits = LE32(b, 1); w = (int)(bits & 0x3FFF) + 1; h = (int)(bits >> 14 & 0x3FFF) + 1; alpha |= (bits >> 28 & 1) != 0; }
            }
            else if (type == "ALPH") alpha = true;
            else if (type == "ANMF") frames++;
            else if (type == "EXIF" && len >= 8)
            {
                var b = Read(s, len);
                if (b != null)
                {
                    int o = b.Length > 6 && b[0] == 'E' && b[1] == 'x' ? 6 : 0;
                    ex = ExifParser.Parse(b, o, b.Length);
                }
            }
            s.Position = start + len + (len & 1);
        }
        if (w <= 0 || h <= 0) return null;
        return new ImageInfo(ImageFormat.WebP, w, h, ex?.Orientation ?? 1, anim ? Math.Max(frames, 1) : 1, alpha, ex?.Data);
    }

    // ---- TIFF ----
    private static ImageInfo? Tiff(Stream s)
    {
        var b = Read(s, Math.Min(s.Length, MaxMeta)) ?? Array.Empty<byte>(); // IFDs of big TIFFs may lie further away: then no size, so not a picture for us
        var ex = ExifParser.Parse(b, 0, b.Length);
        return ex is { Width: > 0, Height: > 0 } ? new ImageInfo(ImageFormat.Tiff, ex.Width, ex.Height, ex.Orientation, 1, ex.ExtraSamples, ex.Data) : null;
    }

    // ---- HEIC / AVIF (ISO BMFF) ----
    private static ImageInfo? Heif(Stream s)
    {
        var b = Read(s, Math.Min(s.Length, MaxMeta));
        if (b == null) return null;
        string? major = null; var compat = new List<string>();
        long bestArea = 0; int w = 0, h = 0, rot = 0;
        Boxes(b, 0, b.Length, 0);

        void Boxes(byte[] buf, int pos, int end, int depth)
        {
            while (pos + 8 <= end)
            {
                long size = BE32(buf, pos);
                string type = Encoding.ASCII.GetString(buf, pos + 4, 4);
                int hdr = 8;
                if (size == 1) { if (pos + 16 > end) return; size = (long)(((ulong)BE32(buf, pos + 8) << 32) | BE32(buf, pos + 12)); hdr = 16; }
                else if (size == 0) size = end - pos;
                if (size < hdr) return;
                int p = pos + hdr;
                long boxEnd = Math.Min(pos + size, end);
                int bodyEnd = (int)boxEnd;
                if (type == "ftyp" && bodyEnd - p >= 8)
                {
                    major = Encoding.ASCII.GetString(buf, p, 4);
                    for (int q = p + 8; q + 4 <= bodyEnd; q += 4) compat.Add(Encoding.ASCII.GetString(buf, q, 4));
                }
                else if (type == "meta" && depth < 6) Boxes(buf, p + 4, bodyEnd, depth + 1);
                else if (type is "iprp" or "ipco" && depth < 6) Boxes(buf, p, bodyEnd, depth + 1);
                else if (type == "ispe" && bodyEnd - p >= 12)
                {
                    long iw = BE32(buf, p + 4), ih = BE32(buf, p + 8);
                    if (iw > 0 && ih > 0 && iw < int.MaxValue && ih < int.MaxValue && iw * ih > bestArea) { bestArea = iw * ih; w = (int)iw; h = (int)ih; }
                }
                else if (type == "irot" && bodyEnd - p >= 1 && rot == 0) rot = buf[p] & 3;
                if (pos + size > end) return; // box runs past what we read (e.g. mdat)
                pos = (int)(pos + size);
            }
        }

        if (major == null || w <= 0) return null;
        bool avif = major is "avif" or "avis" || compat.Contains("avif");
        bool heic = major is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1" || compat.Any(c => c is "heic" or "heix" or "mif1");
        if (!avif && !heic) return null;
        bool alpha = Contains(b, "auxiliary:alpha") || Contains(b, "hevc:2015:auxid:1");
        int orient = rot switch { 1 => 8, 2 => 3, 3 => 6, _ => 1 }; // irot counts 90° steps counter-clockwise
        ExifData? data = null;
        // shortcut: Exif is found by scanning for its "Exif\0\0" marker rather than walking iloc/infe; misses Exif stored past the first 1 MB
        for (int i = ((ReadOnlySpan<byte>)b).IndexOf("Exif\0\0"u8); i >= 0 && i + 10 <= b.Length;)
        {
            if (ExifParser.Parse(b, i + 6, b.Length) is { } x) { data = x.Data; break; }
            int next = ((ReadOnlySpan<byte>)b)[(i + 1)..].IndexOf("Exif\0\0"u8);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return new ImageInfo(avif ? ImageFormat.Avif : ImageFormat.Heic, w, h, orient, 1, alpha, data);
    }

    private static bool Contains(byte[] b, string ascii) => ((ReadOnlySpan<byte>)b).IndexOf(Encoding.ASCII.GetBytes(ascii)) >= 0;
}
