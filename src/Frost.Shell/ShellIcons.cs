using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Frost.Shell;

public static class ShellIcons
{
    private static readonly ConcurrentDictionary<(string, bool, bool), ImageSource> Cache = new();
    private static readonly HashSet<string> PerPathExt = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".ico", ".lnk", ".url", ".scr", ".cur", ".ani", ".msi" };
    private static readonly Lazy<ImageSource> Blank = new(() =>
    {
        var b = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        b.Freeze();
        return b;
    });

    public static ImageSource Get(string path, bool isDirectory, bool large)
    {
        try
        {
            string ext = isDirectory ? "" : Path.GetExtension(path);
            bool isRoot = isDirectory && path.Length <= 3;
            bool perPath = isRoot || (!isDirectory && PerPathExt.Contains(ext));
            var key = (perPath ? path : (isDirectory ? "\0dir" : ext.ToLowerInvariant()), isDirectory, large);
            if (Cache.TryGetValue(key, out var hit)) return hit;

            // By-attribute lookup never touches the disk; per-path lookups do (custom icons).
            var icon = perPath ? Load(path, isDirectory, large, false) ?? Load(path, isDirectory, large, true)
                               : Load(isDirectory ? "folder" : "x" + ext, isDirectory, large, true);
            if (icon == null) return Blank.Value;
            return Cache.GetOrAdd(key, icon);
        }
        catch { return Blank.Value; }
    }

    private static ImageSource? Load(string name, bool isDir, bool large, bool useAttrs)
    {
        uint flags = useAttrs ? Native.SHGFI_USEFILEATTRIBUTES : 0;
        uint attrs = isDir ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL;
        var fi = new Native.SHFILEINFO();
        uint cb = (uint)Marshal.SizeOf<Native.SHFILEINFO>();
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            if (large)
            {
                // 48px extra-large system image list.
                if (Native.SHGetFileInfo(name, attrs, ref fi, cb, flags | Native.SHGFI_SYSICONINDEX) != IntPtr.Zero)
                    hIcon = ExtraLargeIcon(fi.iIcon);
            }
            if (hIcon == IntPtr.Zero)
            {
                fi = new Native.SHFILEINFO();
                Native.SHGetFileInfo(name, attrs, ref fi, cb, flags | Native.SHGFI_ICON | (large ? 0u : Native.SHGFI_SMALLICON));
                hIcon = fi.hIcon;
            }
            if (hIcon == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { if (hIcon != IntPtr.Zero) Native.DestroyIcon(hIcon); }
    }

    private static IntPtr ExtraLargeIcon(int index)
    {
        IntPtr ppv = IntPtr.Zero;
        try
        {
            var iid = Native.IID_IImageList;
            if (Native.SHGetImageList(2 /*SHIL_EXTRALARGE*/, ref iid, out ppv) < 0 || ppv == IntPtr.Zero) return IntPtr.Zero;
            var list = (IImageList)Marshal.GetObjectForIUnknown(ppv);
            try { return list.GetIcon(index, 1 /*ILD_TRANSPARENT*/, out var h) >= 0 ? h : IntPtr.Zero; }
            finally { Marshal.ReleaseComObject(list); }
        }
        catch { return IntPtr.Zero; }
        finally { if (ppv != IntPtr.Zero) Marshal.Release(ppv); }
    }

    // ---- thumbnails: small pool of STA workers ----
    private sealed record Job(string Path, int Size, CancellationToken Ct, TaskCompletionSource<ImageSource?> Tcs);
    private static readonly System.Collections.Concurrent.BlockingCollection<Job> Queue = new();
    private static int _started;

    private static void EnsureWorkers()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        for (int i = 0; i < 3; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = "Frost.Thumbs" + i };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    private static void Worker()
    {
        foreach (var job in Queue.GetConsumingEnumerable())
        {
            if (job.Ct.IsCancellationRequested) { job.Tcs.TrySetCanceled(job.Ct); continue; }
            try { job.Tcs.TrySetResult(Thumb(job.Path, job.Size)); }
            catch { job.Tcs.TrySetResult(null); }
        }
    }

    public static Task<ImageSource?> ThumbnailAsync(string path, int sizePx, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<ImageSource?>(ct);
        EnsureWorkers();
        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(new Job(path, sizePx, ct, tcs));
        return tcs.Task;
    }

    private static ImageSource? Thumb(string path, int size)
    {
        IntPtr ppv = IntPtr.Zero, hbm = IntPtr.Zero;
        IShellItemImageFactory? f = null;
        try
        {
            var iid = Native.IID_IShellItemImageFactory;
            if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out ppv) < 0 || ppv == IntPtr.Zero) return null;
            f = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ppv);
            // SIIGBF_BIGGERSIZEOK (1) | SIIGBF_THUMBNAILONLY (8)
            if (f.GetImage(new Native.SIZE { cx = size, cy = size }, 1 | 8, out hbm) < 0 || hbm == IntPtr.Zero) return null;
            return FromHBitmap(hbm);
        }
        finally
        {
            if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
            if (f != null) Marshal.ReleaseComObject(f);
            if (ppv != IntPtr.Zero) Marshal.Release(ppv);
        }
    }

    private static ImageSource? FromHBitmap(IntPtr hbm)
    {
        if (Native.GetObject(hbm, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
        int w = bm.bmWidth, h = bm.bmHeight;
        var bmi = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var px = new byte[w * h * 4];
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        try { if (Native.GetDIBits(dc, hbm, 0, (uint)h, px, ref bmi, 0) == 0) return null; }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }

        // Opaque formats (e.g. JPEG) come back with alpha 0 everywhere: force opaque.
        bool anyAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        src.Freeze();
        return src;
    }
}
