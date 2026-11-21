using System.Text;

namespace Frost.Core.Tests;

/// <summary>Builds tiny valid files from bytes.</summary>
internal static class TestImages
{
    public sealed record E(int Tag, int Type, byte[] Data, int Count, int PtrTo = -1);

    public static E Ascii(int tag, string s) { var b = Encoding.ASCII.GetBytes(s + "\0"); return new(tag, 2, b, b.Length); }
    public static E Short(int tag, int v, bool le) => new(tag, 3, le ? new[] { (byte)v, (byte)(v >> 8) } : new[] { (byte)(v >> 8), (byte)v }, 1);
    public static E Ptr(int tag, int ifdIndex) => new(tag, 4, new byte[4], 1, ifdIndex);
    public static E Rat(int tag, bool le, params (uint N, uint D)[] v)
    {
        var m = new MemoryStream();
        foreach (var (n, d) in v) { m.Write(U32(n, le)); m.Write(U32(d, le)); }
        return new(tag, 5, m.ToArray(), v.Length);
    }
    public static E Long(int tag, uint v, bool le) => new(tag, 4, U32(v, le), 1);

    public static byte[] U16(int v, bool le) => le ? new[] { (byte)v, (byte)(v >> 8) } : new[] { (byte)(v >> 8), (byte)v };
    public static byte[] U32(uint v, bool le) => le ? new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) } : new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>TIFF block (starting at "II"/"MM"). ifds[0] is IFD0; Ptr entries point to the other IFDs by index. cycleBack makes IFD0's next pointer point to itself.</summary>
    public static byte[] Tiff(bool le, E[][] ifds, bool cycleBack = false)
    {
        int Size(E[] ifd) => 2 + 12 * ifd.Length + 4 + ifd.Where(e => e.Data.Length > 4).Sum(e => (e.Data.Length + 1) & ~1);
        var offs = new int[ifds.Length];
        int at = 8;
        for (int i = 0; i < ifds.Length; i++) { offs[i] = at; at += Size(ifds[i]); }
        var m = new MemoryStream();
        m.Write(Encoding.ASCII.GetBytes(le ? "II" : "MM")); m.Write(U16(42, le)); m.Write(U32(8, le));
        for (int i = 0; i < ifds.Length; i++)
        {
            var ifd = ifds[i];
            m.Write(U16(ifd.Length, le));
            int blob = offs[i] + 2 + 12 * ifd.Length + 4;
            var blobs = new MemoryStream();
            foreach (var e in ifd)
            {
                m.Write(U16(e.Tag, le)); m.Write(U16(e.Type, le)); m.Write(U32((uint)e.Count, le));
                if (e.PtrTo >= 0) m.Write(U32((uint)offs[e.PtrTo], le));
                else if (e.Data.Length <= 4) { m.Write(e.Data); m.Write(new byte[4 - e.Data.Length]); }
                else
                {
                    m.Write(U32((uint)(blob + (int)blobs.Length), le));
                    blobs.Write(e.Data); if ((e.Data.Length & 1) != 0) blobs.WriteByte(0);
                }
            }
            m.Write(U32(cycleBack && i == 0 ? 8u : 0, le));
            m.Write(blobs.ToArray());
        }
        return m.ToArray();
    }

    /// <summary>A realistic photo EXIF: IFD0 (Make, Model, Orientation, ptrs), Exif IFD, GPS IFD.</summary>
    public static byte[] PhotoTiff(bool le, int orientation = 1, bool gps = true, string latRef = "N", string lonRef = "E", bool withOrientation = true)
    {
        var ifd0 = new List<E> { Ascii(0x010F, "ACME"), Ascii(0x0110, "Cam 1") };
        if (withOrientation) ifd0.Add(Short(0x0112, orientation, le));
        ifd0.Add(Ptr(0x8769, 1));
        if (gps) ifd0.Add(Ptr(0x8825, 2));
        var exif = new[]
        {
            Rat(0x829A, le, (1, 120)), Rat(0x829D, le, (18, 10)), Short(0x8827, 400, le), Ascii(0x9003, "2024:05:17 14:30:05"),
            Rat(0x920A, le, (26, 1)), Ascii(0xA434, "Lens 26mm"),
        };
        var g = new[]
        {
            Ascii(1, latRef), Rat(2, le, (52, 1), (13, 1), (3600, 100)), Ascii(3, lonRef), Rat(4, le, (21, 1), (0, 1), (4392, 100)),
        };
        return Tiff(le, gps ? new[] { ifd0.ToArray(), exif, g } : new[] { ifd0.ToArray(), exif });
    }

    public static byte[] App1(byte[] tiff)
    {
        var body = new List<byte>(Encoding.ASCII.GetBytes("Exif\0\0")); body.AddRange(tiff);
        var seg = new List<byte> { 0xFF, 0xE1, (byte)((body.Count + 2) >> 8), (byte)(body.Count + 2) }; seg.AddRange(body);
        return seg.ToArray();
    }

    /// <summary>SOI [APP0 JFIF] [APP1] SOF0 + a DQT-ish segment + SOS + "pixel" bytes + EOI.</summary>
    public static byte[] Jpeg(int w, int h, byte[]? tiff = null, bool jfif = false)
    {
        var m = new MemoryStream();
        m.Write(new byte[] { 0xFF, 0xD8 });
        if (jfif) m.Write(new byte[] { 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 });
        if (tiff != null) m.Write(App1(tiff));
        m.Write(new byte[] { 0xFF, 0xDB, 0, 5, 1, 2, 3 });
        m.Write(new byte[] { 0xFF, 0xC0, 0, 11, 8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 1, 1, 0x11, 0 });
        m.Write(new byte[] { 0xFF, 0xDA, 0, 8, 1, 1, 0, 0, 63, 0 });
        m.Write(new byte[] { 0x12, 0x34, 0xFF, 0x00, 0x56, 0xFF, 0xD9 });
        return m.ToArray();
    }

    static byte[] Be32(uint v) => U32(v, false);

    public static byte[] Png(int w, int h, int colorType = 2, bool trns = false, int frames = 0)
    {
        var m = new MemoryStream();
        m.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string t, byte[] d) { m.Write(Be32((uint)d.Length)); m.Write(Encoding.ASCII.GetBytes(t)); m.Write(d); m.Write(new byte[4]); }
        var ihdr = new List<byte>(); ihdr.AddRange(Be32((uint)w)); ihdr.AddRange(Be32((uint)h)); ihdr.AddRange(new byte[] { 8, (byte)colorType, 0, 0, 0 });
        Chunk("IHDR", ihdr.ToArray());
        if (frames > 0) { var a = new List<byte>(); a.AddRange(Be32((uint)frames)); a.AddRange(Be32(0)); Chunk("acTL", a.ToArray()); }
        if (trns) Chunk("tRNS", new byte[] { 0, 0 });
        Chunk("IDAT", new byte[] { 1, 2, 3 });
        Chunk("IEND", Array.Empty<byte>());
        return m.ToArray();
    }

    public static byte[] Gif(int w, int h, int frames, bool transparent = false)
    {
        var m = new MemoryStream();
        m.Write(Encoding.ASCII.GetBytes("GIF89a")); m.Write(U16(w, true)); m.Write(U16(h, true)); m.Write(new byte[] { 0x80, 0, 0 });
        m.Write(new byte[6]); // 2-colour global table
        for (int i = 0; i < frames; i++)
        {
            m.Write(new byte[] { 0x21, 0xF9, 4, (byte)(transparent ? 1 : 0), 10, 0, 0, 0 });
            m.Write(new byte[] { 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0 });
            m.Write(new byte[] { 2, 2, 0x44, 0x01, 0 });
        }
        m.WriteByte(0x3B);
        return m.ToArray();
    }

    public static byte[] Bmp(int w, int h, int bpp = 24)
    {
        var m = new MemoryStream();
        m.Write(Encoding.ASCII.GetBytes("BM")); m.Write(new byte[12]);
        m.Write(U32(40, true)); m.Write(U32((uint)w, true)); m.Write(U32((uint)h, true)); m.Write(U16(1, true)); m.Write(U16(bpp, true)); m.Write(new byte[24]);
        return m.ToArray();
    }

    static byte[] Riff(params (string Type, byte[] Data)[] chunks)
    {
        var body = new MemoryStream(); body.Write(Encoding.ASCII.GetBytes("WEBP"));
        foreach (var (t, d) in chunks) { body.Write(Encoding.ASCII.GetBytes(t)); body.Write(U32((uint)d.Length, true)); body.Write(d); if ((d.Length & 1) != 0) body.WriteByte(0); }
        var m = new MemoryStream(); m.Write(Encoding.ASCII.GetBytes("RIFF")); m.Write(U32((uint)body.Length, true)); m.Write(body.ToArray());
        return m.ToArray();
    }

    public static byte[] WebPLossy(int w, int h) =>
        Riff(("VP8 ", new byte[] { 0x10, 0, 0, 0x9D, 0x01, 0x2A, (byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8), 0, 0 }));

    public static byte[] WebPLossless(int w, int h, bool alpha)
    {
        uint bits = (uint)(w - 1) | (uint)(h - 1) << 14 | (alpha ? 1u << 28 : 0);
        return Riff(("VP8L", new byte[] { 0x2F, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24) }));
    }

    public static byte[] WebPExtended(int w, int h, bool alpha, int frames, byte[]? exif = null)
    {
        var x = new byte[10];
        x[0] = (byte)((alpha ? 0x10 : 0) | (frames > 0 ? 0x02 : 0) | (exif != null ? 0x08 : 0));
        int cw = w - 1, ch = h - 1;
        x[4] = (byte)cw; x[5] = (byte)(cw >> 8); x[6] = (byte)(cw >> 16); x[7] = (byte)ch; x[8] = (byte)(ch >> 8); x[9] = (byte)(ch >> 16);
        var list = new List<(string, byte[])> { ("VP8X", x) };
        for (int i = 0; i < frames; i++) list.Add(("ANMF", new byte[16]));
        if (exif != null) list.Add(("EXIF", exif));
        return Riff(list.ToArray());
    }

    static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return Be32((uint)(8 + body.Length)).Concat(Encoding.ASCII.GetBytes(type)).Concat(body).ToArray();
    }

    public static byte[] Heif(string brand, int w, int h, int irot = -1, byte[]? tiff = null)
    {
        var ispe = Box("ispe", new byte[4], Be32((uint)w), Be32((uint)h));
        var ipco = irot >= 0 ? Box("ipco", ispe, Box("irot", new[] { (byte)irot })) : Box("ipco", ispe);
        var meta = Box("meta", new byte[4], Box("iprp", ipco));
        var parts = new List<byte[]> { Box("ftyp", Encoding.ASCII.GetBytes(brand), new byte[4], Encoding.ASCII.GetBytes("mif1")), meta };
        if (tiff != null) parts.Add(Box("mdat", Be32(6), Encoding.ASCII.GetBytes("Exif\0\0"), tiff));
        return parts.SelectMany(p => p).ToArray();
    }
}
