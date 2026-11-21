namespace Frost.Core;

// ============================================================================
// Frost 0.4.0 Core contract. Signatures are fixed: the Windows and Linux UIs are
// built against them in parallel. Bodies are implemented (with tests) by the Core task.
// ============================================================================

public enum ImageFormat { Unknown, Jpeg, Png, Gif, Bmp, WebP, Tiff, Heic, Avif }

/// <summary>Photo metadata from EXIF. Any field can be missing.</summary>
public sealed record ExifData(
    DateTime? Taken, string? Make, string? Model, string? Lens,
    double? ExposureSeconds, double? FNumber, int? Iso, double? FocalLengthMm,
    double? Latitude, double? Longitude)
{
    public bool HasLocation => Latitude != null && Longitude != null;
}

/// <summary>What Frost shows about a picture. Read from the header only, never the whole file.</summary>
public sealed record ImageInfo(ImageFormat Format, int Width, int Height, int Orientation, int Frames, bool HasAlpha, ExifData? Exif)
{
    /// <summary>EXIF orientation 5–8 swaps width and height when displayed.</summary>
    public bool IsRotated90 => Orientation is >= 5 and <= 8;
    public int DisplayWidth => IsRotated90 ? Height : Width;
    public int DisplayHeight => IsRotated90 ? Width : Height;
    public double Megapixels => Width * (double)Height / 1_000_000;

    /// <summary>True for extensions Frost treats as pictures (jpg jpeg png gif bmp webp tif tiff heic heif avif), case-insensitive, with or without the dot.</summary>
    public static bool IsImageExtension(string extension) => ImageReader.IsImageExtension(extension);

    /// <summary>Reads format, size, orientation (1 = normal), frame count, alpha and EXIF. Null when not a picture or unreadable; never throws.</summary>
    public static ImageInfo? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryRead(fs);
        }
        catch (Exception) { return null; }
    }

    public static ImageInfo? TryRead(Stream stream)
    {
        try { return ImageReader.Read(stream); }
        catch (Exception) { return null; }
    }
}

/// <summary>Text for the info pane: "12.2 MP", "1/120 s", "f/1.8", "26 mm", "52.2297, 21.0122".</summary>
public static class MediaFormat
{
    public static string Megapixels(int width, int height) => (width * (double)height / 1_000_000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MP";
    public static string Exposure(double seconds) => seconds <= 0 ? "0 s" : seconds < 1 ? "1/" + Math.Round(1 / seconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s" : seconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s";
    public static string FNumber(double f) => "f/" + f.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    public static string FocalLength(double mm) => mm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " mm";
    public static string Location(double latitude, double longitude) => latitude.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ", " + longitude.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
    public static string FormatName(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "JPEG", ImageFormat.Png => "PNG", ImageFormat.Gif => "GIF", ImageFormat.Bmp => "BMP",
        ImageFormat.WebP => "WebP", ImageFormat.Tiff => "TIFF", ImageFormat.Heic => "HEIC", ImageFormat.Avif => "AVIF", _ => "Unknown",
    };
}

/// <summary>Lossless JPEG metadata edits: only the EXIF (APP1) segment changes, image data stays byte-identical.
/// Each writes to a temp file and swaps it in. Returns null on success or an error message; on error the file is untouched.</summary>
public static class ExifEdit
{
    /// <summary>Sets EXIF orientation (1–8). Adds a minimal EXIF block when the JPEG has none.</summary>
    public static string? SetOrientation(string jpegPath, int orientation) => ExifWriter.SetOrientation(jpegPath, orientation);

    /// <summary>Removes the GPS IFD (location) and its pointer.</summary>
    public static string? RemoveLocation(string jpegPath) => ExifWriter.RemoveLocation(jpegPath);

    /// <summary>Orientation after rotating 90° clockwise (or counter-clockwise) from <paramref name="current"/>, keeping any mirroring.</summary>
    public static int Rotate(int current, bool clockwise) => ExifWriter.Rotate(current, clockwise);
}

/// <summary>Backups that make in-place edits undoable (Frost's cache folder, cleaned when older than 7 days).</summary>
public static class EditBackups
{
    /// <summary>Copies <paramref name="path"/> into the backup folder and returns the copy's path.</summary>
    public static string Create(string path) => Scans.Backup(path);

    /// <summary>Folder used for backups: Windows %LocalAppData%\Frost\undo, Linux $XDG_CACHE_HOME/frost/undo.</summary>
    public static string Folder => Scans.BackupFolder;
}

/// <summary>Main colours of a picture, for the palette swatches.</summary>
public static class ColorPalette
{
    /// <summary><paramref name="argb"/> are pixels of a small downscaled copy (e.g. 64×64), 0xAARRGGBB.
    /// Returns up to <paramref name="count"/> colours, most common first, transparent pixels ignored, near-duplicates merged.</summary>
    public static IReadOnlyList<uint> Extract(ReadOnlySpan<uint> argb, int count = 5) => PaletteImpl.Extract(argb, count);

    /// <summary>"#RRGGBB".</summary>
    public static string Hex(uint argb) => "#" + (argb & 0xFFFFFF).ToString("X6");
}

/// <summary>Folder size tree for the space analyzer.</summary>
public sealed record SizeNode(string Path, string Name, bool IsDirectory, long Size, IReadOnlyList<SizeNode> Children, bool Unreadable);

public static class SpaceScan
{
    /// <summary>Sizes of everything under <paramref name="root"/>, children sorted biggest first. Doesn't follow symlinks/junctions or cross into other filesystems.</summary>
    public static Task<SizeNode> ScanAsync(string root, IProgress<OperationProgress>? progress, CancellationToken ct) => Scans.Space(root, progress, ct);
}

public sealed record BigFile(string Path, long Size, DateTime Modified);

/// <summary>Live state of one drive in the biggest-files scan.</summary>
public sealed record DriveScan(string Root, IReadOnlyList<BigFile> Top, long FilesScanned, IReadOnlyList<string> Unreadable, bool Done);

public static class BiggestFiles
{
    /// <summary>Scans every root in parallel; reports each drive's partial top list via <paramref name="progress"/> (about every 250 ms and when it finishes).
    /// Stays on each root's filesystem, skips symlinks/junctions and <see cref="IsExcluded"/> paths.</summary>
    public static Task<IReadOnlyList<DriveScan>> ScanAsync(IReadOnlyList<string> roots, int topN, IProgress<DriveScan>? progress, CancellationToken ct) => Scans.Biggest(roots, topN, progress, ct);

    /// <summary>The biggest <paramref name="topN"/> across several drive results, biggest first.</summary>
    public static IReadOnlyList<BigFile> Combine(IEnumerable<DriveScan> drives, int topN) => drives.SelectMany(d => d.Top).OrderByDescending(f => f.Size).ThenBy(f => f.Path, StringComparer.Ordinal).Take(Math.Max(topN, 0)).ToList();

    /// <summary>System/virtual paths never scanned: /proc /sys /dev /run (except /run/media) /snap; Windows: $Recycle.Bin, System Volume Information.</summary>
    public static bool IsExcluded(string path) => Scans.IsExcluded(path);
}

public sealed record DuplicateGroup(long Size, IReadOnlyList<string> Paths);

public static class Duplicates
{
    /// <summary>Identical files under <paramref name="root"/> (size, then first-64-KB hash, then full SHA-256). Empty files ignored. Biggest wasted space first.</summary>
    public static Task<IReadOnlyList<DuplicateGroup>> FindAsync(string root, IProgress<OperationProgress>? progress, CancellationToken ct) => Scans.Dupes(root, progress, ct);
}
