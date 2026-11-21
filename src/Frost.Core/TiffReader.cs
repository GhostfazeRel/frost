using System.Globalization;
using System.Text;

namespace Frost.Core;

internal readonly record struct TiffEntry(int Tag, int Type, long Count, long Size, long Data, long Pos);

/// <summary>Bounds-checked TIFF/EXIF reader over a byte buffer. Offsets are relative to the TIFF header. Never throws on bad data.</summary>
internal sealed class TiffReader
{
    private readonly byte[] _b;
    public readonly int Start;
    public readonly int Length;
    public readonly bool LE;
    private readonly HashSet<long> _seen = new(); // IFD offsets already visited: cyclic pointers stop here
    private TiffReader(byte[] b, int start, int end, bool le) { _b = b; Start = start; Length = end - start; LE = le; }

    public static TiffReader? Open(byte[] b, int start, int end)
    {
        end = Math.Min(end, b.Length);
        if (start < 0 || end - start < 8) return null;
        bool le;
        if (b[start] == 'I' && b[start + 1] == 'I') le = true;
        else if (b[start] == 'M' && b[start + 1] == 'M') le = false;
        else return null;
        var r = new TiffReader(b, start, end, le);
        return r.U16(2) == 42 ? r : null;
    }

    public long U16(long rel)
    {
        if (rel < 0 || rel + 2 > Length) return -1;
        int p = Start + (int)rel;
        return LE ? _b[p] | _b[p + 1] << 8 : _b[p] << 8 | _b[p + 1];
    }

    public long U32(long rel)
    {
        if (rel < 0 || rel + 4 > Length) return -1;
        int p = Start + (int)rel;
        uint v = LE ? (uint)(_b[p] | _b[p + 1] << 8 | _b[p + 2] << 16 | _b[p + 3] << 24)
                    : (uint)(_b[p] << 24 | _b[p + 1] << 16 | _b[p + 2] << 8 | _b[p + 3]);
        return v;
    }

    public static int TypeSize(int t) => t switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 => 8, _ => 0 };

    /// <summary>Entries of the IFD at <paramref name="off"/>; null when invalid or already visited. strict = refuse a truncated entry table (editing); otherwise it is clamped.</summary>
    public List<TiffEntry>? Ifd(long off, out long next, bool strict = false)
    {
        next = 0;
        if (off < 8 || !_seen.Add(off)) return null;
        long n = U16(off);
        if (n < 0 || n > 1024) return null;
        long fit = (Length - off - 2) / 12;
        if (n > fit) { if (strict) return null; n = fit; }
        var list = new List<TiffEntry>((int)n);
        for (int i = 0; i < n; i++)
        {
            long pos = off + 2 + i * 12L;
            int tag = (int)U16(pos), type = (int)U16(pos + 2);
            long count = U32(pos + 4), size = TypeSize(type) * count, data = size <= 4 ? pos + 8 : U32(pos + 8);
            if (size <= 0 || data < 0 || data + size > Length) data = -1;
            list.Add(new TiffEntry(tag, type, count, size, data, pos));
        }
        long nx = U32(off + 2 + n * 12);
        next = nx < 0 ? 0 : nx;
        return list;
    }

    public long Ptr(TiffEntry e) => e.Type is 4 or 13 && e.Count == 1 ? U32(e.Pos + 8) : -1;

    public string? Str(TiffEntry e)
    {
        if (e.Type != 2 || e.Data < 0) return null;
        var span = new ReadOnlySpan<byte>(_b, Start + (int)e.Data, (int)e.Size);
        int z = span.IndexOf((byte)0);
        if (z >= 0) span = span[..z];
        var s = Encoding.UTF8.GetString(span).Trim();
        return s.Length == 0 ? null : s;
    }

    public double? Num(TiffEntry e, int i = 0)
    {
        if (e.Data < 0 || i < 0 || i >= e.Count) return null;
        long p = e.Data;
        switch (e.Type)
        {
            case 1 or 7: return _b[Start + p + i];
            case 3: return U16(p + 2L * i);
            case 4: return U32(p + 4L * i);
            case 8: return (short)U16(p + 2L * i);
            case 9: return (int)U32(p + 4L * i);
            case 5: { long n = U32(p + 8L * i), d = U32(p + 8L * i + 4); return d == 0 ? null : n / (double)d; }
            case 10: { long n = (int)U32(p + 8L * i), d = (int)U32(p + 8L * i + 4); return d == 0 ? null : n / (double)d; }
            default: return null;
        }
    }
}

