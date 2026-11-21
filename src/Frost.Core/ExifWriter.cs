namespace Frost.Core;

/// <summary>Lossless JPEG EXIF edits: only the APP1 "Exif" segment is rewritten, everything else is copied byte for byte.
/// Edits patch the TIFF block in place (offsets inside it, e.g. MakerNote, stay valid); at most IFD0 is re-appended.</summary>
internal static class ExifWriter
{
    private static readonly byte[] ExifId = { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };

    public static int Rotate(int cur, bool cw)
    {
        // 1..8 as transforms; clockwise map derived from the rotation matrices (mirrored ones turn the other way round)
        int[] c = { 0, 6, 7, 8, 5, 2, 3, 4, 1 };
        if (cur is < 1 or > 8) cur = 1;
        if (cw) return c[cur];
        for (int i = 1; i <= 8; i++) if (c[i] == cur) return i;
        return 1;
    }

    public static string? SetOrientation(string path, int orientation)
    {
        if (orientation is < 1 or > 8) return "Orientation must be 1 to 8.";
        return Apply(path, old =>
        {
            if (old == null)
            {
                var m = new byte[6 + 8 + 2 + 12 + 4];
                ExifId.CopyTo(m, 0);
                int o = 6;
                m[o] = (byte)'M'; m[o + 1] = (byte)'M'; m[o + 3] = 42; m[o + 7] = 8;
                m[o + 9] = 1; // one entry
                m[o + 10] = 0x01; m[o + 11] = 0x12; m[o + 13] = 3; m[o + 17] = 1; m[o + 19] = (byte)orientation;
                return (m, null);
            }
            var r = TiffReader.Open(old, 6, old.Length);
            if (r == null) return (null, "The EXIF block can't be parsed.");
            var ifd0 = r.Ifd(r.U32(4), out long next, strict: true);
            if (ifd0 == null) return (null, "The EXIF block can't be parsed.");
            int k = ifd0.FindIndex(e => e.Tag == 0x0112);
            if (k >= 0)
            {
                var e = ifd0[k];
                if (e.Type != 3 || e.Count != 1) return (null, "The EXIF orientation entry is unusual; not touching the file.");
                var copy = (byte[])old.Clone();
                Put16(copy, 6 + (int)e.Pos + 8, orientation, r.LE);
                return (copy, null);
            }
            // no orientation yet: append a copy of IFD0 with the extra entry (old IFD0 bytes become unused); all other offsets stay valid
            int n = ifd0.Count, pad = (old.Length - 6) & 1;
            var res = new byte[old.Length + pad + 2 + 12 * (n + 1) + 4];
            old.CopyTo(res, 0);
            int newOff = old.Length - 6 + pad, w = 6 + newOff;
            Put16(res, w, n + 1, r.LE);
            int at = ifd0.FindIndex(e => e.Tag > 0x0112);
            if (at < 0) at = n;
            long ifdOff = r.U32(4);
            for (int i = 0, j = 0; i <= n; i++)
            {
                int dst = w + 2 + 12 * i;
                if (i == at)
                {
                    Put16(res, dst, 0x0112, r.LE); Put16(res, dst + 2, 3, r.LE); Put32(res, dst + 4, 1, r.LE); Put16(res, dst + 8, orientation, r.LE);
                }
                else { Array.Copy(old, 6 + (int)ifdOff + 2 + 12 * j, res, dst, 12); j++; }
            }
            Put32(res, w + 2 + 12 * (n + 1), (uint)next, r.LE);
            Put32(res, 6 + 4, (uint)newOff, r.LE);
            return res.Length - 2 > 65535 ? (null, "The EXIF block would become too large.") : (res, null);
        });
    }

