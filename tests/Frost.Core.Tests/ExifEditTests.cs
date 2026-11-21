using Frost.Core;

namespace Frost.Core.Tests;

public sealed class ExifEditTests : IDisposable
{
    private readonly string _d = Directory.CreateTempSubdirectory("frost-exif").FullName;
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }
    private string Write(byte[] b, string n = "p.jpg") { var p = Path.Combine(_d, n); File.WriteAllBytes(p, b); return p; }

    // everything except the APP1 segment
    private static byte[] WithoutApp1(byte[] f)
    {
        var l = new List<byte>(); l.AddRange(f[..2]);
        int i = 2;
        while (i + 4 <= f.Length && f[i] == 0xFF && f[i + 1] != 0xDA)
        {
            int len = f[i + 2] << 8 | f[i + 3];
            if (f[i + 1] != 0xE1) l.AddRange(f[i..(i + 2 + len)]);
            i += 2 + len;
        }
        l.AddRange(f[i..]);
        return l.ToArray();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetOrientation_existing_is_lossless(bool le)
    {
        var orig = TestImages.Jpeg(100, 50, TestImages.PhotoTiff(le, orientation: 1), jfif: true);
        var p = Write(orig);
        Assert.Null(ExifEdit.SetOrientation(p, 6));
        var after = File.ReadAllBytes(p);
        Assert.Equal(orig.Length, after.Length);
        Assert.Equal(WithoutApp1(orig), WithoutApp1(after));
        var i = ImageInfo.TryRead(p)!;
        Assert.Equal(6, i.Orientation);
        Assert.Equal("ACME", i.Exif!.Make);
        Assert.True(i.Exif.HasLocation);
        Assert.Empty(Directory.GetFiles(_d, ".*frost*"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetOrientation_adds_missing_entry(bool le)
    {
        var orig = TestImages.Jpeg(100, 50, TestImages.PhotoTiff(le, withOrientation: false));
        var p = Write(orig);
        Assert.Null(ExifEdit.SetOrientation(p, 8));
        var after = File.ReadAllBytes(p);
        Assert.Equal(WithoutApp1(orig), WithoutApp1(after));
        var i = ImageInfo.TryRead(p)!;
        Assert.Equal(8, i.Orientation);
        Assert.Equal(("ACME", 400, true), (i.Exif!.Make, i.Exif.Iso, i.Exif.HasLocation));
        Assert.Null(ExifEdit.SetOrientation(p, 3)); // now the in-place path
        Assert.Equal(3, ImageInfo.TryRead(p)!.Orientation);
    }

    [Fact]
    public void SetOrientation_creates_exif_when_missing()
    {
        var orig = TestImages.Jpeg(100, 50, jfif: true);
        var p = Write(orig);
        Assert.Null(ExifEdit.SetOrientation(p, 3));
        var after = File.ReadAllBytes(p);
        Assert.Equal(orig, WithoutApp1(after));
        Assert.Equal(3, ImageInfo.TryRead(p)!.Orientation);
        Assert.Equal(100, ImageInfo.TryRead(p)!.Width);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemoveLocation_removes_gps_and_keeps_rest(bool le)
    {
        var orig = TestImages.Jpeg(100, 50, TestImages.PhotoTiff(le, orientation: 6));
        var p = Write(orig);
        Assert.Null(ExifEdit.RemoveLocation(p));
        var after = File.ReadAllBytes(p);
        Assert.Equal(orig.Length, after.Length);
        Assert.Equal(WithoutApp1(orig), WithoutApp1(after));
        var i = ImageInfo.TryRead(p)!;
        Assert.False(i.Exif!.HasLocation);
        Assert.Equal((6, "ACME", 400), (i.Orientation, i.Exif.Make, i.Exif.Iso));
        // no GPS numbers left anywhere in the file: tag 0x8825 and the GPS refs/rationals are gone
        var s = System.Text.Encoding.Latin1.GetString(after);
        Assert.DoesNotContain("\u0088%", s);
        Assert.DoesNotContain("%\u0088", s);
        Assert.Null(ExifEdit.RemoveLocation(p)); // again: nothing to do
    }

    [Fact]
    public void RemoveLocation_without_exif_or_gps_changes_nothing()
    {
        var a = Write(TestImages.Jpeg(10, 10), "a.jpg");
        var b = Write(TestImages.Jpeg(10, 10, TestImages.PhotoTiff(true, gps: false)), "b.jpg");
        var ba = File.ReadAllBytes(a); var bb = File.ReadAllBytes(b);
        Assert.Null(ExifEdit.RemoveLocation(a)); Assert.Null(ExifEdit.RemoveLocation(b));
        Assert.Equal(ba, File.ReadAllBytes(a)); Assert.Equal(bb, File.ReadAllBytes(b));
    }

    [Fact]
    public void Refuses_bad_input_and_leaves_file_untouched()
    {
        var png = Write(TestImages.Png(4, 4), "x.png");
        var text = Write(System.Text.Encoding.ASCII.GetBytes("not an image at all"), "t.jpg");
        var badExif = Write(TestImages.Jpeg(5, 5, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }), "bad.jpg");
        var cyc = Write(TestImages.Jpeg(5, 5, TestImages.Tiff(true, new[] { new[] { TestImages.Ptr(0x8825, 0) } })), "cyc.jpg");
        var trunc = Write(TestImages.Jpeg(5, 5, TestImages.PhotoTiff(true))[..30], "tr.jpg");
        foreach (var f in new[] { png, text, badExif, trunc })
        {
            var before = File.ReadAllBytes(f);
            Assert.NotNull(ExifEdit.SetOrientation(f, 6));
            Assert.Equal(before, File.ReadAllBytes(f));
        }
        Assert.NotNull(ExifEdit.RemoveLocation(cyc));
        Assert.NotNull(ExifEdit.RemoveLocation(png));
        Assert.NotNull(ExifEdit.SetOrientation(Path.Combine(_d, "missing.jpg"), 3));
        Assert.NotNull(ExifEdit.SetOrientation(Write(TestImages.Jpeg(5, 5)), 9));
    }

    [Fact]
    public void Rotate_cycles_and_keeps_mirroring()
    {
        int o = 1;
        var seen = new List<int>();
        for (int i = 0; i < 4; i++) { o = ExifEdit.Rotate(o, true); seen.Add(o); }
        Assert.Equal(new[] { 6, 3, 8, 1 }, seen);
        Assert.Equal(8, ExifEdit.Rotate(1, false));
        Assert.Equal(6, ExifEdit.Rotate(3, false));
        for (int c = 1; c <= 8; c++)
        {
            Assert.Equal(c, ExifEdit.Rotate(ExifEdit.Rotate(c, true), false));
            bool mirrored = c is 2 or 4 or 5 or 7;
            int r = ExifEdit.Rotate(c, true);
            Assert.Equal(mirrored, r is 2 or 4 or 5 or 7);
            Assert.NotEqual(c is >= 5 and <= 8, r is >= 5 and <= 8); // a quarter turn swaps width/height
        }
        Assert.Equal(6, ExifEdit.Rotate(0, true)); // junk counts as 1
    }
}