internal sealed record ExifInfo(ExifData? Data, int Orientation, int Width, int Height, bool ExtraSamples);

internal static class ExifParser
{
    /// <summary>Parses the TIFF structure at b[start..end). Null when it isn't one; partial results when damaged.</summary>
    public static ExifInfo? Parse(byte[] b, int start, int end)
    {
        try { return ParseCore(b, start, end); }
        catch (Exception) { return null; }
    }

    private static ExifInfo? ParseCore(byte[] b, int start, int end)
    {
        var r = TiffReader.Open(b, start, end);
        if (r == null) return null;
        var ifd0 = r.Ifd(r.U32(4), out _);
        if (ifd0 == null) return null;

        string? make = null, model = null, lens = null;
        DateTime? taken = null, fallback = null;
        double? exp = null, fnum = null, focal = null, lat = null, lon = null;
        int? iso = null;
        int orientation = 1, w = 0, h = 0;
        bool extra = false;
        long exifPtr = -1, gpsPtr = -1;

        foreach (var e in ifd0)
            switch (e.Tag)
            {
                case 0x010F: make = r.Str(e); break;
                case 0x0110: model = r.Str(e); break;
                case 0x0112: if (r.Num(e) is { } o and >= 1 and <= 8) orientation = (int)o; break;
                case 0x0100: w = (int)(r.Num(e) ?? 0); break;
                case 0x0101: h = (int)(r.Num(e) ?? 0); break;
                case 0x0132: fallback = Date(r.Str(e)); break;
                case 0x0152: extra = true; break;
                case 0x8769: exifPtr = r.Ptr(e); break;
                case 0x8825: gpsPtr = r.Ptr(e); break;
            }

        if (exifPtr > 0 && r.Ifd(exifPtr, out _) is { } ex)
            foreach (var e in ex)
                switch (e.Tag)
                {
                    case 0x9003: taken = Date(r.Str(e)); break;
                    case 0x9004: fallback ??= Date(r.Str(e)); break;
                    case 0x829A: if (r.Num(e) is > 0 and var x) exp = x; break;
                    case 0x829D: if (r.Num(e) is > 0 and var f) fnum = f; break;
                    case 0x8827: if (r.Num(e) is > 0 and var i) iso = (int)Math.Min(i, int.MaxValue); break;
                    case 0x920A: if (r.Num(e) is > 0 and var fl) focal = fl; break;
                    case 0xA434: lens = r.Str(e); break;
                }

        if (gpsPtr > 0 && r.Ifd(gpsPtr, out _) is { } gps)
        {
            string? latRef = null, lonRef = null;
            double? la = null, lo = null;
            foreach (var e in gps)
                switch (e.Tag)
                {
                    case 1: latRef = r.Str(e); break;
                    case 2: la = Dms(r, e); break;
                    case 3: lonRef = r.Str(e); break;
                    case 4: lo = Dms(r, e); break;
                }
            if (la is { } a && lo is { } o2 && latRef != null && lonRef != null)
            {
                if (latRef[0] is 'S' or 's') a = -a;
                if (lonRef[0] is 'W' or 'w') o2 = -o2;
                if (Math.Abs(a) <= 90 && Math.Abs(o2) <= 180) { lat = a; lon = o2; }
            }
        }

        taken ??= fallback;
        ExifData? data = new ExifData(taken, make, model, lens, exp, fnum, iso, focal, lat, lon);
        if (taken == null && make == null && model == null && lens == null && exp == null && fnum == null && iso == null && focal == null && lat == null)
            data = null;
        return new ExifInfo(data, orientation, w, h, extra);
    }

    private static double? Dms(TiffReader r, TiffEntry e)
    {
        if (r.Num(e, 0) is not { } d) return null;
        if (e.Count == 1) return d;
        if (r.Num(e, 1) is not { } m) return null;
        double s = r.Num(e, 2) ?? 0;
        return d + m / 60 + s / 3600;
    }

    private static DateTime? Date(string? s) =>
        s != null && DateTime.TryParseExact(s.Length >= 19 ? s[..19] : s, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