    public static string? RemoveLocation(string path) => Apply(path, old =>
    {
        if (old == null) return (null, null);
        var r = TiffReader.Open(old, 6, old.Length);
        if (r == null) return (null, "The EXIF block can't be parsed.");
        long ifdOff = r.U32(4);
        var ifd0 = r.Ifd(ifdOff, out _, strict: true);
        if (ifd0 == null) return (null, "The EXIF block can't be parsed.");
        int k = ifd0.FindIndex(e => e.Tag == 0x8825);
        if (k < 0) return (null, null);
        long gOff = r.Ptr(ifd0[k]);
        var gps = gOff > 0 ? r.Ifd(gOff, out _, strict: true) : null;
        if (gps == null) return (null, "The GPS block can't be parsed; not touching the file.");
        var res = (byte[])old.Clone();
        foreach (var e in gps)
            if (e.Size > 4 && e.Data >= 0) Array.Clear(res, 6 + (int)e.Data, (int)e.Size);
        Array.Clear(res, 6 + (int)gOff, 2 + 12 * gps.Count + 4);
        // drop the pointer: shift later entries and the next-IFD offset up by one entry, zero the freed tail
        int n = ifd0.Count, b = 6 + (int)ifdOff;
        Put16(res, b, n - 1, r.LE);
        Array.Copy(old, b + 2 + 12 * (k + 1), res, b + 2 + 12 * k, 12 * (n - 1 - k));
        Array.Copy(old, b + 2 + 12 * n, res, b + 2 + 12 * (n - 1), 4);
        Array.Clear(res, b + 2 + 12 * (n - 1) + 4, 12);
        return (res, null);
    });

    private static void Put16(byte[] b, int o, int v, bool le)
    {
        if (le) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); } else { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
    }

    private static void Put32(byte[] b, int o, uint v, bool le)
    {
        for (int i = 0; i < 4; i++) b[o + (le ? i : 3 - i)] = (byte)(v >> (8 * i));
    }

    /// <summary>Finds the Exif APP1 segment, lets <paramref name="edit"/> produce a new payload ("Exif\0\0" + TIFF), swaps the file atomically.</summary>
    private static string? Apply(string path, Func<byte[]?, (byte[]? Payload, string? Error)> edit)
    {
        string? temp = null;
        try
        {
            long segStart, segEnd;
            byte[]? old = null;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < 4 || fs.ReadByte() != 0xFF || fs.ReadByte() != 0xD8) return "Not a JPEG file.";
                long insertAt = 2;
                segStart = segEnd = -1;
                for (int i = 0; i < 1024; i++)
                {
                    long at = fs.Position;
                    int ff = fs.ReadByte(), m = fs.ReadByte();
                    if (ff != 0xFF || m < 0) return "The JPEG structure can't be parsed.";
                    if (m == 0xDA || m == 0xD9) break;
                    if (m is 0x01 or (>= 0xD0 and <= 0xD8)) continue;
                    int hi = fs.ReadByte(), lo = fs.ReadByte();
                    if (lo < 0) return "The JPEG structure can't be parsed.";
                    int len = hi << 8 | lo;
                    if (len < 2 || at + 2 + len > fs.Length) return "The JPEG structure can't be parsed.";
                    if (i == 0 && m == 0xE0) insertAt = at + 2 + len;
                    if (m == 0xE1 && len >= 8)
                    {
                        var id = new byte[6];
                        fs.ReadExactly(id);
                        if (id.AsSpan().SequenceEqual(ExifId))
                        {
                            old = new byte[len - 2];
                            id.CopyTo(old, 0);
                            fs.ReadExactly(old, 6, len - 2 - 6);
                            segStart = at; segEnd = at + 2 + len;
                            break;
                        }
                    }
                    fs.Position = at + 2 + len;
                }
                if (old == null) segStart = segEnd = insertAt;
            }

            var (payload, err) = edit(old);
            if (err != null) return err;
            if (payload == null) return null; // nothing to change
            if (payload.Length + 2 > 65535) return "The EXIF block would become too large.";

            temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "." + Path.GetFileName(path) + ".frost-" + Guid.NewGuid().ToString("N")[..8]);
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                CopyRange(src, dst, 0, segStart);
                int segLen = payload.Length + 2;
                dst.Write(new byte[] { 0xFF, 0xE1, (byte)(segLen >> 8), (byte)segLen });
                dst.Write(payload);
                CopyRange(src, dst, segEnd, src.Length - segEnd);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            File.Replace(temp, path, null);
            temp = null;
            return null;
        }
        catch (Exception e) { return e is IOException or UnauthorizedAccessException ? e.Message : "Couldn't edit the file: " + e.Message; }
        finally { if (temp != null) try { File.Delete(temp); } catch (Exception) { } }
    }

    private static void CopyRange(FileStream src, FileStream dst, long from, long count)
    {
        src.Position = from;
        var buf = new byte[81920];
        while (count > 0)
        {
            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) throw new IOException("Unexpected end of file.");
            dst.Write(buf, 0, n);
            count -= n;
        }
    }
}
