using Frost.Core;

namespace Frost.Core.Tests;

public sealed class ImageInfoTests
{
    private static ImageInfo? Read(byte[] b) => ImageInfo.TryRead(new MemoryStream(b));

    [Fact]
    public void Extensions()
    {
        foreach (var e in new[] { "jpg", ".JPEG", ".png", "Gif", "bmp", "webp", ".tif", "tiff", "heic", "heif", ".avif" }) Assert.True(ImageInfo.IsImageExtension(e), e);
        foreach (var e in new[] { "txt", ".mp4", "", "." }) Assert.False(ImageInfo.IsImageExtension(e), e);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Jpeg_with_full_exif(bool le)
    {
        var i = Read(TestImages.Jpeg(4000, 3000, TestImages.PhotoTiff(le, orientation: 6)))!;
        Assert.Equal(ImageFormat.Jpeg, i.Format);
        Assert.Equal((4000, 3000, 6), (i.Width, i.Height, i.Orientation));
        Assert.True(i.IsRotated90);
        Assert.Equal((3000, 4000), (i.DisplayWidth, i.DisplayHeight));
        Assert.False(i.HasAlpha);
        var x = i.Exif!;
        Assert.Equal("ACME", x.Make); Assert.Equal("Cam 1", x.Model); Assert.Equal("Lens 26mm", x.Lens);
        Assert.Equal(new DateTime(2024, 5, 17, 14, 30, 5), x.Taken);
        Assert.Equal(1 / 120.0, x.ExposureSeconds!.Value, 6);
        Assert.Equal(1.8, x.FNumber!.Value, 6);
        Assert.Equal(400, x.Iso); Assert.Equal(26, x.FocalLengthMm);
        Assert.True(x.HasLocation);
        Assert.Equal(52 + 13 / 60.0 + 36 / 3600.0, x.Latitude!.Value, 6);
        Assert.Equal(21 + 43.92 / 3600.0, x.Longitude!.Value, 6);
    }

    [Fact]
    public void Gps_south_west_is_negative()
    {
        var x = Read(TestImages.Jpeg(10, 10, TestImages.PhotoTiff(false, latRef: "S", lonRef: "W")))!.Exif!;
        Assert.True(x.Latitude < 0 && x.Longitude < 0);
    }

    [Fact]
    public void Jpeg_without_exif_and_with_jfif()
    {
        var i = Read(TestImages.Jpeg(640, 480, jfif: true))!;
        Assert.Equal((640, 480, 1), (i.Width, i.Height, i.Orientation));
        Assert.Null(i.Exif);
    }

    [Fact]
    public void Png_variants()
    {
        Assert.False(Read(TestImages.Png(30, 20))!.HasAlpha);
        Assert.True(Read(TestImages.Png(30, 20, colorType: 6))!.HasAlpha);
        Assert.True(Read(TestImages.Png(30, 20, trns: true))!.HasAlpha);
        var a = Read(TestImages.Png(30, 20, frames: 5))!;
        Assert.Equal((ImageFormat.Png, 30, 20, 5), (a.Format, a.Width, a.Height, a.Frames));
    }

    [Fact]
    public void Gif_counts_frames_and_transparency()
    {
        var g = Read(TestImages.Gif(16, 9, 3, transparent: true))!;
        Assert.Equal((ImageFormat.Gif, 16, 9, 3, true), (g.Format, g.Width, g.Height, g.Frames, g.HasAlpha));
        Assert.False(Read(TestImages.Gif(2, 2, 1))!.HasAlpha);
    }

    [Fact]
    public void Bmp()
    {
        var b = Read(TestImages.Bmp(100, 50, 32))!;
        Assert.Equal((ImageFormat.Bmp, 100, 50, true), (b.Format, b.Width, b.Height, b.HasAlpha));
        Assert.False(Read(TestImages.Bmp(100, 50))!.HasAlpha);
    }

    [Fact]
    public void WebP_all_kinds()
    {
        var l = Read(TestImages.WebPLossy(321, 123))!;
        Assert.Equal((ImageFormat.WebP, 321, 123, false), (l.Format, l.Width, l.Height, l.HasAlpha));
        var ll = Read(TestImages.WebPLossless(500, 400, alpha: true))!;
        Assert.Equal((500, 400, true), (ll.Width, ll.Height, ll.HasAlpha));
        var x = Read(TestImages.WebPExtended(1000, 700, alpha: true, frames: 4))!;
        Assert.Equal((1000, 700, true, 4), (x.Width, x.Height, x.HasAlpha, x.Frames));
        var e = Read(TestImages.WebPExtended(10, 10, false, 0, TestImages.PhotoTiff(true, orientation: 3)))!;
        Assert.Equal(3, e.Orientation);
        Assert.Equal("ACME", e.Exif!.Make);
    }

    [Fact]
    public void Tiff_with_exif()
    {
        var ifd0 = new[] { new TestImages.E(0x0100, 4, TestImages.U32(800, true), 1), new TestImages.E(0x0101, 4, TestImages.U32(600, true), 1), TestImages.Short(0x0112, 8, true), TestImages.Ascii(0x010F, "Scan") };
        var i = Read(TestImages.Tiff(true, new[] { ifd0 }))!;
        Assert.Equal((ImageFormat.Tiff, 800, 600, 8), (i.Format, i.Width, i.Height, i.Orientation));
        Assert.Equal("Scan", i.Exif!.Make);
    }

    [Fact]
    public void Heic_and_avif()
    {
        var h = Read(TestImages.Heif("heic", 4032, 3024, irot: 3, tiff: TestImages.PhotoTiff(false)))!;
        Assert.Equal((ImageFormat.Heic, 4032, 3024, 6), (h.Format, h.Width, h.Height, h.Orientation));
        Assert.Equal("Cam 1", h.Exif!.Model);
        var a = Read(TestImages.Heif("avif", 64, 32))!;
        Assert.Equal((ImageFormat.Avif, 64, 32, 1), (a.Format, a.Width, a.Height, a.Orientation));
    }

    [Fact]
    public void Cyclic_ifd_does_not_hang()
    {
        var t = TestImages.Tiff(true, new[] { new[] { TestImages.Ascii(0x010F, "X") } }, cycleBack: true);
        var i = Read(TestImages.Jpeg(5, 5, t))!;
        Assert.Equal("X", i.Exif!.Make);
        // exif pointer to IFD0 itself
        var self = TestImages.Tiff(false, new[] { new[] { TestImages.Ptr(0x8769, 0), TestImages.Ascii(0x010F, "Y") } });
        Assert.Equal("Y", Read(TestImages.Jpeg(5, 5, self))!.Exif!.Make);
    }

    [Fact]
    public void Truncated_and_garbage_never_throw()
    {
        var all = new[]
        {
            TestImages.Jpeg(100, 80, TestImages.PhotoTiff(true)), TestImages.Png(5, 5, frames: 2), TestImages.Gif(4, 4, 2), TestImages.Bmp(8, 8),
            TestImages.WebPExtended(9, 9, true, 2, TestImages.PhotoTiff(false)), TestImages.Heif("heic", 10, 10, 1, TestImages.PhotoTiff(true)),
            TestImages.Tiff(false, new[] { new[] { TestImages.Short(0x0112, 3, false) } }),
        };
        foreach (var f in all)
            for (int n = 0; n < f.Length; n++) { var _ = Read(f[..n]); } // any prefix: no exception
        var rnd = new Random(1);
        foreach (var f in all)
            for (int k = 0; k < 300; k++)
            {
                var c = (byte[])f.Clone();
                for (int j = 0; j < 4; j++) c[rnd.Next(c.Length)] = (byte)rnd.Next(256);
                var _ = Read(c);
            }
        Assert.Null(Read(new byte[100]));
        Assert.Null(Read(System.Text.Encoding.ASCII.GetBytes("hello world, this is text")));
        Assert.Null(ImageInfo.TryRead("/nonexistent/file.jpg"));
        Assert.Null(Read(TestImages.Jpeg(100, 80)[..20])); // cut before SOF
    }

    [Fact]
    public void Reads_from_path_and_stays_small()
    {
        string f = Path.Combine(Path.GetTempPath(), "frost-img-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            File.WriteAllBytes(f, TestImages.Jpeg(1200, 800));
            Assert.Equal(1200, ImageInfo.TryRead(f)!.Width);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Format_helpers()
    {
        Assert.Equal("12.2 MP", MediaFormat.Megapixels(4000, 3050));
        Assert.Equal("1/120 s", MediaFormat.Exposure(1 / 120.0));
        Assert.Equal("2.5 s", MediaFormat.Exposure(2.5));
        Assert.Equal("30 s", MediaFormat.Exposure(30));
        Assert.Equal("f/1.8", MediaFormat.FNumber(1.8));
        Assert.Equal("f/2", MediaFormat.FNumber(2));
        Assert.Equal("26 mm", MediaFormat.FocalLength(26));
        Assert.Equal("52.2297, 21.0122", MediaFormat.Location(52.2297, 21.0122));
        Assert.Equal("-33.8688, -70.0000", MediaFormat.Location(-33.8688, -70));
        Assert.Equal("WebP", MediaFormat.FormatName(ImageFormat.WebP));
        Assert.Equal("HEIC", MediaFormat.FormatName(ImageFormat.Heic));
        Assert.Equal("JPEG", MediaFormat.FormatName(ImageFormat.Jpeg));
    }

    [Fact]
    public void Format_helpers_ignore_culture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pl-PL");
            Assert.Equal("f/1.8", MediaFormat.FNumber(1.8));
            Assert.Equal("52.2297, 21.0122", MediaFormat.Location(52.2297, 21.0122));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }
}
